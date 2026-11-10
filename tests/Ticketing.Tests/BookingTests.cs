using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ticketing.Api.Controllers;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;
using Xunit;

namespace Ticketing.Tests;

[Collection(ContainersCollection.Name)]
public sealed class BookingTests(ContainersFixture containers) : IAsyncLifetime
{
    private TicketingFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = containers.CreateFullSystem("TicketingTests");
        _admin = await _factory.LoginAdminAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static string HoldUrl(Guid eventId, Guid seatId) => $"/api/events/{eventId}/seats/{seatId}/hold";

    private static Task<HttpResponseMessage> HoldAsync(HttpClient client, Guid eventId, Guid seatId) =>
        client.PostAsync(HoldUrl(eventId, seatId), null);

    private static async Task<OrderDto> HoldOrderAsync(HttpClient client, Guid eventId, Guid seatId)
    {
        var response = await HoldAsync(client, eventId, seatId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private async Task<string> SeatStatusAsync(Guid eventId, Guid seatId)
    {
        var details = await _admin.GetFromJsonAsync<EventResponseDto>($"/api/events/{eventId}");
        return details!.Seats.Single(s => s.Id == seatId).Status;
    }

    private static async Task<OrderDto> WaitForOrderAsync(HttpClient client, Guid orderId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        OrderDto order;
        do
        {
            order = (await client.GetFromJsonAsync<OrderDto>($"/api/orders/{orderId}"))!;
            if (order.Status == expected)
            {
                return order;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"Order stayed {order.Status}, expected {expected}.");
        return order;
    }

    private async Task WithDbAsync(Func<TicketingDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TicketingDbContext>());
    }

    [Fact]
    public async Task Second_customer_is_refused_a_held_seat()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var first = await _factory.RegisterCustomerAsync();
        var second = await _factory.RegisterCustomerAsync();

        var order = await HoldOrderAsync(first, eventId, seatId);
        var refused = await HoldAsync(second, eventId, seatId);

        Assert.Equal("Pending", order.Status);
        Assert.Equal(15_000, order.AmountCents);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Held", await SeatStatusAsync(eventId, seatId));
    }

    [Fact]
    public async Task Concurrent_holds_produce_exactly_one_winner()
    {
        const int requests = 100;
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var customer = await _factory.RegisterCustomerAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, requests).Select(_ => HoldAsync(customer, eventId, seatId)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(requests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await WithDbAsync(async db => Assert.Equal(1, await db.Orders.CountAsync(o => o.SeatId == seatId)));
    }

    [Fact]
    public async Task Database_rejects_a_second_live_order_for_one_seat()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var customer = await _factory.RegisterCustomerAsync();
        await HoldOrderAsync(customer, eventId, seatId);

        await WithDbAsync(async db =>
        {
            var existing = await db.Orders.AsNoTracking().SingleAsync(o => o.SeatId == seatId);
            db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                UserId = existing.UserId,
                SeatId = seatId,
                AmountCents = 1,
                Status = OrderStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            });

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(TicketingDbContext.IsUniqueViolation(ex));
        });
    }

    [Fact]
    public async Task Expired_hold_frees_the_seat_for_the_next_customer()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var first = await _factory.RegisterCustomerAsync();
        var order = await HoldOrderAsync(first, eventId, seatId);

        await WithDbAsync(db => db.Orders.Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<HoldExpiryService>().SweepAsync(CancellationToken.None);
        }

        Assert.Equal("Expired", (await first.GetFromJsonAsync<OrderDto>($"/api/orders/{order.Id}"))!.Status);
        Assert.Equal("Available", await SeatStatusAsync(eventId, seatId));

        var next = await _factory.RegisterCustomerAsync();
        Assert.Equal("Pending", (await HoldOrderAsync(next, eventId, seatId)).Status);
    }

    [Fact]
    public async Task Expired_order_cannot_be_paid()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var customer = await _factory.RegisterCustomerAsync();
        var order = await HoldOrderAsync(customer, eventId, seatId);

        await WithDbAsync(db => db.Orders.Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

        var response = await customer.PostAsJsonAsync($"/api/orders/{order.Id}/pay", new PayRequest("tok_ok"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Successful_payment_travels_through_the_queue_and_sells_the_seat()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var customer = await _factory.RegisterCustomerAsync();
        var order = await HoldOrderAsync(customer, eventId, seatId);

        var response = await customer.PostAsJsonAsync($"/api/orders/{order.Id}/pay", new PayRequest("tok_ok"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForOrderAsync(customer, order.Id, "Paid");
        Assert.Equal("Sold", await SeatStatusAsync(eventId, seatId));
    }

    [Fact]
    public async Task Declined_payment_releases_the_seat()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var customer = await _factory.RegisterCustomerAsync();
        var order = await HoldOrderAsync(customer, eventId, seatId);

        await customer.PostAsJsonAsync($"/api/orders/{order.Id}/pay", new PayRequest("tok_declined"));

        var failed = await WaitForOrderAsync(customer, order.Id, "PaymentFailed");
        Assert.Equal("Card declined.", failed.FailureReason);
        Assert.Equal("Available", await SeatStatusAsync(eventId, seatId));
    }

    [Fact]
    public async Task Customer_cannot_see_or_pay_someone_elses_order()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var owner = await _factory.RegisterCustomerAsync();
        var stranger = await _factory.RegisterCustomerAsync();
        var order = await HoldOrderAsync(owner, eventId, seatId);

        var read = await stranger.GetAsync($"/api/orders/{order.Id}");
        var pay = await stranger.PostAsJsonAsync($"/api/orders/{order.Id}/pay", new PayRequest("tok_ok"));

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, pay.StatusCode);
    }

    [Fact]
    public async Task Only_admins_can_create_events()
    {
        var request = new CreateEventRequest("X", "Y", DateTimeOffset.UtcNow, [new SeatRequest("A1", 1)]);
        var customer = await _factory.RegisterCustomerAsync();

        var anonymous = await _factory.CreateClient().PostAsJsonAsync("/api/events", request);
        var forbidden = await customer.PostAsJsonAsync("/api/events", request);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
