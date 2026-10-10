namespace OrderMail.Tests;

/// <summary>
/// Describes a test in Japanese for the generated test specification (scripts/TestReport.cs).
/// Every test method must have one; <see cref="SpecCoverageTests"/> enforces it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SpecAttribute(string category, string title, string expected) : Attribute
{
    public string Category { get; } = category;
    public string Title { get; } = title;
    public string Expected { get; } = expected;
}

public static class SpecCategories
{
    public const string Import = "メールの取り込み";
    public const string RuleBased = "ルールベース解析";
    public const string Ai = "AI 連携（OpenAI 互換 API）";
    public const string Prompt = "AI への指示（プロンプト）";
    public const string Parsing = "AI の出力の読み取り";
    public const string Processing = "振り分けと人の確認";
    public const string Examples = "手本と評価";
    public const string Feedback = "フィードバックとチューニング";
    public const string Settings = "設定";
    public const string Meta = "テスト基盤";

    /// <summary>Order of sections in the document.</summary>
    public static readonly string[] All = [Import, RuleBased, Ai, Prompt, Parsing, Processing, Examples, Feedback, Settings, Meta];
}

public class SpecCoverageTests
{
    [Fact]
    [Spec(SpecCategories.Meta, "全テストに仕様の説明が付いている", "テストメソッドすべてに区分・項目名・確認内容があり、区分は決められた一覧のどれかである。")]
    public void Every_test_has_a_spec()
    {
        var tests = typeof(SpecCoverageTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.IsDefined(typeof(FactAttribute), inherit: true))
            .ToList();

        Assert.NotEmpty(tests);
        Assert.All(tests, m =>
        {
            var spec = m.GetCustomAttributes(typeof(SpecAttribute), false).Cast<SpecAttribute>().SingleOrDefault();
            Assert.True(spec is not null, $"{m.DeclaringType!.Name}.{m.Name} に [Spec] がありません。");
            Assert.Contains(spec!.Category, SpecCategories.All);
        });
    }
}
