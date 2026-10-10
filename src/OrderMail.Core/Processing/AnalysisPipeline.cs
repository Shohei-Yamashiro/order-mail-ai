using OrderMail.Core.Analysis;
using OrderMail.Core.Catalog;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Processing;

public sealed record PipelineResult(OrderExtraction Result, string AnalyzerLabel, int ExampleCount, int ProductCount);

/// <summary>
/// One analysis as used in daily processing and in evaluation: pick confirmed examples and candidate products (AI only),
/// ask the analyzer, then check product codes against the master and fill prices (also for the rule-based analyzer).
/// </summary>
public sealed class AnalysisPipeline(IOrderAnalyzer analyzer, CatalogSearch? catalog, AppSettings settings)
{
    public IOrderAnalyzer Analyzer => analyzer;

    private bool UsesAi => settings.Ai.Provider == AiProvider.OpenAiCompatible;

    public async Task<PipelineResult> AnalyzeAsync(OrderMailDbContext db, ReceivedMail mail, CancellationToken ct)
    {
        var input = MailInput.From(mail);
        var examples = UsesAi ? await ExampleSelector.SelectAsync(db, mail, settings.Ai.ExampleCount, ct) : [];
        var products = UsesAi && catalog is not null ? await catalog.CandidatesAsync(input, ct) : [];

        var result = await analyzer.AnalyzeAsync(input, new AnalysisContext(examples, products), ct);
        if (catalog is not null) result = catalog.Index.Resolve(result);
        return new PipelineResult(result, Label(analyzer, examples.Count, products.Count), examples.Count, products.Count);
    }

    public static string Label(IOrderAnalyzer analyzer, int exampleCount, int productCount)
    {
        var label = analyzer.Name;
        if (exampleCount > 0) label += $"・手本{exampleCount}件";
        if (productCount > 0) label += $"・候補{productCount}件";
        return label;
    }
}
