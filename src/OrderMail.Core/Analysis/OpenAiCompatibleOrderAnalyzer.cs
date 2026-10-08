using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Analysis;

public sealed class AiConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Calls any server that implements the OpenAI chat completions API (Ollama, LM Studio, Gemini, OpenAI, ...).
/// Structured output support differs by server, so the response format falls back step by step on HTTP 400.
/// </summary>
public sealed class OpenAiCompatibleOrderAnalyzer(HttpClient http, AiSettings settings) : IOrderAnalyzer
{
    private const int MaxBodyChars = 8000;

    private enum FormatMode
    {
        None,
        JsonObject,
        JsonSchema,
    }

    private FormatMode _mode = settings.UseJsonSchema ? FormatMode.JsonSchema : FormatMode.JsonObject;

    public string Name => $"AI（{settings.Model}）";

    public async Task<OrderExtraction> AnalyzeAsync(MailInput mail, CancellationToken ct)
    {
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt(mail.ReceivedAtUtc.ToLocalTime()) },
            new { role = "user", content = UserPrompt(mail) },
        };

        var content = await CompleteAsync(messages, ct);
        try
        {
            return ExtractionJson.Parse(content);
        }
        catch (FormatException e)
        {
            messages.Add(new { role = "assistant", content });
            messages.Add(new { role = "user", content = $"出力を JSON として解釈できませんでした（{e.Message}）。指定のスキーマに従った JSON オブジェクトだけを出力してください。" });
            return ExtractionJson.Parse(await CompleteAsync(messages, ct));
        }
    }

    private async Task<string> CompleteAsync(List<object> messages, CancellationToken ct)
    {
        while (true)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = settings.Model,
                ["messages"] = messages,
                ["temperature"] = 0,
            };
            if (_mode == FormatMode.JsonSchema)
                body["response_format"] = new { type = "json_schema", json_schema = new { name = "order_extraction", strict = true, schema = ExtractionJson.Schema() } };
            else if (_mode == FormatMode.JsonObject)
                body["response_format"] = new { type = "json_object" };

            using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl(settings.Endpoint)) { Content = JsonContent.Create(body) };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException e)
            {
                throw new AiConnectionException($"AI の接続先に接続できません（{settings.Endpoint}）: {e.Message}", e);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new AiConnectionException($"AI の応答が {http.Timeout.TotalSeconds:0} 秒以内に返りませんでした。", e);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity && _mode != FormatMode.None)
                {
                    _mode--;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new AiConnectionException($"AI の接続先がエラーを返しました（HTTP {(int)response.StatusCode}）: {Truncate(text, 300)}");

                try
                {
                    using var doc = JsonDocument.Parse(text);
                    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
                {
                    throw new AiConnectionException($"AI の応答形式が想定と異なります: {Truncate(text, 300)}", e);
                }
            }
        }
    }

    public static Uri ChatUrl(string endpoint) => new(endpoint.Trim().TrimEnd('/') + "/chat/completions");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public static string SystemPrompt(DateTime receivedLocal) => $$"""
        あなたは受発注業務のアシスタントです。受信メールが「当社への発注（注文）」かどうかを判定し、発注であれば内容を抽出してください。

        # 判定基準
        - 発注: 商品と数量を示して、購入・納品・手配を依頼または確定しているメール。言い回しや敬語の程度は問わない。追加注文も発注。
        - 発注ではない: 見積依頼、価格・在庫・納期の問い合わせ、発送状況の確認、キャンセル・変更の依頼、請求・支払いの連絡、お礼、営業・宣伝、システム通知、社内連絡など。
        - 引用部分（行頭が「>」）は過去のやり取りなので、判定と抽出には使わない。

        # 出力
        次のキーを持つ JSON オブジェクトだけを出力する（説明文やコードブロックは付けない）。
        - is_order: 発注なら true
        - confidence: 判定の確からしさ（0〜1）。曖昧な点があれば低くする。
        - reason: 判定理由（日本語で1〜2文）
        - customer_company: 発注元の会社名（不明なら null）
        - customer_name: 発注元の担当者名（不明なら null）
        - delivery_date: 希望納期（YYYY-MM-DD）。受信日 {{receivedLocal.ToString("yyyy-MM-dd (ddd)", CultureInfo.GetCultureInfo("ja-JP"))}} を基準に「来週金曜」なども日付にする。不明なら null。
        - notes: 配送先・支払条件などの補足（なければ null）
        - items: 明細の配列。各要素は product_name（品名。品番があれば含める）、quantity（数値）、unit（単位。不明なら null）、unit_price（税抜単価の数値。不明なら null）。発注でなければ空配列。

        メールに書かれていないことは推測で埋めず null にする。
        """;

    public static string UserPrompt(MailInput mail)
    {
        var body = mail.Body.Length > MaxBodyChars ? mail.Body[..MaxBodyChars] + "\n（以下省略）" : mail.Body;
        return $"""
            差出人: {mail.FromName} <{mail.FromAddress}>
            件名: {mail.Subject}
            受信日時: {mail.ReceivedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}

            本文:
            {body}
            """;
    }
}

public sealed class AnalyzerFactory
{
    private static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    public IOrderAnalyzer Create(AiSettings settings) => settings.Provider switch
    {
        AiProvider.OpenAiCompatible => new OpenAiCompatibleOrderAnalyzer(
            new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)) },
            settings),
        _ => new RuleBasedOrderAnalyzer(),
    };
}
