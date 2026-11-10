using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ticketing.PaymentWorker;
using Ticketing.Api.Controllers;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Options;
using Ticketing.Api.Services;
using Ticketing.Contracts;
using Xunit;

namespace Ticketing.Tests;

[Collection(ContainersCollection.Name)]
public sealed class HardeningTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly CapturedLogs _logs = new();
    private TicketingFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = containers.CreateFactory("TicketingTests", services =>
        {
            services.AddHostedService<PaymentRequestConsumer>();
            services.AddSingleton<ILoggerProvider>(_logs);
        });
        _admin = await _factory.LoginAdminAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<OrderDto> HoldAsync(HttpClient client, Guid eventId, Guid seatId)
    {
        var response = await client.PostAsync($"/api/events/{eventId}/seats/{seatId}/hold", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    private Task WithDbAsync(Func<TicketingDbContext, Task> action) =>
        WithScopeAsync(services => action(services.GetRequiredService<TicketingDbContext>()));

    private Task SweepAsync() =>
        WithScopeAsync(services => services.GetRequiredService<HoldExpiryService>().SweepAsync(CancellationToken.None));

    private async Task<EventResponseDto> GetEventAsync(Guid eventId) =>
        (await _admin.GetFromJsonAsync<EventResponseDto>($"/api/events/{eventId}"))!;

    private async Task<EventResponseDto> CreateEventAsync(int seats)
    {
        var response = await _admin.PostAsJsonAsync("/api/events", new CreateEventRequest(
            "Test concert",
            "Test hall",
            DateTimeOffset.UtcNow.AddDays(30),
            Enumerable.Range(1, seats).Select(i => new SeatRequest($"S{i}", 1000)).ToList()));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventResponseDto>())!;
    }

    private static async Task<OrderDto> WaitUntilSettledAsync(HttpClient client, Guid orderId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        OrderDto order;
        do
        {
            order = (await client.GetFromJsonAsync<OrderDto>($"/api/orders/{orderId}"))!;
            if (order.Status is not ("Pending" or "PaymentProcessing"))
            {
                return order;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"Order was still {order.Status} after 30 seconds.");
        return order;
    }

    [Fact]
    public async Task Seat_cannot_be_held_after_the_event_starts_and_past_events_are_unlisted()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        await WithDbAsync(db => db.Events.Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var customer = await _factory.RegisterCustomerAsync();

        var hold = await customer.PostAsync($"/api/events/{eventId}/seats/{seatId}/hold", null);
        var listed = await _admin.GetFromJsonAsync<List<EventSummaryResponse>>("/api/events?pageSize=100");

        Assert.Equal(HttpStatusCode.Conflict, hold.StatusCode);
        Assert.DoesNotContain(listed!, e => e.Id == eventId);
    }

    [Fact]
    public async Task Invalid_events_are_rejected()
    {
        var future = DateTimeOffset.UtcNow.AddDays(1);

        var past = await _admin.PostAsJsonAsync("/api/events",
            new CreateEventRequest("X", "Y", DateTimeOffset.UtcNow.AddDays(-1), [new SeatRequest("A1", 1)]));
        var negativePrice = await _admin.PostAsJsonAsync("/api/events",
            new CreateEventRequest("X", "Y", future, [new SeatRequest("A1", -5)]));
        var duplicateLabels = await _admin.PostAsJsonAsync("/api/events",
            new CreateEventRequest("X", "Y", future, [new SeatRequest("A1", 1), new SeatRequest("A1", 1)]));

        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negativePrice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateLabels.StatusCode);
    }

    [Fact]
    public async Task Event_list_is_paged_and_rejects_oversized_pages()
    {
        await _admin.CreateSingleSeatEventAsync();
        await _admin.CreateSingleSeatEventAsync();

        var one = await _admin.GetFromJsonAsync<List<EventSummaryResponse>>("/api/events?page=1&pageSize=1");
        var oversized = await _admin.GetAsync("/api/events?pageSize=101");

        Assert.Single(one!);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Duplicate_payment_result_does_not_change_a_paid_order()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var customer = await _factory.RegisterCustomerAsync();
        var order = await HoldAsync(customer, eventId, seatId);
        await customer.PostAsJsonAsync($"/api/orders/{order.Id}/pay", new PayRequest("tok_ok"));
        Assert.Equal("Paid", (await WaitUntilSettledAsync(customer, order.Id)).Status);

        await WithScopeAsync(services => services.GetRequiredService<OrderPaymentService>()
            .ApplyResultAsync(new PaymentCompleted(order.Id, false, "Late duplicate."), CancellationToken.None));

        Assert.Equal("Paid", (await customer.GetFromJsonAsync<OrderDto>($"/api/orders/{order.Id}"))!.Status);
        Assert.Equal("Sold", (await GetEventAsync(eventId)).Seats.Single().Status);
    }

    [Fact]
    public async Task Stuck_payment_times_out_and_a_late_success_does_not_resell_the_seat()
    {
        var (eventId, seatId) = await _admin.CreateSingleSeatEventAsync();
        var customer = await _factory.RegisterCustomerAsync();
        var order = await HoldAsync(customer, eventId, seatId);

        // The state left behind when a payment message is lost.
        await WithDbAsync(db => db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.Status, OrderStatus.PaymentProcessing)
            .SetProperty(o => o.PaymentStartedAt, DateTimeOffset.UtcNow.AddHours(-1))));

        await SweepAsync();

        var timedOut = await customer.GetFromJsonAsync<OrderDto>($"/api/orders/{order.Id}");
        Assert.Equal("PaymentFailed", timedOut!.Status);
        Assert.Equal("Payment timed out.", timedOut.FailureReason);
        Assert.Equal("Available", (await GetEventAsync(eventId)).Seats.Single().Status);

        await WithScopeAsync(services => services.GetRequiredService<OrderPaymentService>()
            .ApplyResultAsync(new PaymentCompleted(order.Id, true, null), CancellationToken.None));

        Assert.Equal("PaymentFailed", (await customer.GetFromJsonAsync<OrderDto>($"/api/orders/{order.Id}"))!.Status);
        Assert.Equal("Available", (await GetEventAsync(eventId)).Seats.Single().Status);
        Assert.Contains(_logs.Messages, m => m.Contains(order.Id.ToString()) && m.Contains("A refund is required"));
    }

    [Fact]
    public async Task Expiry_racing_payment_never_leaves_order_and_seat_disagreeing()
    {
        const int seats = 30;
        var created = await CreateEventAsync(seats);
        var customer = await _factory.RegisterCustomerAsync();
        var orders = new List<OrderDto>();
        foreach (var seat in created.Seats)
        {
            orders.Add(await HoldAsync(customer, created.Id, seat.Id));
        }

        // Payments are spread across the instant the holds expire, with sweeps running throughout.
        var ids = orders.Select(o => o.Id).ToList();
        await WithDbAsync(db => db.Orders.Where(o => ids.Contains(o.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTimeOffset.UtcNow.AddMilliseconds(300))));

        var payments = orders.Select(async (order, i) =>
        {
            await Task.Delay(i * 20);
            var response = await customer.PostAsJsonAsync($"/api/orders/{order.Id}/pay", new PayRequest("tok_ok"));
            return (order.Id, response.StatusCode);
        }).ToList();
        var sweeps = Enumerable.Range(0, 12).Select(async i =>
        {
            await Task.Delay(i * 60);
            await SweepAsync();
        }).ToList();

        var results = await Task.WhenAll(payments);
        await Task.WhenAll(sweeps);
        await Task.Delay(350);
        await SweepAsync();

        var details = await GetEventAsync(created.Id);
        foreach (var (orderId, status) in results)
        {
            var settled = await WaitUntilSettledAsync(customer, orderId);
            var seatId = orders.Single(o => o.Id == orderId).SeatId;
            details = await GetEventAsync(created.Id);
            var seatStatus = details.Seats.Single(s => s.Id == seatId).Status;

            if (status == HttpStatusCode.Accepted)
            {
                Assert.Equal("Paid", settled.Status);
                Assert.Equal("Sold", seatStatus);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, status);
                Assert.Equal("Expired", settled.Status);
                Assert.Equal("Available", seatStatus);
            }
        }
    }

    [Fact]
    public async Task One_sweep_clears_more_than_a_single_batch()
    {
        const int overdue = 205;
        var created = await CreateEventAsync(overdue);

        await WithDbAsync(async db =>
        {
            var userId = await db.Users.Select(u => u.Id).FirstAsync();
            var seats = await db.Seats.Where(s => s.EventId == created.Id).ToListAsync();
            foreach (var seat in seats)
            {
                var orderId = Guid.NewGuid();
                seat.Status = SeatStatus.Held;
                seat.CurrentOrderId = orderId;
                db.Orders.Add(new Order
                {
                    Id = orderId,
                    UserId = userId,
                    SeatId = seat.Id,
                    AmountCents = 1000,
                    Status = OrderStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                });
            }

            await db.SaveChangesAsync();
        });

        await SweepAsync();

        Assert.All((await GetEventAsync(created.Id)).Seats, s => Assert.Equal("Available", s.Status));
    }

    [Fact]
    public async Task Readiness_reports_a_reachable_database()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_login_attempts_are_rate_limited()
    {
        await using var limited = containers.CreateFactory("TicketingRateLimit", services =>
        {
            services.RemoveAll<IHostedService>();
            services.PostConfigure<AuthRateLimitOptions>(o => o.AuthRequestsPerMinute = 3);
        });
        var client = limited.CreateClient();
        var attempt = new CredentialsRequest("nobody@tests.local", "Wrong_Password_1!");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", attempt)).StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests],
            statuses);
    }
}
