using OrderMail.Core.Domain;

namespace OrderMail.Core.Analysis;

public sealed record MailInput(string Subject, string FromAddress, string FromName, string Body, DateTime ReceivedAtUtc)
{
    public static MailInput From(ReceivedMail mail) =>
        new(mail.Subject, mail.FromAddress, mail.FromName, mail.Body, mail.ReceivedAt);
}

public sealed record ExtractedItem(string ProductName, decimal Quantity, string? Unit, decimal? UnitPrice, string? ProductCode = null);

public sealed record OrderExtraction
{
    public bool IsOrder { get; init; }
    public double Confidence { get; init; }
    public string? Reason { get; init; }
    public string? CustomerCompany { get; init; }
    public string? CustomerName { get; init; }
    public DateOnly? DeliveryDate { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<ExtractedItem> Items { get; init; } = [];
}

/// <summary>A product-master row found for the mail and shown to the AI.</summary>
public sealed record CatalogProduct(string Code, string Name, string? Unit, decimal? UnitPrice);

/// <summary>What the app found before asking the analyzer: confirmed similar mails and candidate products.</summary>
public sealed record AnalysisContext(IReadOnlyList<AnalysisExample> Examples, IReadOnlyList<CatalogProduct> Products)
{
    public static AnalysisContext Empty { get; } = new([], []);
}

public interface IOrderAnalyzer
{
    string Name { get; }

    /// <param name="context">Examples and candidate products; analyzers that cannot use them ignore them.</param>
    Task<OrderExtraction> AnalyzeAsync(MailInput mail, AnalysisContext context, CancellationToken ct);
}

public static class OrderAnalyzerExtensions
{
    public static Task<OrderExtraction> AnalyzeAsync(this IOrderAnalyzer analyzer, MailInput mail, IReadOnlyList<AnalysisExample> examples, CancellationToken ct) =>
        analyzer.AnalyzeAsync(mail, new AnalysisContext(examples, []), ct);
}
