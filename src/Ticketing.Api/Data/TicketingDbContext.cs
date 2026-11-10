using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

public sealed class TicketingDbContext(DbContextOptions<TicketingDbContext> options) : DbContext(options)
{
    public const string ActiveOrderIndex = "IX_Orders_SeatId_Active";

    public DbSet<User> Users => Set<User>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(254);
            e.HasIndex(u => u.Email).IsUnique();
        });

        model.Entity<Event>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Venue).HasMaxLength(200);
        });

        model.Entity<Seat>(e =>
        {
            e.Property(s => s.Label).HasMaxLength(32);
            e.HasIndex(s => new { s.EventId, s.Label }).IsUnique();
            e.HasIndex(s => s.CurrentOrderId);
            e.HasOne<Event>().WithMany(x => x.Seats).HasForeignKey(s => s.EventId);
        });

        model.Entity<Order>(e =>
        {
            e.Property(o => o.FailureReason).HasMaxLength(200);
            e.HasOne<User>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Seat>().WithMany().HasForeignKey(o => o.SeatId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(o => new { o.Status, o.ExpiresAt });
            e.HasIndex(o => o.UserId);

            // Second line of defence: even a bug in the application cannot give one seat
            // two live orders. 0, 1 and 2 are Pending, PaymentProcessing and Paid.
            e.HasIndex(o => o.SeatId)
                .IsUnique()
                .HasDatabaseName(ActiveOrderIndex)
                .HasFilter("[Status] IN (0, 1, 2)");
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.Property(m => m.Destination).HasMaxLength(100);
            e.HasIndex(m => m.CreatedAt);
            e.HasIndex(m => m.OrderId);
        });
    }
}
