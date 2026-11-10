using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Options;

namespace Ticketing.Api.Services;

public sealed class HoldExpiryService(
    TicketingDbContext db,
    TimeProvider time,
    IOptions<BookingOptions> options)
{
    private const int BatchSize = 200;

    /// <summary>Frees seats from overdue holds and stuck payments, and returns how many.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var paymentCutoff = now.AddMinutes(-options.Value.PaymentTimeoutMinutes);

        var expired = await ReleaseAsync(
            o => o.Status == OrderStatus.Pending && o.ExpiresAt <= now,
            OrderStatus.Expired,
            failureReason: null,
            ct);

        // Without this, one lost message would lock a seat forever.
        var timedOut = await ReleaseAsync(
            o => o.Status == OrderStatus.PaymentProcessing && o.PaymentStartedAt <= paymentCutoff,
            OrderStatus.PaymentFailed,
            "Payment timed out.",
            ct);

        return expired + timedOut;
    }

    private async Task<int> ReleaseAsync(
        Expression<Func<Order, bool>> isDue,
        OrderStatus newStatus,
        string? failureReason,
        CancellationToken ct)
    {
        var total = 0;
        int releasedInBatch;
        do
        {
            var dueOrderIds = await db.Orders
                .Where(isDue)
                .OrderBy(o => o.ExpiresAt)
                .Select(o => o.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            releasedInBatch = 0;
            foreach (var orderId in dueOrderIds)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                // Conditional, so a payment that just started, or another API instance
                // sweeping at the same moment, simply wins and this order is skipped.
                var changed = await db.Orders
                    .Where(o => o.Id == orderId)
                    .Where(isDue)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(o => o.Status, newStatus)
                        .SetProperty(o => o.FailureReason, failureReason), ct);

                if (changed == 1)
                {
                    await db.Seats
                        .Where(s => s.CurrentOrderId == orderId && s.Status == SeatStatus.Held)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(s => s.Status, SeatStatus.Available)
                            .SetProperty(s => s.CurrentOrderId, (Guid?)null), ct);

                    // A payment request that was never sent must not charge a timed-out order.
                    await db.OutboxMessages.Where(m => m.OrderId == orderId).ExecuteDeleteAsync(ct);
                    releasedInBatch++;
                }

                await tx.CommitAsync(ct);
            }

            total += releasedInBatch;
        }
        while (releasedInBatch == BatchSize);

        return total;
    }
}

public sealed class HoldExpirySweeper(
    IServiceScopeFactory scopes,
    IOptions<BookingOptions> options,
    ILogger<HoldExpirySweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ExpirySweepSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var released = await scope.ServiceProvider
                        .GetRequiredService<HoldExpiryService>()
                        .SweepAsync(stoppingToken);
                    if (released > 0)
                    {
                        log.LogInformation("Released {Count} seats", released);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Sweep failed. It will run again on the next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
