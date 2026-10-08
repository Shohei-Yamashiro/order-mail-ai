using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderMail.Core;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;
using OrderMail.Core.Mail;
using OrderMail.Core.Processing;
using OrderMail.Core.Settings;

namespace OrderMail.Tests;

public sealed class ProcessingTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ordermail-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public ProcessingTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrderMailCore(_dataDir, AppContext.BaseDirectory);
        _services = services.BuildServiceProvider();

        var store = _services.GetRequiredService<SettingsStore>();
        var settings = store.Current;
        settings.Mail.EmlFolder = Samples.MailFolder;
        store.Save(settings);

        using var db = Db();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dataDir, recursive: true);
    }

    private OrderMailDbContext Db() => _services.GetRequiredService<IDbContextFactory<OrderMailDbContext>>().CreateDbContext();

    [Fact]
    public async Task Run_imports_samples_once_and_sorts_them_by_confidence()
    {
        var processor = _services.GetRequiredService<MailProcessor>();

        var first = await processor.RunAsync();
        var second = await processor.RunAsync();

        Assert.Null(first.Error);
        Assert.Equal(Samples.Labels.Count, first.Imported);
        Assert.Equal(Samples.Labels.Count, first.Analyzed);
        Assert.Equal(0, second.Imported);
        Assert.Equal(0, second.Analyzed);

        await using var db = Db();
        var mails = await db.Mails.Include(m => m.Order).ThenInclude(o => o!.Items).ToListAsync();
        Assert.DoesNotContain(mails, m => m.Status is MailStatus.Unanalyzed or MailStatus.Failed);
        Assert.All(mails.Where(m => m.Status == MailStatus.Registered), m =>
        {
            Assert.Equal(RegisteredBy.Auto, m.Order!.RegisteredBy);
            Assert.NotEmpty(m.Order.Items);
        });
        Assert.All(mails.Where(m => m.Status != MailStatus.Registered), m => Assert.Null(m.Order));
        Assert.Contains(mails, m => m.Status == MailStatus.NeedsReview);
        Assert.Equal(mails.Count, await db.Analyses.CountAsync());
    }

    [Fact]
    public async Task Review_decisions_survive_reanalysis()
    {
        var processor = _services.GetRequiredService<MailProcessor>();
        var review = _services.GetRequiredService<ReviewService>();
        await processor.RunAsync();

        int reviewId, dismissId;
        await using (var db = Db())
        {
            reviewId = await db.Mails.Where(m => m.Status == MailStatus.NeedsReview).Select(m => m.Id).FirstAsync();
            dismissId = await db.Mails.Where(m => m.Status == MailStatus.Registered).Select(m => m.Id).FirstAsync();
        }

        var draft = new OrderDraft { CustomerCompany = " ひなたデザイン ", Items = [new() { ProductName = "ボールペン", Quantity = 50, Unit = "本" }] };
        await review.ConfirmOrderAsync(reviewId, draft);
        await review.MarkNotOrderAsync(dismissId);
        await processor.ReanalyzeAsync(reviewId);
        await processor.ReanalyzeAsync(dismissId);

        await using var check = Db();
        var confirmed = await check.Mails.Include(m => m.Order).ThenInclude(o => o!.Items).SingleAsync(m => m.Id == reviewId);
        Assert.Equal(MailStatus.Registered, confirmed.Status);
        Assert.True(confirmed.IsReviewed);
        Assert.Equal(RegisteredBy.Human, confirmed.Order!.RegisteredBy);
        Assert.Equal("ひなたデザイン", confirmed.Order.CustomerCompany);
        Assert.Equal("ボールペン", Assert.Single(confirmed.Order.Items).ProductName);

        var dismissed = await check.Mails.Include(m => m.Order).SingleAsync(m => m.Id == dismissId);
        Assert.Equal(MailStatus.NotOrder, dismissed.Status);
        Assert.Null(dismissed.Order);
        Assert.Equal(2, await check.Analyses.CountAsync(a => a.ReceivedMailId == dismissId));
    }

    [Fact]
    public async Task Confirm_rejects_invalid_draft()
    {
        var processor = _services.GetRequiredService<MailProcessor>();
        await processor.RunAsync();
        await using var db = Db();
        var id = await db.Mails.Select(m => m.Id).FirstAsync();

        var e = await Assert.ThrowsAsync<ArgumentException>(() =>
            _services.GetRequiredService<ReviewService>().ConfirmOrderAsync(id, new OrderDraft { Items = [new() { ProductName = "", Quantity = 0 }] }));

        Assert.Contains("品名", e.Message);
        Assert.Contains("数量", e.Message);
    }

    [Fact]
    public async Task Failed_analysis_is_marked_and_retried_next_run()
    {
        var store = _services.GetRequiredService<SettingsStore>();
        var settings = store.Current;
        settings.Ai.Provider = AiProvider.OpenAiCompatible;
        settings.Ai.Endpoint = "http://127.0.0.1:9/v1";
        settings.Ai.TimeoutSeconds = 5;
        store.Save(settings);

        var folder = Directory.CreateDirectory(Path.Combine(_dataDir, "two-mails")).FullName;
        foreach (var file in Samples.Labels.Take(2).Select(l => l.File))
            File.Copy(Path.Combine(Samples.MailFolder, file), Path.Combine(folder, file));

        var processor = _services.GetRequiredService<MailProcessor>();
        await processor.ImportAsync(new EmlFolderMailSource(folder), CancellationToken.None);
        var (_, failed) = await processor.AnalyzePendingAsync(store.Current, CancellationToken.None);

        Assert.Equal(2, failed);
        await using var db = Db();
        Assert.All(await db.Mails.ToListAsync(), m =>
        {
            Assert.Equal(MailStatus.Failed, m.Status);
            Assert.Contains("接続", m.LastError);
        });

        settings.Ai.Provider = AiProvider.RuleBased;
        store.Save(settings);
        var (analyzed, _) = await processor.AnalyzePendingAsync(store.Current, CancellationToken.None);
        Assert.Equal(2, analyzed);
    }

    [Fact]
    public void Settings_encrypt_secrets_on_disk()
    {
        var path = Path.Combine(_dataDir, "secret-test.json");
        var store = new SettingsStore(path);
        var settings = store.Current;
        settings.Ai.ApiKey = "sk-very-secret";
        settings.Mail.ImapPassword = "p@ss";
        store.Save(settings);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("sk-very-secret", raw);
        Assert.DoesNotContain("p@ss", raw);

        var reloaded = new SettingsStore(path).Current;
        Assert.Equal("sk-very-secret", reloaded.Ai.ApiKey);
        Assert.Equal("p@ss", reloaded.Mail.ImapPassword);
    }

    [Fact]
    public async Task Eml_source_decodes_japanese_headers_and_html_bodies()
    {
        var mails = await new EmlFolderMailSource(Samples.MailFolder).FetchAsync(_ => false, CancellationToken.None);

        Assert.Equal(Samples.Labels.Count, mails.Count);
        var html = mails.Single(m => m.MessageId == "sample-07@midori-clinic.example");
        Assert.Equal("発注書送付（医療用消耗品）", html.Subject);
        Assert.Contains("サージカルマスク（50枚入） | 40 | 箱 | 480", html.Body);
        Assert.DoesNotContain("<", html.Body);
    }
}
