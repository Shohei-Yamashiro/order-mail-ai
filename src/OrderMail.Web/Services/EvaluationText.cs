using OrderMail.Core.Analysis;
using OrderMail.Core.Processing;

namespace OrderMail.Web.Services;

/// <summary>Labels and difference messages for evaluation results (settings page and feedback page).</summary>
public static class EvaluationText
{
    public static readonly (string Label, Func<EvaluationReport, string> Value)[] Metrics =
    [
        ("発注かどうかの判定", r => Ratio(r.IsOrderOk, r.Total)),
        ("明細（数量・単位）", r => Ratio(r.ItemsOk, r.Orders)),
        ("会社名", r => Ratio(r.CompanyOk, r.Orders)),
        ("希望納期", r => Ratio(r.DateOk, r.Orders)),
        ("自動で確定", r => Ratio(r.Auto, r.Total)),
        ("うち判定の誤り", r => $"{r.WrongAuto} 件"),
        ("解析エラー", r => $"{r.Errors} 件"),
        ("1 通あたりの時間", r => $"{r.AverageSeconds:0.0} 秒"),
    ];

    public static string Ratio(int ok, int total) =>
        total == 0 ? "－" : $"{ok}/{total}（{(double)ok / total:P0}）";

    /// <param name="actualLabel">What produced the actual answer, for example "AI".</param>
    public static IEnumerable<string> Differences(EvaluationRow row, string actualLabel = "AI")
    {
        if (row.Error is not null)
        {
            yield return "エラー: " + row.Error;
            yield break;
        }
        var a = row.Actual!;
        var e = row.Expected;
        if (!row.IsOrderOk)
        {
            yield return $"判定: 正解は{(e.IsOrder ? "発注" : "発注以外")}、{actualLabel} は{(a.IsOrder ? "発注" : "発注以外")}（自信度 {a.Confidence:0.00}）";
            yield break;
        }
        if (!e.IsOrder) yield break;
        if (!row.CompanyOk) yield return $"会社名: 正解「{e.CustomerCompany ?? "なし"}」、{actualLabel}「{a.CustomerCompany ?? "なし"}」";
        if (!row.DateOk) yield return $"希望納期: 正解 {e.DeliveryDate?.ToString("yyyy-MM-dd") ?? "なし"}、{actualLabel} {a.DeliveryDate?.ToString("yyyy-MM-dd") ?? "なし"}";
        if (!row.ItemsOk) yield return $"明細: 正解 {Items(e)}、{actualLabel} {Items(a)}";
    }

    private static string Items(OrderExtraction e) =>
        e.Items.Count == 0 ? "なし" : string.Join("／", e.Items.Select(i => $"{i.ProductName} {i.Quantity:0.###}{i.Unit}"));
}
