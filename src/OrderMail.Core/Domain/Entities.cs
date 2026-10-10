namespace OrderMail.Core.Domain;

public enum MailStatus
{
    Unanalyzed,
    NeedsReview,
    Registered,
    NotOrder,
    Failed,
}

public enum RegisteredBy
{
    Auto,
    Human,
}

public class ReceivedMail
{
    public int Id { get; set; }
    public string MessageId { get; set; } = "";
    public string Source { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime ImportedAt { get; set; }
    public MailStatus Status { get; set; }
    public double? Confidence { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? LastError { get; set; }

    public List<MailAnalysis> Analyses { get; set; } = [];
    public Order? Order { get; set; }

    public bool IsReviewed => ReviewedAt is not null;
}

public class MailAnalysis
{
    public int Id { get; set; }
    public int ReceivedMailId { get; set; }
    public string Analyzer { get; set; } = "";
    public bool IsOrder { get; set; }
    public double Confidence { get; set; }
    public string? Reason { get; set; }
    public string ResultJson { get; set; } = "";
    public long ElapsedMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int ReceivedMailId { get; set; }
    public ReceivedMail? ReceivedMail { get; set; }
    public string? CustomerCompany { get; set; }
    public string? CustomerName { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public string? Notes { get; set; }
    public RegisteredBy RegisteredBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int LineNo { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>Code in the product master, when the line was matched to one.</summary>
    public string? ProductCode { get; set; }
}

/// <summary>A row of the product master, searched to identify products in mails (RAG).</summary>
public class Product
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Other names customers use, separated by "|".</summary>
    public string? Aliases { get; set; }

    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>Normalized float32 vector of <see cref="SearchText"/>; null until embeddings are built.</summary>
    public byte[]? Embedding { get; set; }

    public string? EmbeddingModel { get; set; }

    public string SearchText => string.IsNullOrWhiteSpace(Aliases) ? $"{Code} {Name}" : $"{Code} {Name}（{Aliases.Replace("|", "、")}）";
}
