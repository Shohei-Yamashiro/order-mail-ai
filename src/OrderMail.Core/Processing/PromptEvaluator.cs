using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Analysis;
using OrderMail.Core.Catalog;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Processing;

public sealed record EvaluationRow(
    int MailId,
    string Subject,
    OrderExtraction Expected,
    OrderExtraction? Actual,
    string? Error,
    bool Auto,
    long ElapsedMs)
{
    public bool IsOrderOk => Actual is not null && Actual.IsOrder == Expected.IsOrder;

    /// <summary>Same number of lines with the same quantities and units; product names are not compared because notation varies.</summary>
    public bool ItemsOk => IsOrderOk && Lines(Actual!).SequenceEqual(Lines(Expected));

    public bool CompanyOk => IsOrderOk && SameText(Actual!.CustomerCompany, Expected.CustomerCompany);
    public bool DateOk => IsOrderOk && Actual!.DeliveryDate == Expected.DeliveryDate;

    /// <summary>Only judged when the confirmed answer has product codes.</summary>
    public bool HasCodes => Expected.IsOrder && Expected.Items.Any(i => i.ProductCode is not null);

    /// <summary>The same set of product codes as confirmed.</summary>
    public bool CodesOk => IsOrderOk && Codes(Actual!).SequenceEqual(Codes(Expected));

    /// <summary>Would have been registered or dismissed without review, but wrongly.</summary>
    public bool WrongAuto => Auto && !IsOrderOk;

    public bool AllOk => IsOrderOk && (!Expected.IsOrder || (ItemsOk && CompanyOk && DateOk && (!HasCodes || CodesOk)));

    private static IEnumerable<string> Codes(OrderExtraction e) =>
        e.Items.Select(i => Normalize(i.ProductCode).ToUpperInvariant()).Order(StringComparer.Ordinal);

    private static IEnumerable<string> Lines(OrderExtraction e) =>
        e.Items.Select(i => $"{i.Quantity:0.###} {Normalize(i.Unit)}").Order(StringComparer.Ordinal);

    private static bool SameText(string? a, string? b) => Normalize(a) == Normalize(b);

    private static string Normalize(string? s) =>
        new((s ?? "").Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)).ToArray());
}

public sealed record EvaluationReport(string Analyzer, int ExampleCount, IReadOnlyList<EvaluationRow> Rows)
{
    public int Total => Rows.Count;
    public int Orders => Rows.Count(r => r.Expected.IsOrder);
    public int Errors => Rows.Count(r => r.Error is not null);
    public int IsOrderOk => Rows.Count(r => r.IsOrderOk);
    public int ItemsOk => Rows.Count(r => r.Expected.IsOrder && r.ItemsOk);
    public int CompanyOk => Rows.Count(r => r.Expected.IsOrder && r.CompanyOk);
    public int DateOk => Rows.Count(r => r.Expected.IsOrder && r.DateOk);
    public int WithCodes => Rows.Count(r => r.HasCodes);
    public int CodesOk => Rows.Count(r => r.HasCodes && r.CodesOk);
    public int Auto => Rows.Count(r => r.Auto);
    public int WrongAuto => Rows.Count(r => r.WrongAuto);
    public double AverageSeconds => Rows.Count == 0 ? 0 : Rows.Average(r => r.ElapsedMs) / 1000.0;
}

/// <summary>
/// Runs the given (possibly unsaved) settings over mails a person has confirmed and compares the results,
/// so prompt changes can be measured before saving. Nothing is written to the database.
/// </summary>
public sealed class PromptEvaluator(IDbContextFactory<OrderMailDbContext> dbFactory, AnalyzerFactory analyzers, ProductCatalogService catalogs)
{
    public async Task<EvaluationReport> EvaluateAsync(AppSettings settings, int maxMails, IProgress<string>? progress, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mails = await db.Mails
            .AsNoTracking()
            .Include(m => m.Order).ThenInclude(o => o!.Items)
            .Where(m => m.ReviewedAt != null && (m.Status == MailStatus.NotOrder || (m.Status == MailStatus.Registered && m.Order != null)))
            .OrderByDescending(m => m.ReviewedAt)
            .Take(maxMails)
            .ToListAsync(ct);

        var pipeline = new AnalysisPipeline(analyzers.Create(settings.Ai), await catalogs.OpenAsync(db, settings, ct), settings);
        var exampleCount = settings.Ai.Provider == AiProvider.OpenAiCompatible ? settings.Ai.ExampleCount : 0;
        var rows = new List<EvaluationRow>();
        foreach (var mail in mails)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"評価中 {rows.Count + 1}/{mails.Count}");
            var expected = ExampleSelector.ConfirmedResult(mail);
            var watch = Stopwatch.StartNew();
            try
            {
                var actual = (await pipeline.AnalyzeAsync(db, mail, ct)).Result;
                var auto = RegistrationPolicy.Decide(actual, settings.AutoRegisterThreshold) != MailStatus.NeedsReview;
                rows.Add(new EvaluationRow(mail.Id, mail.Subject, expected, actual, null, auto, watch.ElapsedMilliseconds));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                rows.Add(new EvaluationRow(mail.Id, mail.Subject, expected, null, e.Message, false, watch.ElapsedMilliseconds));
            }
        }
        return new EvaluationReport(pipeline.Analyzer.Name, exampleCount, rows);
    }
}
