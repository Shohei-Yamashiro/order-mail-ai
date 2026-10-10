using System.Globalization;
using System.Text;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Catalog;

/// <summary>Reads the product master CSV: a header row with code, name, aliases, unit, unit_price (UTF-8).</summary>
public static class ProductCsv
{
    public sealed record ParseResult(IReadOnlyList<Product> Products, IReadOnlyList<string> Warnings);

    public static ParseResult Parse(string text)
    {
        var rows = Rows(text).ToList();
        var warnings = new List<string>();
        if (rows.Count == 0) return new([], ["CSV が空です。"]);

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Column(string name) => header.IndexOf(name);
        int code = Column("code"), name = Column("name"), aliases = Column("aliases"), unit = Column("unit"), price = Column("unit_price");
        if (code < 0 || name < 0) return new([], ["1 行目に code と name の列が必要です（例: code,name,aliases,unit,unit_price）。"]);

        var products = new List<Product>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            string? Cell(int index) => index >= 0 && index < row.Count && !string.IsNullOrWhiteSpace(row[index]) ? row[index].Trim() : null;
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var lineNo = i + 1;
            var c = Cell(code);
            var n = Cell(name);
            if (c is null || n is null) { warnings.Add($"{lineNo} 行目: code または name が空のため読み飛ばしました。"); continue; }
            if (!seen.Add(c)) { warnings.Add($"{lineNo} 行目: コード {c} が重複しているため読み飛ばしました。"); continue; }

            decimal? unitPrice = null;
            if (Cell(price) is { } p)
            {
                if (decimal.TryParse(p.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) unitPrice = value;
                else warnings.Add($"{lineNo} 行目: 単価「{p}」を数値として読めないため空にしました。");
            }
            products.Add(new Product { Code = c, Name = n, Aliases = Cell(aliases), Unit = Cell(unit), UnitPrice = unitPrice });
        }
        return new(products, warnings);
    }

    private static IEnumerable<List<string>> Rows(string text)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n')
            {
                row.Add(cell.ToString()); cell.Clear();
                yield return row;
                row = [];
            }
            else if (c != '\r' && c != '\uFEFF') cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row;
        }
    }
}
