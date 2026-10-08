using OrderMail.Core.Analysis;
using OrderMail.Core.Domain;
using OrderMail.Core.Processing;

namespace OrderMail.Tests;

public class ExtractionJsonTests
{
    [Fact]
    [Spec(SpecCategories.Parsing, "コードブロックや思考部分に囲まれた JSON を読む", "<think> ブロックや ```json で囲まれた応答でも本体の JSON を読み、空文字は null として扱う。")]
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
    [Spec(SpecCategories.Parsing, "型の揺れを吸収する", "文字列の \"true\"、0〜100 の自信度、「５０本」「1,200円」のような数量・単価を読み取る。品名がない・数量 0 の明細と、読めない日付は捨てる。")]
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
    [Spec(SpecCategories.Parsing, "使えない応答はエラーにする", "JSON がない・is_order がない・構文が壊れている応答は FormatException にする（3 ケース）。")]
    public void Rejects_unusable_output(string text)
    {
        Assert.Throws<FormatException>(() => ExtractionJson.Parse(text));
    }

    [Fact]
    [Spec(SpecCategories.Parsing, "保存した解析結果を復元できる", "解析結果を JSON に保存して読み戻すと、元の内容と一致する。")]
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
    [Spec(SpecCategories.Processing, "自信度と明細で振り分ける", "自信度がしきい値以上なら「発注」（明細あり）か「発注以外」に自動で振り分け、しきい値未満と明細のない発注は「要確認」にする（5 ケース）。")]
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
