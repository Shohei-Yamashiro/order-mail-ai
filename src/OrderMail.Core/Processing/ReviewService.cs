using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Processing;

/// <summary>Human decisions on the review screen. A reviewed mail is no longer changed by re-analysis.</summary>
public sealed class ReviewService(IDbContextFactory<OrderMailDbContext> dbFactory, TimeProvider time)
{
    public event Action? Changed;

    /// <exception cref="ArgumentException">The draft is invalid.</exception>
    public async Task ConfirmOrderAsync(int mailId, OrderDraft draft, CancellationToken ct = default)
    {
        var errors = draft.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mail = await LoadAsync(db, mailId, ct);
        var now = time.GetUtcNow().UtcDateTime;

        mail.Order ??= new Order { CreatedAt = now };
        draft.ApplyTo(mail.Order);
        mail.Order.RegisteredBy = RegisteredBy.Human;
        mail.Order.UpdatedAt = now;
        mail.Status = MailStatus.Registered;
        mail.ReviewedAt = now;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
    }

    public async Task MarkNotOrderAsync(int mailId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mail = await LoadAsync(db, mailId, ct);
        if (mail.Order is not null) db.Orders.Remove(mail.Order);
        mail.Status = MailStatus.NotOrder;
        mail.ReviewedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>Puts a reviewed mail back into the review queue, keeping any registered order.</summary>
    public async Task ReopenAsync(int mailId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mail = await LoadAsync(db, mailId, ct);
        mail.ReviewedAt = null;
        if (mail.Order is null) mail.Status = MailStatus.NeedsReview;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// For debugging: forgets every human decision. Orders a person confirmed are deleted and the mails
    /// return to unanalyzed so the next run analyzes them again. Unreviewed mails and analysis history are kept.
    /// </summary>
    /// <returns>The number of mails that were reviewed.</returns>
    public async Task<int> ClearAllReviewsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mails = await db.Mails.Include(m => m.Order).Where(m => m.ReviewedAt != null).ToListAsync(ct);
        foreach (var mail in mails)
        {
            if (mail.Order is not null) db.Orders.Remove(mail.Order);
            mail.ReviewedAt = null;
            mail.Status = MailStatus.Unanalyzed;
            mail.Confidence = null;
        }
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return mails.Count;
    }

    public async Task<int> CountReviewedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Mails.CountAsync(m => m.ReviewedAt != null, ct);
    }

    private static Task<ReceivedMail> LoadAsync(OrderMailDbContext db, int mailId, CancellationToken ct) =>
        db.Mails.Include(m => m.Order).ThenInclude(o => o!.Items).SingleAsync(m => m.Id == mailId, ct);
}
