using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Processing;

/// <summary>Picks mails a person has confirmed that look like the target, to send to the AI as worked examples.</summary>
public static class ExampleSelector
{
    private const int MaxCandidates = 300;

    public static async Task<IReadOnlyList<AnalysisExample>> SelectAsync(OrderMailDbContext db, ReceivedMail target, int count, CancellationToken ct)
    {
        if (count <= 0) return [];

        var candidates = await db.Mails
            .AsNoTracking()
            .Include(m => m.Order).ThenInclude(o => o!.Items)
            .Where(m => m.ReviewedAt != null && m.Id != target.Id && m.MessageId != target.MessageId
                && (m.Status == MailStatus.NotOrder || (m.Status == MailStatus.Registered && m.Order != null)))
            .OrderByDescending(m => m.ReviewedAt)
            .Take(MaxCandidates)
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var targetGrams = Bigrams(target.Subject + "\n" + target.Body);
        return candidates
            .Select(c => (Mail: c, Score: Similarity(target, targetGrams, c)))
            .OrderByDescending(x => x.Score)
            .Take(count)
            .Reverse()
            .Select(x => new AnalysisExample(MailInput.From(x.Mail), ConfirmedResult(x.Mail)))
            .ToList();
    }

    /// <summary>The answer a person confirmed, in the same shape the AI returns.</summary>
    public static OrderExtraction ConfirmedResult(ReceivedMail mail)
    {
        if (mail.Status != MailStatus.Registered || mail.Order is not { } order)
            return new OrderExtraction { IsOrder = false, Confidence = 0.95, Reason = "発注の依頼ではない。" };

        return new OrderExtraction
        {
            IsOrder = true,
            Confidence = 0.95,
            Reason = "商品と数量を示して発注している。",
            CustomerCompany = order.CustomerCompany,
            CustomerName = order.CustomerName,
            DeliveryDate = order.DeliveryDate,
            Notes = order.Notes,
            Items = order.Items.OrderBy(i => i.LineNo).Select(i => new ExtractedItem(i.ProductName, i.Quantity, i.Unit, i.UnitPrice)).ToList(),
        };
    }

    /// <summary>Same sender counts most, then same domain, then shared character pairs in subject and body.</summary>
    private static double Similarity(ReceivedMail target, HashSet<string> targetGrams, ReceivedMail candidate)
    {
        var score = Jaccard(targetGrams, Bigrams(candidate.Subject + "\n" + candidate.Body));
        if (SameText(target.FromAddress, candidate.FromAddress)) score += 2;
        else if (SameText(Domain(target.FromAddress), Domain(candidate.FromAddress))) score += 1;
        return score;
    }

    private static bool SameText(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? null : address[(at + 1)..];
    }

    private static HashSet<string> Bigrams(string text)
    {
        var chars = text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)).ToArray();
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < chars.Length; i++) set.Add(new string(chars, i, 2));
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }
}
