using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Catalog;

/// <param name="Keyword">Share of the product name's character pairs found in the mail (0-1).</param>
/// <param name="Vector">Cosine similarity of the embeddings, when both sides have them.</param>
public sealed record ProductHit(Product Product, double Keyword, double? Vector);

/// <summary>
/// In-memory search over the product master: keyword (character bigram) search, optional vector search,
/// combined by reciprocal rank fusion. Load once per processing run.
/// </summary>
public sealed class ProductIndex
{
    public const double MinKeywordScore = 0.34;

    /// <summary>A product is matched without AI only when it scores this well and clearly beats the runner-up.</summary>
    public const double ResolveScore = 0.6;
    public const double ResolveMargin = 0.15;

    private const int RrfK = 60;
    private const int MaxQueryLines = 40;
    private const int MaxQueryLineChars = 200;

    private readonly List<Entry> _entries;
    private readonly Dictionary<string, Entry> _byCode;

    private ProductIndex(List<Entry> entries)
    {
        _entries = entries;
        _byCode = entries.GroupBy(e => Normalize(e.Product.Code)).ToDictionary(g => g.Key, g => g.First());
    }

    public int Count => _entries.Count;

    /// <summary>True when every product has a vector for the embedding model in use.</summary>
    public bool HasVectors => _entries.Count > 0 && _entries.All(e => e.Vector is not null);

    public static ProductIndex Create(IEnumerable<Product> products, string? embeddingModel = null) =>
        new(products.Select(p => new Entry(p, embeddingModel)).ToList());

    public static async Task<ProductIndex> LoadAsync(OrderMailDbContext db, string? embeddingModel, CancellationToken ct) =>
        Create(await db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync(ct), embeddingModel);

    /// <summary>Lines of the subject and body that may name products (quoted lines are skipped).</summary>
    public static IReadOnlyList<string> QueryLines(string subject, string body)
    {
        var lines = new List<string>();
        foreach (var raw in (subject + "\n" + body).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line.StartsWith('>')) continue;
            lines.Add(line.Length > MaxQueryLineChars ? line[..MaxQueryLineChars] : line);
            if (lines.Count == MaxQueryLines) break;
        }
        return lines;
    }

    /// <param name="lineVectors">Embeddings of <paramref name="lines"/>; null for keyword search only.</param>
    public IReadOnlyList<ProductHit> Search(IReadOnlyList<string> lines, float[][]? lineVectors, int top)
    {
        if (_entries.Count == 0 || top <= 0) return [];
        var queries = lines.Select(Normalize).Where(q => q.Length > 0).ToList();
        var hits = _entries.Select(e => new ProductHit(
            e.Product,
            queries.Count == 0 ? 0 : queries.Max(e.KeywordScore),
            lineVectors is { Length: > 0 } && e.Vector is not null ? lineVectors.Max(v => Dot(v, e.Vector)) : null)).ToList();

        var fused = new Dictionary<ProductHit, double>();
        var byKeyword = hits.Where(h => h.Keyword >= MinKeywordScore).OrderByDescending(h => h.Keyword).ToList();
        for (var i = 0; i < byKeyword.Count; i++) fused[byKeyword[i]] = 1.0 / (RrfK + i + 1);
        var byVector = hits.Where(h => h.Vector is not null).OrderByDescending(h => h.Vector).Take(top * 2).ToList();
        for (var i = 0; i < byVector.Count; i++) fused[byVector[i]] = fused.GetValueOrDefault(byVector[i]) + 1.0 / (RrfK + i + 1);

        return fused.OrderByDescending(f => f.Value).ThenByDescending(f => f.Key.Keyword).Take(top).Select(f => f.Key).ToList();
    }

    public Product? FindByCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : _byCode.GetValueOrDefault(Normalize(code))?.Product;

    /// <summary>
    /// Keeps only product codes that exist in the master, matches lines without a code when the name is unambiguous,
    /// and fills the unit and unit price the mail did not state from the master.
    /// </summary>
    public OrderExtraction Resolve(OrderExtraction extraction)
    {
        if (_entries.Count == 0 || extraction.Items.Count == 0) return extraction;
        return extraction with
        {
            Items = extraction.Items.Select(item =>
            {
                var product = FindByCode(item.ProductCode) ?? BestMatch(item.ProductName);
                return product is null
                    ? item with { ProductCode = null }
                    : item with { ProductCode = product.Code, Unit = item.Unit ?? product.Unit, UnitPrice = item.UnitPrice ?? product.UnitPrice };
            }).ToList(),
        };
    }

    /// <summary>The one product an extracted product name refers to, or null when none or several fit.</summary>
    public Product? BestMatch(string productName)
    {
        var query = Normalize(productName);
        if (query.Length < 2) return null;
        var ranked = _entries.Select(e => (e.Product, Score: e.NameScore(query))).OrderByDescending(r => r.Score).Take(2).ToList();
        if (ranked.Count == 0 || ranked[0].Score < ResolveScore) return null;
        if (ranked.Count == 1) return ranked[0].Product;
        var (best, second) = (ranked[0].Score, ranked[1].Score);
        var onlyExact = best >= 1 && second < 1;
        return best - second >= ResolveMargin || onlyExact ? ranked[0].Product : null;
    }

    /// <summary>Full-width to half-width, lower case, letters and digits only.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] ToVector(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static HashSet<string> Bigrams(string normalized)
    {
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < normalized.Length; i++) set.Add(normalized.Substring(i, 2));
        return set;
    }

    private sealed class Entry
    {
        private readonly string _code;
        private readonly List<HashSet<string>> _names;

        public Entry(Product product, string? embeddingModel)
        {
            Product = product;
            _code = Normalize(product.Code);
            _names = new[] { product.Name }.Concat((product.Aliases ?? "").Split('|'))
                .Select(Normalize).Where(n => n.Length >= 2).Select(Bigrams).ToList();
            if (product.Embedding is { Length: > 0 } && !string.IsNullOrEmpty(embeddingModel) && product.EmbeddingModel == embeddingModel)
                Vector = ToVector(product.Embedding);
        }

        public Product Product { get; }
        public float[]? Vector { get; }

        /// <summary>1 when the code appears in the text; otherwise the best share of a name's bigrams found in it.</summary>
        public double KeywordScore(string normalizedQuery)
        {
            if (_code.Length >= 3 && normalizedQuery.Contains(_code, StringComparison.Ordinal)) return 1;
            var grams = Bigrams(normalizedQuery);
            var best = 0.0;
            foreach (var name in _names)
                best = Math.Max(best, (double)name.Count(grams.Contains) / name.Count);
            return best;
        }

        /// <summary>Like <see cref="KeywordScore"/>, but a short name such as "キッチンペーパー" also fits a longer master name.</summary>
        public double NameScore(string normalizedName)
        {
            var score = KeywordScore(normalizedName);
            var grams = Bigrams(normalizedName);
            if (grams.Count == 0) return score;
            foreach (var name in _names)
                score = Math.Max(score, (double)grams.Count(name.Contains) / grams.Count);
            return score;
        }
    }
}
