using OrderMail.Core.Domain;

namespace OrderMail.Core.Analysis;

public sealed record MailInput(string Subject, string FromAddress, string FromName, string Body, DateTime ReceivedAtUtc)
{
    public static MailInput From(ReceivedMail mail) =>
        new(mail.Subject, mail.FromAddress, mail.FromName, mail.Body, mail.ReceivedAt);
}

public sealed record ExtractedItem(string ProductName, decimal Quantity, string? Unit, decimal? UnitPrice);

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

public interface IOrderAnalyzer
{
    string Name { get; }
    Task<OrderExtraction> AnalyzeAsync(MailInput mail, CancellationToken ct);
}
