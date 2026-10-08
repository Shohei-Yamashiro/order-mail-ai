using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace OrderMail.Core.Analysis;

/// <summary>JSON contract between the app and the AI: parsing is lenient because model output varies.</summary>
public static partial class ExtractionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
    };

    public static JsonNode Schema() => JsonNode.Parse(SchemaText)!;

    public const string SchemaText = """
        {
          "type": "object",
          "properties": {
            "is_order": { "type": "boolean" },
            "confidence": { "type": "number" },
            "reason": { "type": "string" },
            "customer_company": { "type": ["string", "null"] },
            "customer_name": { "type": ["string", "null"] },
            "delivery_date": { "type": ["string", "null"] },
            "notes": { "type": ["string", "null"] },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "product_name": { "type": "string" },
                  "quantity": { "type": "number" },
                  "unit": { "type": ["string", "null"] },
                  "unit_price": { "type": ["number", "null"] }
                },
                "required": ["product_name", "quantity", "unit", "unit_price"],
                "additionalProperties": false
              }
            }
          },
          "required": ["is_order", "confidence", "reason", "customer_company", "customer_name", "delivery_date", "notes", "items"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };

    public static string Serialize(OrderExtraction e, bool indented = true) => JsonSerializer.Serialize(new Dto
    {
        IsOrder = e.IsOrder,
        Confidence = e.Confidence,
        Reason = e.Reason,
        CustomerCompany = e.CustomerCompany,
        CustomerName = e.CustomerName,
        DeliveryDate = e.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Notes = e.Notes,
        Items = e.Items.Select(i => new ItemDto
        {
            ProductName = i.ProductName,
            Quantity = i.Quantity,
            Unit = i.Unit,
            UnitPrice = i.UnitPrice,
        }).ToList(),
    }, indented ? Options : CompactOptions);

    /// <exception cref="FormatException">The text does not contain a usable result.</exception>
    public static OrderExtraction Parse(string text)
    {
        var json = ExtractObject(text);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("JSON の構文が正しくありません: " + e.Message, e);
        }
        if (root is not JsonObject obj) throw new FormatException("JSON オブジェクトではありません。");

        var isOrder = ToBool(obj["is_order"]) ?? throw new FormatException("is_order がありません。");
        var confidence = (double)(ToDecimal(obj["confidence"]) ?? 0.5m);
        if (confidence > 1 && confidence <= 100) confidence /= 100;

        var items = new List<ExtractedItem>();
        if (obj["items"] is JsonArray array)
        {
            foreach (var node in array.OfType<JsonObject>())
            {
                var name = ToText(node["product_name"]);
                var quantity = ToDecimal(node["quantity"]);
                if (name is null || quantity is not > 0) continue;
                items.Add(new ExtractedItem(name, quantity.Value, ToText(node["unit"]), ToDecimal(node["unit_price"])));
            }
        }

        return new OrderExtraction
        {
            IsOrder = isOrder,
            Confidence = Math.Clamp(confidence, 0, 1),
            Reason = ToText(obj["reason"]),
            CustomerCompany = ToText(obj["customer_company"]),
            CustomerName = ToText(obj["customer_name"]),
            DeliveryDate = ToDate(obj["delivery_date"]),
            Notes = ToText(obj["notes"]),
            Items = items,
        };
    }

    private static string ExtractObject(string text)
    {
        text = ThinkBlock().Replace(text, "");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("JSON が見つかりません。");
        return text[start..(end + 1)];
    }

    private static string? ToText(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        text = text.Trim();
        return text.Length == 0 || text is "null" or "不明" or "なし" ? null : text;
    }

    private static bool? ToBool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var b)) return b;
        return ToText(node)?.ToLowerInvariant() switch
        {
            "true" or "yes" or "はい" => true,
            "false" or "no" or "いいえ" => false,
            _ => null,
        };
    }

    private static decimal? ToDecimal(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<decimal>(out var d)) return d;
        if (value.TryGetValue<double>(out var f)) return (decimal)f;
        var text = ToText(node);
        if (text is null) return null;
        var match = Number().Match(text.Normalize(NormalizationForm.FormKC).Replace(",", ""));
        return match.Success ? decimal.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }

    private static DateOnly? ToDate(JsonNode? node)
    {
        var text = ToText(node);
        if (text is null) return null;
        string[] formats = ["yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d"];
        return DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"-?\d+(\.\d+)?")]
    private static partial Regex Number();

    private sealed class Dto
    {
        public bool IsOrder { get; set; }
        public double Confidence { get; set; }
        public string? Reason { get; set; }
        public string? CustomerCompany { get; set; }
        public string? CustomerName { get; set; }
        public string? DeliveryDate { get; set; }
        public string? Notes { get; set; }
        public List<ItemDto> Items { get; set; } = [];
    }

    private sealed class ItemDto
    {
        public string ProductName { get; set; } = "";
        public decimal Quantity { get; set; }
        public string? Unit { get; set; }
        public decimal? UnitPrice { get; set; }
    }
}
