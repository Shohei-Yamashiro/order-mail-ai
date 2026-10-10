using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrderMail.Core.Analysis;

/// <param name="Template">The prompt template the model was published with (Go template syntax).</param>
/// <param name="Parameters">Modelfile PARAMETER lines without the keyword, for example <c>stop "&lt;|im_end|&gt;"</c>.</param>
public sealed record OllamaModelInfo(string Template, IReadOnlyList<string> Parameters);

/// <summary>Calls Ollama's own API (not the OpenAI-compatible one) for what fine-tuning needs to know about a base model.</summary>
public sealed class OllamaClient(HttpClient http)
{
    /// <summary>"http://localhost:11434/v1" → "http://localhost:11434/api/show".</summary>
    public static Uri ApiUrl(string endpoint, string path)
    {
        var root = endpoint.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        return new Uri(root + "/api/" + path);
    }

    /// <summary>Null when Ollama cannot be reached or does not know the model.</summary>
    public async Task<OllamaModelInfo?> ShowAsync(string endpoint, string model, CancellationToken ct = default)
    {
        try
        {
            using var body = new StringContent(new JsonObject { ["model"] = model }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(ApiUrl(endpoint, "show"), body, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var template = root.TryGetProperty("template", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(template)) return null;
            var parameters = root.TryGetProperty("parameters", out var p) ? ParseParameters(p.GetString()) : [];
            return new OllamaModelInfo(template, parameters);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Ollama lists parameters as "stop                           \"&lt;|im_start|&gt;\"" lines.</summary>
    public static IReadOnlyList<string> ParseParameters(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .Select(parts => $"{parts[0]} {parts[1].Trim()}")
            .ToList();
}
