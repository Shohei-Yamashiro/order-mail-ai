using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrderMail.Core.Analysis;
using OrderMail.Core.Settings;

namespace OrderMail.Tests;

public class OpenAiCompatibleAnalyzerTests
{
    private const string OrderJson = """{"is_order": true, "confidence": 0.95, "reason": "r", "customer_company": null, "customer_name": null, "delivery_date": null, "notes": null, "items": [{"product_name": "封筒", "quantity": 100, "unit": "枚", "unit_price": null}]}""";

    private static readonly MailInput Mail = new("発注", "a@example.com", "A", "封筒を100枚お願いします", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    [Spec(SpecCategories.Ai, "接続先・モデル・API キー・JSON スキーマを送る", "エンドポイント末尾の「/」を補って /chat/completions に送り、モデル名・Bearer 認証・json_schema 指定が含まれる。応答から明細を読み取れる。")]
    public async Task Sends_schema_and_api_key_to_chat_completions()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, Completion(OrderJson)));
        var analyzer = Create(handler, new AiSettings { Endpoint = "https://api.example.com/v1/", Model = "m1", ApiKey = "secret" });

        var result = await analyzer.AnalyzeAsync(Mail, [], CancellationToken.None);

        Assert.True(result.IsOrder);
        Assert.Equal("封筒", Assert.Single(result.Items).ProductName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.example.com/v1/chat/completions", request.Url);
        Assert.Equal("Bearer secret", request.Authorization);
        Assert.Equal("m1", request.Body["model"]!.GetValue<string>());
        Assert.Equal("json_schema", request.Body["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "JSON スキーマ非対応の接続先では自動で切り替える", "json_schema 指定が HTTP 400 で拒否されたら json_object で送り直し、解析に成功する。API キーが空なら認証ヘッダーを付けない。")]
    public async Task Falls_back_to_plain_json_when_schema_is_rejected()
    {
        var handler = new FakeHandler(body => body["response_format"]?["type"]?.GetValue<string>() == "json_schema"
            ? Reply(HttpStatusCode.BadRequest, """{"error": "response_format not supported"}""")
            : Reply(HttpStatusCode.OK, Completion(OrderJson)));
        var analyzer = Create(handler, new AiSettings { Endpoint = "http://localhost:11434/v1", Model = "m" });

        var result = await analyzer.AnalyzeAsync(Mail, [], CancellationToken.None);

        Assert.True(result.IsOrder);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("json_object", handler.Requests[1].Body["response_format"]!["type"]!.GetValue<string>());
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    [Spec(SpecCategories.Ai, "Azure OpenAI には api-key ヘッダーで送る", "接続先が Azure OpenAI（*.openai.azure.com）なら、API キーを api-key ヘッダーで送り、Bearer 認証は付けない。モデル名（デプロイ名）はそのまま送る。")]
    public async Task Sends_api_key_header_to_azure_openai()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, Completion(OrderJson)));
        var analyzer = Create(handler, new AiSettings { Endpoint = "https://contoso.openai.azure.com/openai/v1", Model = "my-deployment", ApiKey = "azure-key" });

        await analyzer.AnalyzeAsync(Mail, [], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://contoso.openai.azure.com/openai/v1/chat/completions", request.Url);
        Assert.Equal("azure-key", request.ApiKey);
        Assert.Null(request.Authorization);
        Assert.Equal("my-deployment", request.Body["model"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "temperature を受け付けないモデルでは指定を外す", "temperature の指定が HTTP 400 で拒否されたら、temperature だけを外して送り直す（JSON スキーマ指定は残す）。")]
    public async Task Retries_without_temperature_for_models_that_reject_it()
    {
        var handler = new FakeHandler(body => body["temperature"] is not null
            ? Reply(HttpStatusCode.BadRequest, """{"error": {"message": "Unsupported value: 'temperature' does not support 0 with this model."}}""")
            : Reply(HttpStatusCode.OK, Completion(OrderJson)));
        var analyzer = Create(handler, new AiSettings { Model = "reasoning-model" });

        var result = await analyzer.AnalyzeAsync(Mail, [], CancellationToken.None);

        Assert.True(result.IsOrder);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[1].Body["temperature"]);
        Assert.Equal("json_schema", handler.Requests[1].Body["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "JSON でない応答には 1 回だけ出し直しを頼む", "1 回目の応答が JSON でないとき、読めなかった理由を伝えて出し直させ、2 回目の応答で解析に成功する。")]
    public async Task Retries_once_when_output_is_not_json()
    {
        var replies = new Queue<string>(["すみません、発注です。", OrderJson]);
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, Completion(replies.Dequeue())));
        var analyzer = Create(handler, new AiSettings { Model = "m" });

        var result = await analyzer.AnalyzeAsync(Mail, [], CancellationToken.None);

        Assert.True(result.IsOrder);
        Assert.Equal(4, handler.Requests[1].Body["messages"]!.AsArray().Count);
    }

    [Fact]
    [Spec(SpecCategories.Prompt, "手本を会話例として送る", "手本は「メール（user）と正しい答え（assistant）」の組として解析するメールの前に並ぶ。編集したルール・当社名・手本の説明が指示文に入り、既定のルールは入らない。")]
    public async Task Sends_examples_as_conversation_before_the_mail()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, Completion(OrderJson)));
        var analyzer = Create(handler, new AiSettings { Model = "m", OwnCompanyName = "サンプル商事", Instructions = "独自のルール" });
        var example = new AnalysisExample(
            new MailInput("いつもの", "sato@hinata.example", "佐藤", "ボールペン50本お願いします", Mail.ReceivedAtUtc),
            new OrderExtraction { IsOrder = true, Confidence = 0.95, CustomerCompany = "ひなたデザイン", Items = [new ExtractedItem("ボールペン", 50, "本", null)] });

        await analyzer.AnalyzeAsync(Mail, [example], CancellationToken.None);

        var messages = Assert.Single(handler.Requests).Body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var system = messages[0]!["content"]!.GetValue<string>();
        Assert.Contains("独自のルール", system);
        Assert.DoesNotContain("# 判定基準", system);
        Assert.Contains("「サンプル商事」", system);
        Assert.Contains("# 手本", system);
        Assert.Contains("ボールペン50本", messages[1]!["content"]!.GetValue<string>());
        var answer = ExtractionJson.Parse(messages[2]!["content"]!.GetValue<string>());
        Assert.Equal("ひなたデザイン", answer.CustomerCompany);
        Assert.Contains("封筒を100枚", messages[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Prompt, "既定のルールとカレンダーを指示文に入れる", "ルールが未編集なら既定の判定基準を使い、受信日・今週と来週の曜日付きカレンダー・出力形式が入る。手本がなければ手本の説明は入らない。")]
    public void System_prompt_has_default_rules_and_a_calendar_for_relative_dates()
    {
        var system = OrderPrompt.System(new AiSettings(), new DateTime(2026, 10, 8, 9, 0, 0), hasExamples: false);

        Assert.Contains("# 判定基準", system);
        Assert.Contains("受信日: 2026-10-08(木)", system);
        Assert.Contains("今週: 2026-10-05(月)", system);
        Assert.Contains("2026-10-10(土)", system);
        Assert.Contains("来週: 2026-10-12(月)", system);
        Assert.Contains("当社はこのメールの受信者", system);
        Assert.Contains("# 出力", system);
        Assert.DoesNotContain("# 手本", system);
    }

    [Fact]
    [Spec(SpecCategories.Ai, "会社名に当社名が返ったら空欄に戻す", "当社名が「サンプル商事」のとき、AI が会社名を「株式会社 サンプル商事」と返しても空欄にし、明細はそのまま残す。")]
    public async Task Drops_customer_company_when_it_is_the_own_company()
    {
        var json = OrderJson.Replace("\"customer_company\": null", "\"customer_company\": \"株式会社 サンプル商事\"");
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, Completion(json)));
        var analyzer = Create(handler, new AiSettings { Model = "m", OwnCompanyName = "サンプル商事" });

        var result = await analyzer.AnalyzeAsync(Mail, [], CancellationToken.None);

        Assert.Null(result.CustomerCompany);
        Assert.Single(result.Items);
    }

    [Fact]
    [Spec(SpecCategories.Prompt, "相対的な日付を受信日から換算する", "受信日 2026-10-08(木) に対して「今週土曜」「来週の金曜日」「再来週月曜」「明日」を正しい日付にし、「来週中」は日が決まらない表現とする。引用行と「10月9日」のような日付の表記は対象外。")]
    public void Date_hints_resolve_relative_days_from_the_received_date()
    {
        var received = new DateOnly(2026, 10, 8);

        var hints = DateHints.Find("今週土曜までに手配を。来週の金曜日でも可。再来週月曜は不可。\n明日連絡します。来週中に届くと助かります。\n> 明後日（引用）", received);

        Assert.Equal(
        [
            "「今週土曜」= 2026-10-10(土)",
            "「来週の金曜日」= 2026-10-16(金)",
            "「再来週月曜」= 2026-10-19(月)",
            "「明日」= 2026-10-09(金)",
            "「来週中」は日が決まらない表現",
        ], hints);
        Assert.Empty(DateHints.Find("納期：10月9日", received));
    }

    [Fact]
    [Spec(SpecCategories.Prompt, "日付の手がかりは必要なときだけ付ける", "本文に相対的な日付があるときだけ、メールの後に「日付の手がかり」を付け足す。")]
    public void User_prompt_adds_date_hints_only_when_found()
    {
        var local = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Local);
        var withHint = OrderPrompt.User(Mail with { Body = "封筒を100枚、今週土曜までに", ReceivedAtUtc = local.ToUniversalTime() });
        var without = OrderPrompt.User(Mail with { ReceivedAtUtc = local.ToUniversalTime() });

        Assert.Contains("日付の手がかり", withHint);
        Assert.Contains("2026-10-10(土)", withHint);
        Assert.DoesNotContain("日付の手がかり", without);
    }

    [Fact]
    [Spec(SpecCategories.Ai, "接続先のエラーを分かる形で伝える", "HTTP 401 などのエラーでは、ステータスコードと接続先のメッセージを含む例外にする。")]
    public async Task Reports_server_errors_clearly()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.Unauthorized, """{"error": "invalid api key"}"""));
        var analyzer = Create(handler, new AiSettings { Model = "m" });

        var e = await Assert.ThrowsAsync<AiConnectionException>(() => analyzer.AnalyzeAsync(Mail, [], CancellationToken.None));

        Assert.Contains("401", e.Message);
        Assert.Contains("invalid api key", e.Message);
    }

    private static OpenAiCompatibleOrderAnalyzer Create(FakeHandler handler, AiSettings settings) =>
        new(new HttpClient(handler), settings);

    private static string Completion(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(string Url, string? Authorization, string? ApiKey, JsonNode Body);

    private sealed class FakeHandler(Func<JsonNode, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var apiKey = request.Headers.TryGetValues("api-key", out var values) ? values.Single() : null;
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), apiKey, body));
            return respond(body);
        }
    }
}
