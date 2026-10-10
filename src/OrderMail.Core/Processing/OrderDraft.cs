using OrderMail.Core.Analysis;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Processing;

public static class RegistrationPolicy
{
    public static MailStatus Decide(OrderExtraction result, double threshold)
    {
        if (result.Confidence < threshold) return MailStatus.NeedsReview;
        if (!result.IsOrder) return MailStatus.NotOrder;
        return result.Items.Count > 0 ? MailStatus.Registered : MailStatus.NeedsReview;
    }
}

/// <summary>Editable order shown on the review screen.</summary>
public sealed class OrderDraft
{
    public string? CustomerCompany { get; set; }
    public string? CustomerName { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? Notes { get; set; }
    public List<OrderDraftItem> Items { get; set; } = [];

    public static OrderDraft From(OrderExtraction e) => new()
    {
        CustomerCompany = e.CustomerCompany,
        CustomerName = e.CustomerName,
        DeliveryDate = e.DeliveryDate?.ToDateTime(TimeOnly.MinValue),
        Notes = e.Notes,
        Items = e.Items.Select(i => new OrderDraftItem { ProductName = i.ProductName, Quantity = i.Quantity, Unit = i.Unit, UnitPrice = i.UnitPrice, ProductCode = i.ProductCode }).ToList(),
    };

    public static OrderDraft From(Order o) => new()
    {
        CustomerCompany = o.CustomerCompany,
        CustomerName = o.CustomerName,
        DeliveryDate = o.DeliveryDate?.ToDateTime(TimeOnly.MinValue),
        Notes = o.Notes,
        Items = o.Items.OrderBy(i => i.LineNo).Select(i => new OrderDraftItem { ProductName = i.ProductName, Quantity = i.Quantity, Unit = i.Unit, UnitPrice = i.UnitPrice, ProductCode = i.ProductCode }).ToList(),
    };

    /// <summary>Returns error messages; empty when the draft can be registered.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Items.Count == 0) errors.Add("明細を1行以上入力してください。");
        for (var i = 0; i < Items.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Items[i].ProductName)) errors.Add($"{i + 1}行目: 品名を入力してください。");
            if (Items[i].Quantity <= 0) errors.Add($"{i + 1}行目: 数量は1以上にしてください。");
            if (Items[i].UnitPrice < 0) errors.Add($"{i + 1}行目: 単価は0以上にしてください。");
        }
        return errors;
    }

    public void ApplyTo(Order order)
    {
        order.CustomerCompany = Trim(CustomerCompany);
        order.CustomerName = Trim(CustomerName);
        order.DeliveryDate = DeliveryDate is { } d ? DateOnly.FromDateTime(d) : null;
        order.Notes = Trim(Notes);
        order.Items.Clear();
        var line = 1;
        foreach (var item in Items)
        {
            order.Items.Add(new OrderItem
            {
                LineNo = line++,
                ProductName = item.ProductName.Trim(),
                Quantity = item.Quantity,
                Unit = Trim(item.Unit),
                UnitPrice = item.UnitPrice,
                ProductCode = Trim(item.ProductCode),
            });
        }
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class OrderDraftItem
{
    public string ProductName { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? ProductCode { get; set; }
}
