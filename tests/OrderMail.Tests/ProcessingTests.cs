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
    [Spec(SpecCategories.Processing, "サンプルを 1 回だけ取り込んで解析する", "サンプル 20 通を取り込んで全件解析し、2 回目は同じメールを取り込まない。自動登録した発注には明細があり、それ以外には発注データがない。要確認も含まれる。")]
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
    [Spec(SpecCategories.Processing, "人の確認は再解析で上書きされない", "人が確定した発注と「発注ではない」の判定は、再解析しても変わらない（解析履歴は増える）。")]
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
    [Spec(SpecCategories.Processing, "不正な発注内容は確定できない", "品名が空・数量 0 の明細では確定できず、品名と数量のエラーを返す。")]
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
    [Spec(SpecCategories.Processing, "解析の失敗を記録し、次回やり直す", "AI に接続できないとメールを「解析失敗」にして理由を残し、次の実行で解析し直す。")]
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

    private async Task<int> SampleIdAsync(string number)
    {
        await using var db = Db();
        return await db.Mails.Where(m => m.MessageId.StartsWith($"sample-{number}@")).Select(m => m.Id).SingleAsync();
    }

    [Fact]
    [Spec(SpecCategories.Examples, "手本は似た確認済みメールから選ぶ", "同じ差出人、同じドメイン、文面の似たものの順に選び、最も似たものを最後に並べる。解析するメール自身と未確認のメールは使わない。手本数 0 なら選ばない。")]
    public async Task Examples_are_confirmed_mails_most_similar_last()
    {
        await _services.GetRequiredService<MailProcessor>().RunAsync();
        var review = _services.GetRequiredService<ReviewService>();
        await review.ConfirmOrderAsync(await SampleIdAsync("01"), new OrderDraft { CustomerCompany = "あおぞら工業株式会社", Items = [new() { ProductName = "A4コピー用紙", Quantity = 10, Unit = "箱" }] });
        await review.MarkNotOrderAsync(await SampleIdAsync("11"));
        await review.MarkNotOrderAsync(await SampleIdAsync("12"));
        var targetId = await SampleIdAsync("08");
        await review.ConfirmOrderAsync(targetId, new OrderDraft { Items = [new() { ProductName = "クリアファイル(A4)", Quantity = 200, Unit = "枚" }] });

        await using var db = Db();
        var target = await db.Mails.SingleAsync(m => m.Id == targetId);

        var two = await ExampleSelector.SelectAsync(db, target, 2, CancellationToken.None);
        var all = await ExampleSelector.SelectAsync(db, target, 10, CancellationToken.None);

        // 01 has the same sender, 11 the same domain; 08 itself and unreviewed mails are never used.
        Assert.Equal(["経理部", "山田 太郎"], two.Select(e => e.Mail.FromName));
        Assert.Equal("あおぞら工業株式会社", two[1].Expected.CustomerCompany);
        Assert.Equal(new ExtractedItem("A4コピー用紙", 10, "箱", null), Assert.Single(two[1].Expected.Items));
        Assert.False(two[0].Expected.IsOrder);
        Assert.Equal(3, all.Count);
        Assert.Empty(await ExampleSelector.SelectAsync(db, target, 0, CancellationToken.None));
    }

    [Fact]
    [Spec(SpecCategories.Examples, "確認済みメールで評価する", "確定内容と比べて判定・明細・会社名・納期の一致数と自動確定数を数え、違ったメールを特定する。評価しても DB は変わらない。")]
    public async Task Evaluation_compares_with_confirmed_answers_without_changing_data()
    {
        await _services.GetRequiredService<MailProcessor>().RunAsync();
        var review = _services.GetRequiredService<ReviewService>();
        await review.ConfirmOrderAsync(await SampleIdAsync("01"), new OrderDraft
        {
            CustomerCompany = "あおぞら工業株式会社",
            DeliveryDate = new DateTime(2026, 10, 9),
            Items = [new() { ProductName = "A4コピー用紙", Quantity = 10, Unit = "箱" }, new() { ProductName = "トナー", Quantity = 2, Unit = "本" }],
        });
        await review.ConfirmOrderAsync(await SampleIdAsync("02"), new OrderDraft
        {
            CustomerCompany = "ひなたデザイン",
            Items = [new() { ProductName = "ボールペン", Quantity = 50, Unit = "本" }, new() { ProductName = "付箋", Quantity = 20, Unit = "個" }],
        });
        await review.MarkNotOrderAsync(await SampleIdAsync("13"));
        int analyses;
        await using (var db = Db()) analyses = await db.Analyses.CountAsync();

        var report = await _services.GetRequiredService<PromptEvaluator>().EvaluateAsync(new AppSettings(), 50, null, CancellationToken.None);

        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Orders);
        Assert.Equal(3, report.IsOrderOk);
        Assert.Equal(2, report.ItemsOk);
        Assert.Equal(1, report.CompanyOk);
        Assert.Equal(2, report.DateOk);
        Assert.Equal(2, report.Auto);
        Assert.Equal(0, report.WrongAuto);
        var miss = Assert.Single(report.Rows, r => !r.AllOk);
        Assert.Equal("ひなたデザイン", miss.Expected.CustomerCompany);
        await using (var db = Db()) Assert.Equal(analyses, await db.Analyses.CountAsync());
    }

    [Fact]
    [Spec(SpecCategories.Feedback, "担当者の修正を集計する", "確定した時点で表示されていた解析結果（確定より後の再解析は使わない）と確定内容を比べ、直した件数と項目ごとの一致数を解析器ごとに数える。手本の件数が違っても同じ解析器にまとめる。")]
    public async Task Feedback_compares_confirmed_answers_with_the_analysis_shown_at_review()
    {
        await _services.GetRequiredService<MailProcessor>().RunAsync();
        var review = _services.GetRequiredService<ReviewService>();
        var orderId = await SampleIdAsync("01");
        await review.ConfirmOrderAsync(orderId, new OrderDraft { CustomerCompany = "担当者が直した会社", Items = [new() { ProductName = "A4コピー用紙", Quantity = 10, Unit = "箱" }, new() { ProductName = "トナー", Quantity = 2, Unit = "本" }] });
        await review.MarkNotOrderAsync(await SampleIdAsync("13"));
        await using (var db = Db())
        {
            db.Analyses.Add(new MailAnalysis
            {
                ReceivedMailId = orderId,
                Analyzer = "AI（m）・手本3件",
                ResultJson = ExtractionJson.Serialize(new OrderExtraction { IsOrder = false, Confidence = 0.9 }),
                CreatedAt = DateTime.UtcNow.AddMinutes(5),
            });
            await db.SaveChangesAsync();
        }

        var report = await _services.GetRequiredService<FeedbackService>().BuildAsync(0.8);

        Assert.Equal(2, report.Overall.Total);
        Assert.Equal(2, report.Overall.IsOrderOk);
        Assert.Equal(0, report.Overall.CompanyOk);
        Assert.Equal(1, report.Corrected);
        Assert.Equal("ルールベース", Assert.Single(report.ByAnalyzer).Analyzer);
        Assert.Equal("AI（m）", FeedbackService.BaseName("AI（m）・手本3件"));
    }

    [Fact]
    [Spec(SpecCategories.Feedback, "ファインチューニング用の学習データを書き出す", "確定したメールを 1 行 1 件の JSONL（system・user・assistant）にし、指定した間隔で検証用に分ける。答えは確定内容で、件数が少ないと警告する。")]
    public async Task Fine_tuning_export_writes_chat_jsonl_from_confirmed_mails()
    {
        await _services.GetRequiredService<MailProcessor>().RunAsync();
        var review = _services.GetRequiredService<ReviewService>();
        await review.ConfirmOrderAsync(await SampleIdAsync("01"), new OrderDraft { CustomerCompany = "あおぞら工業株式会社", Items = [new() { ProductName = "A4コピー用紙", Quantity = 10, Unit = "箱" }] });
        await review.ConfirmOrderAsync(await SampleIdAsync("02"), new OrderDraft { CustomerCompany = "ひなたデザイン", Items = [new() { ProductName = "ボールペン", Quantity = 50, Unit = "本" }] });
        await review.MarkNotOrderAsync(await SampleIdAsync("13"));

        var export = await _services.GetRequiredService<FineTuningExporter>().ExportAsync(new AiSettings { OwnCompanyName = "サンプル商事" }, validationEvery: 3);

        Assert.Equal(2, export.TrainCount);
        Assert.Equal(1, export.ValidationCount);
        Assert.Contains(export.Warnings, w => w.Contains("10 件以上"));
        var lines = File.ReadAllLines(export.TrainPath);
        Assert.Equal(2, lines.Length);
        var messages = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Contains("「サンプル商事」", messages[0]!["content"]!.GetValue<string>());
        Assert.Contains("本文:", messages[1]!["content"]!.GetValue<string>());
        var answer = ExtractionJson.Parse(messages[2]!["content"]!.GetValue<string>());
        Assert.True(answer.IsOrder);
        Assert.Equal("あおぞら工業株式会社", answer.CustomerCompany);
        Assert.False(ExtractionJson.Parse(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllLines(export.ValidationPath!)[0])!["messages"]![2]!["content"]!.GetValue<string>()).IsOrder);
    }

    [Fact]
    [Spec(SpecCategories.Processing, "人の確認をすべて取り消せる（デバッグ用）", "確認済みのメールを未解析に戻して人が確定した発注を削除し、自動登録の発注と解析履歴は残す。次の実行で解析し直す。")]
    public async Task Clearing_reviews_resets_human_decisions_and_reanalyzes_them()
    {
        var processor = _services.GetRequiredService<MailProcessor>();
        var review = _services.GetRequiredService<ReviewService>();
        await processor.RunAsync();
        int confirmedId = await SampleIdAsync("02"), dismissedId = await SampleIdAsync("13"), autoId = await SampleIdAsync("01");
        await review.ConfirmOrderAsync(confirmedId, new OrderDraft { CustomerCompany = "人が入力", Items = [new() { ProductName = "ボールペン", Quantity = 50, Unit = "本" }] });
        await review.MarkNotOrderAsync(dismissedId);
        int analyses;
        await using (var db = Db()) analyses = await db.Analyses.CountAsync();

        Assert.Equal(2, await review.ClearAllReviewsAsync());

        await using (var db = Db())
        {
            var cleared = await db.Mails.Include(m => m.Order).Where(m => m.Id == confirmedId || m.Id == dismissedId).ToListAsync();
            Assert.All(cleared, m =>
            {
                Assert.False(m.IsReviewed);
                Assert.Equal(MailStatus.Unanalyzed, m.Status);
                Assert.Null(m.Order);
            });
            var auto = await db.Mails.Include(m => m.Order).SingleAsync(m => m.Id == autoId);
            Assert.Equal(MailStatus.Registered, auto.Status);
            Assert.Equal(RegisteredBy.Auto, auto.Order!.RegisteredBy);
            Assert.Equal(analyses, await db.Analyses.CountAsync());
        }
        Assert.Equal(0, await review.CountReviewedAsync());

        var rerun = await processor.RunAsync();
        Assert.Equal(2, rerun.Analyzed);
        await using (var db = Db())
        {
            var reanalyzed = await db.Mails.Include(m => m.Order).SingleAsync(m => m.Id == confirmedId);
            Assert.NotEqual(MailStatus.Unanalyzed, reanalyzed.Status);
            Assert.NotEqual("人が入力", reanalyzed.Order?.CustomerCompany);
        }
    }

    [Fact]
    [Spec(SpecCategories.Settings, "プロンプトの設定を保存する", "当社名・編集したルール・手本数を保存して読み戻せる。ルールが未編集なら既定のルールを使う。")]
    public void Settings_keep_prompt_settings_and_unchanged_rules_as_null()
    {
        var path = Path.Combine(_dataDir, "prompt-test.json");
        var store = new SettingsStore(path);
        var settings = store.Current;
        Assert.Null(settings.Ai.Instructions);
        Assert.Equal(OrderPrompt.DefaultInstructions, OrderPrompt.Instructions(settings.Ai));
        settings.Ai.OwnCompanyName = "サンプル商事";
        settings.Ai.Instructions = "独自のルール";
        settings.Ai.ExampleCount = 5;
        store.Save(settings);

        var reloaded = new SettingsStore(path).Current.Ai;
        Assert.Equal("サンプル商事", reloaded.OwnCompanyName);
        Assert.Equal("独自のルール", OrderPrompt.Instructions(reloaded));
        Assert.Equal(5, reloaded.ExampleCount);
    }

    [Fact]
    [Spec(SpecCategories.Settings, "API キーとパスワードを暗号化して保存する", "設定ファイルに API キーと IMAP パスワードが平文で残らず、読み戻すと元の値になる。")]
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
    [Spec(SpecCategories.Import, "日本語メールと HTML メールを読み込む", ".eml フォルダから 20 通を読み、日本語の件名を復元し、HTML の表を「|」区切りのテキストにする（タグは残さない）。")]
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
