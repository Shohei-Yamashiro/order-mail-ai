using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Domain;

namespace OrderMail.Core.Data;

public sealed class OrderMailDbContext(DbContextOptions<OrderMailDbContext> options) : DbContext(options)
{
    public DbSet<ReceivedMail> Mails => Set<ReceivedMail>();
    public DbSet<MailAnalysis> Analyses => Set<MailAnalysis>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReceivedMail>(e =>
        {
            e.HasIndex(m => m.MessageId).IsUnique();
            e.HasIndex(m => m.ReceivedAt);
            e.Property(m => m.Status).HasConversion<string>();
            e.HasMany(m => m.Analyses).WithOne().HasForeignKey(a => a.ReceivedMailId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Order).WithOne(o => o.ReceivedMail).HasForeignKey<Order>(o => o.ReceivedMailId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.HasIndex(o => o.ReceivedMailId).IsUnique();
            e.Property(o => o.RegisteredBy).HasConversion<string>();
            e.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
