using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Options;
using Ticketing.Contracts;

namespace Ticketing.Api.Services;

public enum StartPaymentOutcome { Started, NotFound, NotPayable }

public sealed class OrderPaymentService(
    TicketingDbContext db,
    TimeProvider time,
    IOptions<BookingOptions> options,
    ILogger<OrderPaymentService> log)
{
    public async Task<StartPaymentOutcome> StartAsync(Guid orderId, Guid userId, string paymentToken, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Moving out of Pending also stops the expiry sweep from taking the seat mid-payment.
        var started = await db.Orders
            .Where(o => o.Id == orderId && o.UserId == userId
                && o.Status == OrderStatus.Pending && o.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Status, OrderStatus.PaymentProcessing)
                .SetProperty(o => o.PaymentStartedAt, now), ct);

        if (started == 0)
        {
            await tx.RollbackAsync(ct);
            var owned = await db.Orders.AnyAsync(o => o.Id == orderId && o.UserId == userId, ct);
            return owned ? StartPaymentOutcome.NotPayable : StartPaymentOutcome.NotFound;
        }

        var amount = await db.Orders.Where(o => o.Id == orderId).Select(o => o.AmountCents).SingleAsync(ct);

        // Half the timeout, so a charge can't start just as the order is about to be timed out.
        var chargeBefore = now.AddMinutes(options.Value.PaymentTimeoutMinutes / 2.0);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Destination = Queues.PaymentRequested,
            Payload = JsonSerializer.Serialize(new PaymentRequested(orderId, amount, paymentToken, chargeBefore)),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StartPaymentOutcome.Started;
    }

    /// <summary>Safe to call twice for one order: a duplicate delivery changes no rows.</summary>
    public async Task ApplyResultAsync(PaymentCompleted result, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var applied = await db.Orders
            .Where(o => o.Id == result.OrderId && o.Status == OrderStatus.PaymentProcessing)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Status, result.Succeeded ? OrderStatus.Paid : OrderStatus.PaymentFailed)
                .SetProperty(o => o.PaidAt, result.Succeeded ? now : null)
                .SetProperty(o => o.FailureReason, result.FailureReason), ct);

        if (applied == 0)
        {
            await tx.RollbackAsync(ct);
            if (result.Succeeded)
            {
                var status = await db.Orders.Where(o => o.Id == result.OrderId)
                    .Select(o => (OrderStatus?)o.Status)
                    .SingleOrDefaultAsync(ct);

                // A duplicate of a success is harmless. A success for an order that already
                // timed out means money was taken for a seat the customer doesn't have.
                if (status != OrderStatus.Paid)
                {
                    log.LogError(
                        "Payment succeeded for order {OrderId} in status {Status}. A refund is required",
                        result.OrderId,
                        status);
                }
            }

            return;
        }

        var seats = db.Seats.Where(s => s.CurrentOrderId == result.OrderId);
        if (result.Succeeded)
        {
            await seats.ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, SeatStatus.Sold), ct);
        }
        else
        {
            await seats.ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, SeatStatus.Available)
                .SetProperty(s => s.CurrentOrderId, (Guid?)null), ct);
        }

        await tx.CommitAsync(ct);
    }
}
