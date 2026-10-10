using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Catalog;

public sealed record CatalogStats(int Count, int Embedded);

public sealed record CatalogImport(int Count, int KeptVectors, IReadOnlyList<string> Warnings);

/// <summary>Product search for one processing run; embedding errors fall back to keyword search for the rest of the run.</summary>
public sealed class CatalogSearch(ProductIndex index, IEmbedder? embedder, int candidateCount, ILogger logger)
{
    private IEmbedder? _embedder = index.HasVectors ? embedder : null;

    public ProductIndex Index => index;
    public bool UsesVectors => _embedder is not null;

    public async Task<IReadOnlyList<ProductHit>> SearchAsync(string subject, string body, CancellationToken ct)
    {
        var lines = ProductIndex.QueryLines(subject, body);
        float[][]? vectors = null;
        if (_embedder is not null && lines.Count > 0)
        {
            try
            {
                vectors = await _embedder.EmbedAsync(lines, ct);
            }
            catch (AiConnectionException e)
            {
                logger.LogWarning(e, "Embedding failed; product search uses keywords only");
                _embedder = null;
            }
        }
        return index.Search(lines, vectors, candidateCount);
    }

    public async Task<IReadOnlyList<CatalogProduct>> CandidatesAsync(MailInput mail, CancellationToken ct) =>
        (await SearchAsync(mail.Subject, mail.Body, ct))
            .Select(h => new CatalogProduct(h.Product.Code, h.Product.Name, h.Product.Unit, h.Product.UnitPrice))
            .ToList();
}

/// <summary>Imports the product master, builds embeddings and opens searches over it (RAG).</summary>
public sealed class ProductCatalogService(
    IDbContextFactory<OrderMailDbContext> dbFactory,
    AnalyzerFactory analyzers,
    string baseDirectory,
    ILogger<ProductCatalogService> logger)
{
    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

    /// <summary>Replaces the product master with the CSV. Vectors are kept for products whose text did not change.</summary>
    public async Task<CatalogImport> ImportCsvAsync(string path, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath)) return new(0, 0, [$"ファイルが見つかりません: {fullPath}"]);
        var parsed = ProductCsv.Parse(await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct));
        if (parsed.Products.Count == 0) return new(0, 0, parsed.Warnings);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var old = await db.Products.ToListAsync(ct);
        var oldByCode = old.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var kept = 0;
        foreach (var product in parsed.Products)
        {
            if (oldByCode.TryGetValue(product.Code, out var previous) && previous.SearchText == product.SearchText && previous.Embedding is not null)
            {
                product.Embedding = previous.Embedding;
                product.EmbeddingModel = previous.EmbeddingModel;
                kept++;
            }
        }
        db.Products.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        db.Products.AddRange(parsed.Products);
        await db.SaveChangesAsync(ct);
        return new(parsed.Products.Count, kept, parsed.Warnings);
    }

    /// <summary>Loads the CSV in the settings when the master is empty, so the samples work right after unzipping.</summary>
    public async Task ImportIfEmptyAsync(CatalogSettings settings, CancellationToken ct = default)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.CsvPath) || !File.Exists(ResolvePath(settings.CsvPath))) return;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Products.AnyAsync(ct)) return;
        }
        var result = await ImportCsvAsync(settings.CsvPath, ct);
        logger.LogInformation("Imported {Count} products from {Path}", result.Count, settings.CsvPath);
    }

    public async Task<CatalogStats> StatsAsync(string? embeddingModel, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = embeddingModel?.Trim() ?? "";
        return new(
            await db.Products.CountAsync(ct),
            model.Length == 0 ? 0 : await db.Products.CountAsync(p => p.Embedding != null && p.EmbeddingModel == model, ct));
    }

    /// <summary>Embeds products that have no vector for the configured model. Returns how many were built.</summary>
    public async Task<int> BuildEmbeddingsAsync(AiSettings ai, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var embedder = analyzers.CreateEmbedder(ai) ?? throw new InvalidOperationException("埋め込みモデルが設定されていません。");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var targets = await db.Products.Where(p => p.Embedding == null || p.EmbeddingModel != embedder.Model).ToListAsync(ct);
        var done = 0;
        foreach (var batch in targets.Chunk(32))
        {
            progress?.Report($"ベクトル作成中 {done}/{targets.Count}");
            var vectors = await embedder.EmbedAsync(batch.Select(p => p.SearchText).ToList(), ct);
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i].Embedding = ProductIndex.ToBytes(vectors[i]);
                batch[i].EmbeddingModel = embedder.Model;
            }
            await db.SaveChangesAsync(ct);
            done += batch.Length;
        }
        return done;
    }

    /// <summary>Null when the catalog is disabled or empty.</summary>
    public async Task<CatalogSearch?> OpenAsync(OrderMailDbContext db, AppSettings settings, CancellationToken ct = default)
    {
        if (!settings.Catalog.Enabled) return null;
        var embedder = analyzers.CreateEmbedder(settings.Ai);
        var index = await ProductIndex.LoadAsync(db, embedder?.Model, ct);
        return index.Count == 0 ? null : new CatalogSearch(index, embedder, Math.Clamp(settings.Catalog.CandidateCount, 1, 20), logger);
    }

    public async Task<IReadOnlyList<ProductHit>> SearchAsync(string text, AppSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var search = await OpenAsync(db, settings, ct);
        return search is null ? [] : await search.SearchAsync("", text, ct);
    }

    public async Task<IReadOnlyList<CatalogProduct>> CandidatesAsync(MailInput mail, AppSettings settings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var search = await OpenAsync(db, settings, ct);
        return search is null ? [] : await search.CandidatesAsync(mail, ct);
    }
}
