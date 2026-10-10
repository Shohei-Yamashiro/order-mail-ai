using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Data;

namespace OrderMail.Core.Processing;

/// <summary>
/// Keyword search over the mail list: subject, sender, body, and the registered order's customer and product names.
/// Every space-separated term must appear somewhere; full-width and half-width forms and letter case are ignored.
/// </summary>
public static class MailSearch
{
    public static IReadOnlyList<string> Terms(string? query) =>
        Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(IReadOnlyList<string> terms, IEnumerable<string?> fields)
    {
        if (terms.Count == 0) return true;
        var text = string.Join("\n", fields.Select(Normalize));
        return terms.All(text.Contains);
    }

    /// <returns>Ids of the matching mails, or null when the query has no terms (everything matches).</returns>
    public static async Task<HashSet<int>?> FindAsync(OrderMailDbContext db, string? query, CancellationToken ct = default)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return null;
        var mails = await db.Mails
            .Select(m => new
            {
                m.Id,
                m.Subject,
                m.FromName,
                m.FromAddress,
                m.Body,
                Customer = m.Order == null ? null : m.Order.CustomerCompany,
                Person = m.Order == null ? null : m.Order.CustomerName,
                Products = m.Order == null ? new List<string>() : m.Order.Items.Select(i => i.ProductName).ToList(),
            })
            .ToListAsync(ct);
        return mails
            .Where(m => Matches(terms, [m.Subject, m.FromName, m.FromAddress, m.Body, m.Customer, m.Person, .. m.Products]))
            .Select(m => m.Id)
            .ToHashSet();
    }

    private static string Normalize(string? text) =>
        (text ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}
