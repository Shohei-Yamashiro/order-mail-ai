using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;
using OrderMail.Core.Mail;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Processing;

public sealed record RunSummary(DateTime FinishedAtUtc, int Imported, int Analyzed, int Failed, string? Error);

/// <summary>Imports new mail and analyzes it. Runs one at a time; the UI subscribes to <see cref="StateChanged"/>.</summary>
public sealed class MailProcessor(
    IDbContextFactory<OrderMailDbContext> dbFactory,
    SettingsStore settingsStore,
    MailSourceFactory sources,
    AnalyzerFactory analyzers,
    TimeProvider time,
    ILogger<MailProcessor> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? StateChanged;

    public bool IsRunning { get; private set; }
    public string? Progress { get; private set; }
    public RunSummary? LastRun { get; private set; }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<RunSummary> RunAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IsRunning = true;
            Report("メールを取り込んでいます…");
            var settings = settingsStore.Current;
            int imported = 0, analyzed = 0, failed = 0;
            string? error = null;
            try
            {
                imported = await ImportAsync(sources.Create(settings.Mail), ct);
                (analyzed, failed) = await AnalyzePendingAsync(settings, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Mail processing failed");
                error = e.Message;
            }
            LastRun = new RunSummary(Now, imported, analyzed, failed, error);
            return LastRun;
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
            Report(null);
        }
    }

    public async Task<int> ImportAsync(IMailSource source, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = (await db.Mails.Select(m => m.MessageId).ToListAsync(ct)).ToHashSet();
        var fetched = await source.FetchAsync(known.Contains, ct);
        var now = Now;
        var count = 0;
        foreach (var f in fetched)
        {
            if (!known.Add(f.MessageId)) continue;
            db.Mails.Add(new ReceivedMail
            {
                MessageId = f.MessageId,
                Source = f.Source,
                FromAddress = f.FromAddress,
                FromName = f.FromName,
                To = f.To,
                Subject = f.Subject,
                Body = f.Body,
                ReceivedAt = f.ReceivedAtUtc,
                ImportedAt = now,
                Status = MailStatus.Unanalyzed,
            });
            count++;
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task<(int Analyzed, int Failed)> AnalyzePendingAsync(AppSettings settings, CancellationToken ct)
    {
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ids = await db.Mails
                .Where(m => m.Status == MailStatus.Unanalyzed || m.Status == MailStatus.Failed)
                .OrderBy(m => m.ReceivedAt)
                .Select(m => m.Id)
                .ToListAsync(ct);
        }

        var analyzer = analyzers.Create(settings.Ai);
        int analyzed = 0, failed = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            Report($"解析中 {i + 1}/{ids.Count}（{analyzer.Name}）");
            if (await AnalyzeOneAsync(ids[i], analyzer, settings.AutoRegisterThreshold, ct)) analyzed++;
            else failed++;
        }
        return (analyzed, failed);
    }

    public async Task<bool> ReanalyzeAsync(int mailId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IsRunning = true;
            var settings = settingsStore.Current;
            var analyzer = analyzers.Create(settings.Ai);
            Report($"再解析中（{analyzer.Name}）");
            return await AnalyzeOneAsync(mailId, analyzer, settings.AutoRegisterThreshold, ct);
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
            Report(null);
        }
    }

    private async Task<bool> AnalyzeOneAsync(int mailId, IOrderAnalyzer analyzer, double threshold, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mail = await db.Mails.Include(m => m.Order).SingleAsync(m => m.Id == mailId, ct);

        var watch = Stopwatch.StartNew();
        OrderExtraction result;
        try
        {
            result = await analyzer.AnalyzeAsync(MailInput.From(mail), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Analysis failed for mail {MailId}", mailId);
            if (!mail.IsReviewed) mail.Status = MailStatus.Failed;
            mail.LastError = e.Message;
            await db.SaveChangesAsync(ct);
            return false;
        }

        var now = Now;
        db.Analyses.Add(new MailAnalysis
        {
            ReceivedMailId = mail.Id,
            Analyzer = analyzer.Name,
            IsOrder = result.IsOrder,
            Confidence = result.Confidence,
            Reason = result.Reason,
            ResultJson = ExtractionJson.Serialize(result),
            ElapsedMs = watch.ElapsedMilliseconds,
            CreatedAt = now,
        });
        mail.LastError = null;
        mail.Confidence = result.Confidence;

        if (!mail.IsReviewed)
        {
            if (mail.Order is not null)
            {
                db.Orders.Remove(mail.Order);
                await db.SaveChangesAsync(ct);
                mail.Order = null;
            }
            mail.Status = RegistrationPolicy.Decide(result, threshold);
            if (mail.Status == MailStatus.Registered)
            {
                var order = new Order { RegisteredBy = RegisteredBy.Auto, CreatedAt = now, UpdatedAt = now };
                OrderDraft.From(result).ApplyTo(order);
                mail.Order = order;
            }
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private void Report(string? progress)
    {
        Progress = progress;
        StateChanged?.Invoke();
    }
}
