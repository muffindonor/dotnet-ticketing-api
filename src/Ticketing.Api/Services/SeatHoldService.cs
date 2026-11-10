using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Options;

namespace Ticketing.Api.Services;

public enum HoldOutcome { Held, SeatTaken, SeatNotFound, EventStarted }

public sealed record HoldResult(HoldOutcome Outcome, Order? Order = null);

public interface ISeatHoldService
{
    Task<HoldResult> HoldAsync(Guid eventId, Guid seatId, Guid userId, CancellationToken ct);
}

public sealed class SeatHoldService(
    TicketingDbContext db,
    TimeProvider time,
    IOptions<BookingOptions> options) : ISeatHoldService
{
    public async Task<HoldResult> HoldAsync(Guid eventId, Guid seatId, Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // A fast way to turn most requests away in a rush. It never grants a seat:
        // several requests can pass this check, and the UPDATE below picks the one winner.
        var seat = await (
            from s in db.Seats
            join e in db.Events on s.EventId equals e.Id
            where s.Id == seatId && s.EventId == eventId
            select new { s.Status, s.PriceCents, e.StartsAt })
            .SingleOrDefaultAsync(ct);

        if (seat is null)
        {
            return new HoldResult(HoldOutcome.SeatNotFound);
        }

        if (seat.StartsAt <= now)
        {
            return new HoldResult(HoldOutcome.EventStarted);
        }

        if (seat.Status != SeatStatus.Available)
        {
            return new HoldResult(HoldOutcome.SeatTaken);
        }

        var orderId = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The whole race is decided by this one UPDATE. SQL Server serialises writers on the
        // row, so exactly one request sees Status = Available and affects a row. Everyone
        // else affects zero rows. Acting on the read above instead would not be safe.
        var claimed = await db.Seats
            .Where(s => s.Id == seatId && s.EventId == eventId && s.Status == SeatStatus.Available)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, SeatStatus.Held)
                .SetProperty(s => s.CurrentOrderId, orderId), ct);

        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return new HoldResult(HoldOutcome.SeatTaken);
        }

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            SeatId = seatId,
            AmountCents = seat.PriceCents,
            Status = OrderStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(options.Value.HoldMinutes),
        };
        db.Orders.Add(order);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (TicketingDbContext.IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            return new HoldResult(HoldOutcome.SeatTaken);
        }

        await tx.CommitAsync(ct);
        return new HoldResult(HoldOutcome.Held, order);
    }
}
