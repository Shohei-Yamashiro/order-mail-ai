using OrderMail.Core.Analysis;
using Xunit.Abstractions;

namespace OrderMail.Tests;

public class RuleBasedAnalyzerTests(ITestOutputHelper output)
{
    [Fact]
    [Spec(SpecCategories.RuleBased, "サンプル 20 通の正解率", "ルールベースで発注かどうかを判定し、正解ラベル（samples/labels.json）との一致率が 85% 以上である。メールごとの判定結果は付録に載せる。")]
    public void Classifies_most_samples_correctly()
    {
        var correct = 0;
        foreach (var label in Samples.Labels)
        {
            var result = RuleBasedOrderAnalyzer.Analyze(Samples.Input(label.File));
            var ok = result.IsOrder == label.IsOrder;
            if (ok) correct++;
            output.WriteLine($"{(ok ? "OK " : "NG ")} {label.File,-30} order={result.IsOrder,-5} conf={result.Confidence:0.00} items={result.Items.Count}/{label.Items} {result.Reason}");
            foreach (var item in result.Items)
                output.WriteLine($"      - {item.ProductName} / {item.Quantity} {item.Unit} @ {item.UnitPrice}");
            if (result.IsOrder)
                output.WriteLine($"      company={result.CustomerCompany} name={result.CustomerName} date={result.DeliveryDate}");
        }

        var accuracy = (double)correct / Samples.Labels.Count;
        output.WriteLine($"accuracy {accuracy:P0}");
        Assert.True(accuracy >= 0.85, $"accuracy {accuracy:P0}");
    }

    [Fact]
    [Spec(SpecCategories.RuleBased, "「品名：」「数量：」形式から明細と単価を読む", "ブロック形式の 2 明細を品名・数量・単位・単価まで読み取り、希望納期と会社名も読み取る。")]
    public void Extracts_block_format_with_prices()
    {
        var result = RuleBasedOrderAnalyzer.Analyze(Samples.Input("03_block_format.eml"));

        Assert.True(result.IsOrder);
        Assert.Equal(
            [new ExtractedItem("ニトリル手袋 Mサイズ(100枚入)", 30, "箱", 850), new ExtractedItem("養生テープ 緑 50mm", 60, "巻", 210)],
            result.Items);
        Assert.Equal(new DateOnly(2026, 10, 16), result.DeliveryDate);
        Assert.Equal("北風精機株式会社", result.CustomerCompany);
    }

    [Fact]
    [Spec(SpecCategories.RuleBased, "HTML の表から明細を読む", "HTML メールの表から 2 明細（品名・数量・単位・単価）と希望納期を読み取る。")]
    public void Extracts_html_table()
    {
        var result = RuleBasedOrderAnalyzer.Analyze(Samples.Input("07_html_table.eml"));

        Assert.True(result.IsOrder);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new ExtractedItem("サージカルマスク(50枚入)", 40, "箱", 480), result.Items[0]);
        Assert.Equal(new DateOnly(2026, 10, 13), result.DeliveryDate);
    }

    [Fact]
    [Spec(SpecCategories.RuleBased, "引用部分は判定に使わない", "行頭が「>」の引用行に「発注いたします」があっても、発注と判定しない。")]
    public void Ignores_quoted_lines()
    {
        var mail = new MailInput("Re: ご案内", "a@example.com", "", "承知しました。\n> 見積書をお送りします。\n> 発注いたします", DateTime.UtcNow);
        var result = RuleBasedOrderAnalyzer.Analyze(mail);

        Assert.False(result.IsOrder);
    }

    [Fact]
    [Spec(SpecCategories.RuleBased, "年末に受信した「1月10日」は翌年にする", "12 月に受信したメールの「納期: 1月10日」を、翌年の 1 月 10 日として読む。")]
    public void Resolves_month_day_into_next_year_after_year_end()
    {
        var mail = new MailInput("発注", "a@example.com", "", "下記の通り発注いたします。\n・封筒 × 100枚\n納期: 1月10日", new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc));
        var result = RuleBasedOrderAnalyzer.Analyze(mail);

        Assert.Equal(new DateOnly(2027, 1, 10), result.DeliveryDate);
    }
}
