using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;
using Xunit;
using Xunit.Abstractions;

namespace Ticketing.Tests;

/// <summary>
/// Shows that the concurrency test can actually fail: with both protections removed, the same
/// kind of burst double-books. It relies on a real race, so CI excludes it (Category=Control).
/// </summary>
[Collection(ContainersCollection.Name)]
public sealed class NaiveControlTest(ContainersFixture containers, ITestOutputHelper output)
{
    private sealed class NaiveSeatHoldService(TicketingDbContext db) : ISeatHoldService
    {
        public async Task<HoldResult> HoldAsync(Guid eventId, Guid seatId, Guid userId, CancellationToken ct)
        {
            var seat = await db.Seats.SingleAsync(s => s.Id == seatId && s.EventId == eventId, ct);
            if (seat.Status != SeatStatus.Available)
            {
                return new HoldResult(HoldOutcome.SeatTaken);
            }

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SeatId = seatId,
                AmountCents = seat.PriceCents,
                Status = OrderStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            };
            seat.Status = SeatStatus.Held;
            seat.CurrentOrderId = order.Id;
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            return new HoldResult(HoldOutcome.Held, order);
        }
    }

    [Fact]
    [Trait("Category", "Control")]
    public async Task Naive_read_then_write_double_books_when_protections_are_removed()
    {
        const int requests = 100;
        await using var factory = containers.CreateFactory("TicketingControl", services =>
        {
            // This test has its own database but shares the broker, so its background
            // services must not consume messages meant for the real tests.
            services.RemoveAll<IHostedService>();
            services.RemoveAll<ISeatHoldService>();
            services.AddScoped<ISeatHoldService, NaiveSeatHoldService>();
        });

        var admin = await factory.LoginAdminAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                $"DROP INDEX [{TicketingDbContext.ActiveOrderIndex}] ON [Orders]");
        }

        var (eventId, seatId) = await admin.CreateSingleSeatEventAsync();
        var customer = await factory.RegisterCustomerAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, requests)
            .Select(_ => customer.PostAsync($"/api/events/{eventId}/seats/{seatId}/hold", null)));

        var winners = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        output.WriteLine($"Naive control: {requests} requests for one seat, {winners} customers were given it.");
        Assert.True(winners > 1, $"Expected a double booking, but there were {winners} winners.");
    }
}
