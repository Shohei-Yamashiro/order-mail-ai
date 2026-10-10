using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OrderMail.Core.Analysis;

/// <summary>Keyword and pattern heuristics; used when no AI is configured and as a baseline for comparison.</summary>
public sealed partial class RuleBasedOrderAnalyzer : IOrderAnalyzer
{
    private const double OrderThreshold = 4;

    private const string Units = "個|本|箱|台|枚|袋|冊|セット|ケース|ダース|kg|g|L|m|巻|缶|パック|組|式|点|双|足|着|ロール|束|部|cs|pcs|units?";

    private static readonly (Regex Pattern, double Weight, string Label)[] Signals =
    [
        (new(@"(発注|注文)(を)?(いた|致)?(します|させて|お願い|したく)"), 4, "発注の意思表示"),
        (new(@"発注|注文|オーダー"), 2, "発注・注文の語"),
        (new(@"発注書|注文書"), 1, "発注書"),
        (new(@"手配(を)?(お願い|願い|していただけ|してもらえ)"), 3, "手配の依頼"),
        (new(@"(送って|納品して|届けて)(ください|いただけ|もらえ)"), 2, "納品の依頼"),
        (new(@"お願い(いた|致)?します|お願いでき"), 1, "依頼表現"),
        (new(@"購入(します|いたします|したい)"), 2, "購入の意思"),
        (new(@"追加で"), 1, "追加"),
        (new(@"purchase order|please (supply|send|ship)|would like to order|\border\b", RegexOptions.IgnoreCase), 4, "英文の発注"),
        (new(@"見積"), -3, "見積"),
        (new(@"キャンセル|取り消|取消"), -6, "キャンセル"),
        (new(@"請求書.{0,6}(受領|送付いたし|発行いたし)|お支払い|お振込|振込"), -4, "請求・支払い"),
        (new(@"在庫(は|が)?(あり|ござい|確認)"), -3, "在庫の問い合わせ"),
        (new(@"セミナー|ウェビナー|配信停止|メールマガジン|キャンペーン"), -4, "案内・宣伝"),
        (new(@"発送状況|配送状況|出荷状況|届いておりません|いつ頃(届|発送)"), -5, "配送状況の確認"),
        (new(@"届きました|受け取りました|受領しました"), -3, "受領の連絡"),
        (new(@"パスワード|ログイン|アカウント"), -3, "システム通知"),
        (new(@"会議|打ち合わせ|ミーティング"), -2, "社内連絡"),
        (new(@"検討"), -1, "検討中"),
    ];

    private static readonly string[] NotProductNames = ["合計", "小計", "総額", "送料", "消費税", "税", "数量", "単価", "金額", "計", "品名", "品番"];

    public string Name => "ルールベース";

    public Task<OrderExtraction> AnalyzeAsync(MailInput mail, AnalysisContext context, CancellationToken ct) =>
        Task.FromResult(Analyze(mail));

    public static OrderExtraction Analyze(MailInput mail)
    {
        var lines = OwnLines(Normalize(mail.Body));
        var text = Normalize(mail.Subject) + "\n" + string.Join('\n', lines);
        var items = ExtractItems(lines);

        double score = 0;
        var reasons = new List<string>();
        foreach (var (pattern, weight, label) in Signals)
        {
            if (!pattern.IsMatch(text)) continue;
            score += weight;
            reasons.Add($"{label}({weight:+0;-0})");
        }
        if (items.Count > 0)
        {
            score += 2;
            reasons.Add($"明細{items.Count}件(+2)");
        }

        var isOrder = score >= OrderThreshold;
        var confidence = Math.Clamp(0.5 + Math.Abs(score - (OrderThreshold - 0.5)) * 0.06, 0.5, 0.9);

        return new OrderExtraction
        {
            IsOrder = isOrder,
            Confidence = Math.Round(confidence, 2),
            Reason = $"スコア {score:0.#}（{(reasons.Count == 0 ? "手がかりなし" : string.Join("、", reasons))}）",
            CustomerCompany = isOrder ? FindCompany(lines) : null,
            CustomerName = isOrder ? FindPerson(lines) ?? NullIfEmpty(mail.FromName) : null,
            DeliveryDate = isOrder ? FindDeliveryDate(lines, mail.ReceivedAtUtc.ToLocalTime()) : null,
            Items = isOrder ? items : [],
        };
    }

    private static string Normalize(string text) =>
        text.Normalize(NormalizationForm.FormKC).Replace("\r\n", "\n");

    /// <summary>Drops quoted reply lines so earlier messages in the thread do not count.</summary>
    private static List<string> OwnLines(string body) =>
        body.Split('\n').Select(l => l.TrimEnd()).Where(l => !l.TrimStart().StartsWith('>')).ToList();

    public static List<ExtractedItem> ExtractItems(IReadOnlyList<string> lines)
    {
        var items = new List<ExtractedItem>();
        string? blockName = null;
        int[]? tableColumns = null;
        var previous = "";

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var before = previous;
            previous = line;

            if (!line.Contains('|'))
            {
                tableColumns = null;
            }
            else
            {
                var cells = line.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
                if (tableColumns is null)
                {
                    var nameCol = Array.FindIndex(cells, c => c is "品名" or "商品名" or "品目" or "商品");
                    var qtyCol = Array.FindIndex(cells, c => c.StartsWith("数量"));
                    if (nameCol >= 0 && qtyCol >= 0)
                    {
                        tableColumns = [nameCol, qtyCol, Array.FindIndex(cells, c => c.StartsWith("単位")), Array.FindIndex(cells, c => c.StartsWith("単価"))];
                        continue;
                    }
                }
                else if (cells.Length > tableColumns[1] && cells.Length > tableColumns[0])
                {
                    var qtyMatch = QuantityCell().Match(cells[tableColumns[1]]);
                    if (qtyMatch.Success && IsProductName(cells[tableColumns[0]]))
                    {
                        var unit = tableColumns[2] >= 0 && cells.Length > tableColumns[2] ? cells[tableColumns[2]] : NullIfEmpty(qtyMatch.Groups["unit"].Value);
                        var price = tableColumns[3] >= 0 && cells.Length > tableColumns[3] ? ParseNumber(cells[tableColumns[3]]) : null;
                        items.Add(new ExtractedItem(cells[tableColumns[0]], ParseNumber(qtyMatch.Groups["qty"].Value)!.Value, unit, price));
                    }
                    continue;
                }
            }

            if (BlockName().Match(line) is { Success: true } nameMatch)
            {
                blockName = nameMatch.Groups["v"].Value.Trim();
                continue;
            }
            if (BlockQuantity().Match(line) is { Success: true } blockQty && blockName is not null)
            {
                items.Add(new ExtractedItem(blockName, ParseNumber(blockQty.Groups["qty"].Value)!.Value, NullIfEmpty(blockQty.Groups["unit"].Value), null));
                blockName = null;
                continue;
            }
            if (BlockPrice().Match(line) is { Success: true } blockPrice && items.Count > 0 && items[^1].UnitPrice is null)
            {
                items[^1] = items[^1] with { UnitPrice = ParseNumber(blockPrice.Groups["p"].Value) };
                continue;
            }

            if (QuantityOnly().Match(line) is { Success: true } alone)
            {
                var label = CleanName(ItemLabel().Replace(before, ""));
                if (IsProductName(label))
                    items.Add(new ExtractedItem(label, ParseNumber(alone.Groups["qty"].Value)!.Value, alone.Groups["unit"].Value, null));
                continue;
            }

            var match = TimesPattern().Match(line);
            if (!match.Success) match = NameQuantityPattern().Match(line);
            List<Match> matches = match.Success ? [match] : SentencePattern().Matches(line).ToList();
            var priceMatch = InlinePrice().Match(line);
            foreach (var m in matches)
            {
                var name = CleanName(m.Groups["name"].Value);
                if (!IsProductName(name)) continue;
                items.Add(new ExtractedItem(
                    name,
                    ParseNumber(m.Groups["qty"].Value)!.Value,
                    NullIfEmpty(m.Groups["unit"].Value),
                    matches.Count == 1 && priceMatch.Success ? ParseNumber(priceMatch.Groups["p"].Value) : null));
            }
        }
        return items;
    }

    private static string CleanName(string name) =>
        LeadingFiller().Replace(name.Trim().TrimStart('・', '-', '*', '●', '■', '◆', '□', '○', '◎', ' '), "").TrimEnd(':', '、', ' ', '　');

    private static bool IsProductName(string name) =>
        name.Length is > 0 and <= 40 && !name.Contains('。') && !NotProductNames.Any(n => name.StartsWith(n, StringComparison.Ordinal));

    private static decimal? ParseNumber(string text)
    {
        var digits = text.Replace(",", "");
        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The sender's company, usually in the signature; the addressee block (御中・様) is skipped.</summary>
    private static string? FindCompany(IReadOnlyList<string> lines)
    {
        static bool IsAddressee(string line) => line.Contains("御中") || line.Contains('様');
        var candidates = lines
            .Select((l, i) => (Line: l.Trim(), Next: i + 1 < lines.Count ? lines[i + 1] : ""))
            .Where(x => CompanyPattern().IsMatch(x.Line) && x.Line.Length <= 40 && !IsAddressee(x.Line) && !IsAddressee(x.Next))
            .Select(x => x.Line)
            .ToList();
        if (candidates.Count == 0) return null;
        var line = CompanyLabel().Replace(candidates[^1], "");
        return CompanyPattern().Match(line).Value.Trim();
    }

    private static string? FindPerson(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (PersonPattern().Match(line) is { Success: true } m) return m.Groups["n"].Value.Trim();
        }
        return null;
    }

    private static DateOnly? FindDeliveryDate(IReadOnlyList<string> lines, DateTime received)
    {
        foreach (var line in lines.Where(l => DeliveryKeyword().IsMatch(l)))
        {
            if (FullDate().Match(line) is { Success: true } f &&
                TryDate(int.Parse(f.Groups["y"].Value), int.Parse(f.Groups["m"].Value), int.Parse(f.Groups["d"].Value), out var full))
                return full;

            if (MonthDay().Match(line) is { Success: true } md)
            {
                int month = int.Parse(md.Groups["m"].Value), day = int.Parse(md.Groups["d"].Value);
                var year = month < received.Month - 6 ? received.Year + 1 : received.Year;
                if (TryDate(year, month, day, out var date)) return date;
            }
        }
        return null;
    }

    private static bool TryDate(int y, int m, int d, out DateOnly date)
    {
        date = default;
        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    [GeneratedRegex(@"^(?:[・\-\*●■◆□○◎]|\d+[\.\)])?\s*(?<name>.+)\s*(?:×|\s[xX\*]\s?)\s*(?<qty>\d[\d,]*)\s*(?<unit>" + Units + ")?")]
    private static partial Regex TimesPattern();

    [GeneratedRegex(@"^(?:[・\-\*●■◆□○◎]|\d+[\.\)])?\s*(?<name>[^:\s][^:]*?)\s*:?\s+(?<qty>\d[\d,]*)\s*(?<unit>" + Units + ")")]
    private static partial Regex NameQuantityPattern();

    [GeneratedRegex(@"(?<name>[^\s、。,を「」]{2,40}?)[をも]\s*(?<qty>\d[\d,]*)\s*(?<unit>" + Units + ")")]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"^(?<qty>\d[\d,]*)\s*(?<unit>" + Units + ")$")]
    private static partial Regex QuantityOnly();

    [GeneratedRegex(@"^(品番|品名|商品名|型番)\s*:?\s*")]
    private static partial Regex ItemLabel();

    [GeneratedRegex(@"^(追加で|あと|また|それと|いつもの)")]
    private static partial Regex LeadingFiller();

    [GeneratedRegex(@"^(?<qty>\d[\d,]*)\s*(?<unit>\S*)")]
    private static partial Regex QuantityCell();

    [GeneratedRegex(@"^[・\-\*●■◆]?\s*(?:品名|商品名|品目|商品)\s*:\s*(?<v>.+)$")]
    private static partial Regex BlockName();

    [GeneratedRegex(@"^[・\-\*●■◆]?\s*数量\s*:\s*(?<qty>\d[\d,]*)\s*(?<unit>\S*)")]
    private static partial Regex BlockQuantity();

    [GeneratedRegex(@"^[・\-\*●■◆]?\s*単価\s*:\s*(?<p>\d[\d,]*)")]
    private static partial Regex BlockPrice();

    [GeneratedRegex(@"(?:@|単価\s*:?)\s*(?<p>\d[\d,]*)\s*円?")]
    private static partial Regex InlinePrice();

    [GeneratedRegex(@"(?:株式会社|有限会社|合同会社)\s*[^\s、。]+|[^\s、。]+\s*(?:株式会社|有限会社|合同会社)|\S+\s*\(株\)")]
    private static partial Regex CompanyPattern();

    [GeneratedRegex(@"^(会社名|社名)\s*:\s*")]
    private static partial Regex CompanyLabel();

    [GeneratedRegex(@"(?:(?:^|[\sの])(?<n>[一-龠々ァ-ヶー]{1,4}(?:\s[一-龠々ァ-ヶー]{1,4})?)と申します)|(?:担当(?:者)?\s*:\s*(?<n>[^\s(]+(?:\s[^\s(]+)?))|(?:の(?<n>[一-龠々]{1,4})(?:です|でございます))")]
    private static partial Regex PersonPattern();

    [GeneratedRegex(@"納期|納品|希望|まで|着|届|deliver|by", RegexOptions.IgnoreCase)]
    private static partial Regex DeliveryKeyword();

    [GeneratedRegex(@"(?<y>\d{4})[/\-年](?<m>\d{1,2})[/\-月](?<d>\d{1,2})")]
    private static partial Regex FullDate();

    [GeneratedRegex(@"(?<m>\d{1,2})[/月](?<d>\d{1,2})")]
    private static partial Regex MonthDay();
}
