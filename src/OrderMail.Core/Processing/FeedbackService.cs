using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Processing;

/// <summary>Accuracy in daily use: what the analyzer answered versus what a person confirmed, per analyzer.</summary>
public sealed record FeedbackReport(EvaluationReport Overall, IReadOnlyList<EvaluationReport> ByAnalyzer)
{
    public int Corrected => Overall.Rows.Count(r => !r.AllOk);
}

/// <summary>
/// Compares each confirmed mail with the analysis the person saw when confirming it (the latest one before the review),
/// so corrections show which fields the AI gets wrong without running the AI again.
/// </summary>
public sealed class FeedbackService(IDbContextFactory<OrderMailDbContext> dbFactory)
{
    public async Task<FeedbackReport> BuildAsync(double autoRegisterThreshold, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mails = await db.Mails
            .AsNoTracking()
            .Include(m => m.Order).ThenInclude(o => o!.Items)
            .Include(m => m.Analyses)
            .Where(m => m.ReviewedAt != null && (m.Status == MailStatus.NotOrder || (m.Status == MailStatus.Registered && m.Order != null)))
            .ToListAsync(ct);

        var rows = new List<(string Analyzer, EvaluationRow Row)>();
        foreach (var mail in mails.OrderByDescending(m => m.ReviewedAt))
        {
            var seen = mail.Analyses.Where(a => a.CreatedAt <= mail.ReviewedAt).MaxBy(a => a.CreatedAt);
            if (seen is null) continue;
            OrderExtraction actual;
            try
            {
                actual = ExtractionJson.Parse(seen.ResultJson);
            }
            catch (FormatException)
            {
                continue;
            }
            var auto = RegistrationPolicy.Decide(actual, autoRegisterThreshold) != MailStatus.NeedsReview;
            var row = new EvaluationRow(mail.Id, mail.Subject, ExampleSelector.ConfirmedResult(mail), actual, null, auto, seen.ElapsedMs);
            rows.Add((BaseName(seen.Analyzer), row));
        }

        var overall = new EvaluationReport("すべての解析", 0, rows.Select(r => r.Row).ToList());
        var byAnalyzer = rows
            .GroupBy(r => r.Analyzer)
            .OrderByDescending(g => g.Count())
            .Select(g => new EvaluationReport(g.Key, 0, g.Select(r => r.Row).ToList()))
            .ToList();
        return new FeedbackReport(overall, byAnalyzer);
    }

    /// <summary>"AI（qwen2.5:7b）・手本3件" and "AI（qwen2.5:7b）・手本1件" are the same analyzer.</summary>
    public static string BaseName(string analyzerLabel)
    {
        var i = analyzerLabel.IndexOf("・手本", StringComparison.Ordinal);
        return i < 0 ? analyzerLabel : analyzerLabel[..i];
    }
}
