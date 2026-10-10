using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Processing;

public sealed record DataCounts(int Mails, int Orders, int Reviewed);

/// <summary>For debugging: clears imported data so that the same mails can be processed again from scratch.</summary>
public sealed class DataResetService(IDbContextFactory<OrderMailDbContext> dbFactory)
{
    public async Task<DataCounts> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new DataCounts(
            await db.Mails.CountAsync(ct),
            await db.Orders.CountAsync(ct),
            await db.Mails.CountAsync(m => m.ReviewedAt != null, ct));
    }

    /// <summary>
    /// Deletes every imported mail with its analyses and order. The next run imports the mails again
    /// (as long as they are still in the mail folder or on the server). The product master is kept.
    /// </summary>
    /// <returns>The number of deleted mails.</returns>
    public async Task<int> ClearMailsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.OrderItems.ExecuteDeleteAsync(ct);
        await db.Orders.ExecuteDeleteAsync(ct);
        await db.Analyses.ExecuteDeleteAsync(ct);
        var count = await db.Mails.ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }

    /// <summary>
    /// Deletes every order, automatic or confirmed by a person. Their mails lose any human decision and
    /// return to unanalyzed so the next run analyzes them again. Other mails and analysis history are kept.
    /// </summary>
    /// <returns>The number of deleted orders.</returns>
    public async Task<int> ClearOrdersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mailIds = await db.Orders.Select(o => o.ReceivedMailId).ToListAsync(ct);
        await db.OrderItems.ExecuteDeleteAsync(ct);
        var count = await db.Orders.ExecuteDeleteAsync(ct);
        await db.Mails.Where(m => mailIds.Contains(m.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MailStatus.Unanalyzed)
            .SetProperty(m => m.ReviewedAt, (DateTime?)null)
            .SetProperty(m => m.Confidence, (double?)null), ct);
        await tx.CommitAsync(ct);
        return count;
    }
}
