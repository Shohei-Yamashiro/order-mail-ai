using OrderMail.Core.Analysis;
using OrderMail.Core.Domain;
using OrderMail.Core.Processing;

namespace OrderMail.Tests;

public class ExtractionJsonTests
{
    [Fact]
    public void Parses_output_wrapped_in_code_fence_and_think_block()
    {
        var text = """
            <think>{"is_order": false}</think>
            結果です。
            ```json
            {"is_order": true, "confidence": 0.92, "reason": "発注の意思あり", "customer_company": "あおぞら工業株式会社",
             "customer_name": null, "delivery_date": "2026-10-09", "notes": "",
             "items": [{"product_name": "A4コピー用紙", "quantity": 10, "unit": "箱", "unit_price": null}]}
            ```
            """;

        var result = ExtractionJson.Parse(text);

        Assert.True(result.IsOrder);
        Assert.Equal(0.92, result.Confidence);
        Assert.Equal("あおぞら工業株式会社", result.CustomerCompany);
        Assert.Null(result.CustomerName);
        Assert.Null(result.Notes);
        Assert.Equal(new DateOnly(2026, 10, 9), result.DeliveryDate);
        Assert.Equal([new ExtractedItem("A4コピー用紙", 10, "箱", null)], result.Items);
    }

    [Fact]
    public void Accepts_loose_types_from_models()
    {
        var text = """
            {"is_order": "true", "confidence": 85, "delivery_date": "来週", "items": [
              {"product_name": "ボールペン", "quantity": "５０本", "unit": "本", "unit_price": "1,200円"},
              {"product_name": "", "quantity": 3},
              {"product_name": "付箋", "quantity": 0}
            ]}
            """;

        var result = ExtractionJson.Parse(text);

        Assert.True(result.IsOrder);
        Assert.Equal(0.85, result.Confidence);
        Assert.Null(result.DeliveryDate);
        Assert.Equal([new ExtractedItem("ボールペン", 50, "本", 1200)], result.Items);
    }

    [Theory]
    [InlineData("申し訳ありませんが判定できません")]
    [InlineData("{\"confidence\": 0.5}")]
    [InlineData("{\"is_order\": true,, }")]
    public void Rejects_unusable_output(string text)
    {
        Assert.Throws<FormatException>(() => ExtractionJson.Parse(text));
    }

    [Fact]
    public void Round_trips_through_stored_json()
    {
        var original = new OrderExtraction
        {
            IsOrder = true,
            Confidence = 0.7,
            Reason = "理由",
            CustomerCompany = "株式会社テスト",
            DeliveryDate = new DateOnly(2026, 11, 2),
            Items = [new ExtractedItem("模造紙", 10, "パック", 350.5m)],
        };

        var restored = ExtractionJson.Parse(ExtractionJson.Serialize(original));

        Assert.Equal(original with { Items = [] }, restored with { Items = [] });
        Assert.Equal(original.Items, restored.Items);
    }

    [Theory]
    [InlineData(true, 0.9, 1, MailStatus.Registered)]
    [InlineData(true, 0.9, 0, MailStatus.NeedsReview)]
    [InlineData(true, 0.7, 1, MailStatus.NeedsReview)]
    [InlineData(false, 0.9, 0, MailStatus.NotOrder)]
    [InlineData(false, 0.7, 0, MailStatus.NeedsReview)]
    public void Registration_policy(bool isOrder, double confidence, int items, MailStatus expected)
    {
        var result = new OrderExtraction
        {
            IsOrder = isOrder,
            Confidence = confidence,
            Items = Enumerable.Range(0, items).Select(i => new ExtractedItem($"品{i}", 1, null, null)).ToList(),
        };

        Assert.Equal(expected, RegistrationPolicy.Decide(result, 0.8));
    }
}
