using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Analysis;

public sealed class AiConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Calls any server that implements the OpenAI chat completions API (Ollama, LM Studio, Gemini, OpenAI, Azure OpenAI, ...).
/// Structured output support differs by server, so the response format falls back step by step on HTTP 400.
/// Reasoning models that only accept the default temperature are retried without it.
/// </summary>
public sealed class OpenAiCompatibleOrderAnalyzer(HttpClient http, AiSettings settings) : IOrderAnalyzer
{
    private enum FormatMode
    {
        None,
        JsonObject,
        JsonSchema,
    }

    private FormatMode _mode = settings.UseJsonSchema ? FormatMode.JsonSchema : FormatMode.JsonObject;
    private bool _sendTemperature = true;

    public string Name => $"AI（{settings.Model}）";

    public async Task<OrderExtraction> AnalyzeAsync(MailInput mail, IReadOnlyList<AnalysisExample> examples, CancellationToken ct)
    {
        var messages = new List<object>
        {
            new { role = "system", content = OrderPrompt.System(settings, mail.ReceivedAtUtc.ToLocalTime(), examples.Count > 0) },
        };
        foreach (var example in examples)
        {
            messages.Add(new { role = "user", content = OrderPrompt.User(example.Mail, OrderPrompt.MaxExampleBodyChars) });
            messages.Add(new { role = "assistant", content = OrderPrompt.Answer(example.Expected) });
        }
        messages.Add(new { role = "user", content = OrderPrompt.User(mail) });

        var content = await CompleteAsync(messages, ct);
        OrderExtraction result;
        try
        {
            result = ExtractionJson.Parse(content);
        }
        catch (FormatException e)
        {
            messages.Add(new { role = "assistant", content });
            messages.Add(new { role = "user", content = $"出力を JSON として解釈できませんでした（{e.Message}）。指定のスキーマに従った JSON オブジェクトだけを出力してください。" });
            result = ExtractionJson.Parse(await CompleteAsync(messages, ct));
        }
        return OrderPrompt.IsOwnCompany(result.CustomerCompany, settings.OwnCompanyName) ? result with { CustomerCompany = null } : result;
    }

    private async Task<string> CompleteAsync(List<object> messages, CancellationToken ct)
    {
        while (true)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = settings.Model,
                ["messages"] = messages,
            };
            if (_sendTemperature) body["temperature"] = 0;
            if (_mode == FormatMode.JsonSchema)
                body["response_format"] = new { type = "json_schema", json_schema = new { name = "order_extraction", strict = true, schema = ExtractionJson.Schema() } };
            else if (_mode == FormatMode.JsonObject)
                body["response_format"] = new { type = "json_object" };

            using var request = new HttpRequestMessage(HttpMethod.Post, AiEndpoint.Url(settings.Endpoint, "chat/completions")) { Content = JsonContent.Create(body) };
            AiEndpoint.Authorize(request, settings);

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
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    if (_sendTemperature && text.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        _sendTemperature = false;
                        continue;
                    }
                    if (_mode != FormatMode.None)
                    {
                        _mode--;
                        continue;
                    }
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
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
