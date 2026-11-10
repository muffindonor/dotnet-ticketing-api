using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Ticketing.Api.Controllers;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Messaging;
using Ticketing.Api.Services;
using Ticketing.Contracts;
using Xunit;

namespace Ticketing.Tests;

[Collection(ContainersCollection.Name)]
public sealed class ResilienceTests(ContainersFixture containers)
{
    private static void NoBackgroundServices(IServiceCollection services) => services.RemoveAll<IHostedService>();

    private Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { Uri = new Uri(containers.RabbitUri) }.CreateConnectionAsync();

    private static async Task WithDbAsync(TicketingFactory factory, Func<TicketingDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TicketingDbContext>());
    }

    private static async Task<OrderDto> HoldAsync(HttpClient client, Guid eventId, Guid seatId)
    {
        var response = await client.PostAsync($"/api/events/{eventId}/seats/{seatId}/hold", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    [Fact]
    public async Task Atomic_claim_alone_gives_one_winner_without_the_unique_index()
    {
        const int requests = 100;
        await using var factory = containers.CreateFactory("TicketingNoIndex", NoBackgroundServices);
        var admin = await factory.LoginAdminAsync();
        await WithDbAsync(factory, db => db.Database.ExecuteSqlRawAsync(
            $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{TicketingDbContext.ActiveOrderIndex}') " +
            $"DROP INDEX [{TicketingDbContext.ActiveOrderIndex}] ON [Orders]"));

        var (eventId, seatId) = await admin.CreateSingleSeatEventAsync();
        var customer = await factory.RegisterCustomerAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, requests)
            .Select(_ => customer.PostAsync($"/api/events/{eventId}/seats/{seatId}/hold", null)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(requests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Two_outbox_publishers_never_send_the_same_row()
    {
        const int rows = 1000;
        var queue = $"outbox-test-{Guid.NewGuid():N}";
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);

        static void PublisherOnly(IServiceCollection services)
        {
            services.RemoveAll<IHostedService>();
            services.AddHostedService<OutboxPublisher>();
        }

        await using var first = containers.CreateFactory("TicketingOutbox", PublisherOnly);
        await using var second = containers.CreateFactory("TicketingOutbox", PublisherOnly);
        _ = first.Services;
        _ = second.Services;

        await WithDbAsync(first, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.OutboxMessages.AddRange(Enumerable.Range(0, rows).Select(i => new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Destination = queue,
                Payload = "{}",
                CreatedAt = now.AddTicks(i),
            }));
            await db.SaveChangesAsync();
        });

        var deadline = DateTime.UtcNow.AddSeconds(90);
        var remaining = rows;
        while (remaining > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            await WithDbAsync(first, async db => remaining = await db.OutboxMessages.CountAsync(m => m.Destination == queue));
        }

        Assert.Equal(0, remaining);

        var delivered = new List<string>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
        {
            delivered.Add(message.BasicProperties.MessageId!);
        }

        await channel.QueueDeleteAsync(queue);
        Assert.Equal(rows, delivered.Count);
        Assert.Equal(rows, delivered.Distinct().Count());
    }

    [Theory]
    [InlineData(Queues.PaymentCompleted, "{}")]
    [InlineData(Queues.PaymentCompleted, "not json")]
    [InlineData(Queues.PaymentRequested, "{}")]
    [InlineData(Queues.PaymentRequested, "not json")]
    public async Task Malformed_messages_go_to_the_dead_letter_queue(string queue, string body)
    {
        await using var factory = containers.CreateFullSystem("TicketingTests");
        _ = factory.Services;
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitTopology.DeclareAsync(channel, CancellationToken.None);
        var deadLetter = RabbitTopology.DeadLetterQueue(queue);
        await channel.QueuePurgeAsync(deadLetter);

        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: queue,
            mandatory: false,
            basicProperties: new BasicProperties(),
            body: Encoding.UTF8.GetBytes(body));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        uint count;
        do
        {
            await Task.Delay(100);
            count = await channel.MessageCountAsync(deadLetter);
        }
        while (count == 0 && DateTime.UtcNow < deadline);

        Assert.Equal(1u, count);
        await channel.QueuePurgeAsync(deadLetter);
    }

    [Fact]
    public async Task Payment_request_past_its_deadline_is_refused_without_charging()
    {
        await using var factory = containers.CreateFullSystem("TicketingTests");
        var admin = await factory.LoginAdminAsync();
        var (eventId, seatId) = await admin.CreateSingleSeatEventAsync();
        var customer = await factory.RegisterCustomerAsync();
        var order = await HoldAsync(customer, eventId, seatId);

        await WithDbAsync(factory, async db =>
        {
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatus.PaymentProcessing)
                .SetProperty(o => o.PaymentStartedAt, DateTimeOffset.UtcNow));
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Destination = Queues.PaymentRequested,
                Payload = JsonSerializer.Serialize(
                    new PaymentRequested(order.Id, 15_000, "tok_ok", DateTimeOffset.UtcNow.AddMinutes(-1))),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        var deadline = DateTime.UtcNow.AddSeconds(30);
        OrderDto current;
        do
        {
            await Task.Delay(100);
            current = (await customer.GetFromJsonAsync<OrderDto>($"/api/orders/{order.Id}"))!;
        }
        while (current.Status == "PaymentProcessing" && DateTime.UtcNow < deadline);

        Assert.Equal("PaymentFailed", current.Status);
        Assert.Equal("Payment request expired.", current.FailureReason);
    }

    [Fact]
    public async Task Timing_out_a_payment_cancels_its_unsent_request()
    {
        await using var factory = containers.CreateFactory("TicketingTests", NoBackgroundServices);
        var admin = await factory.LoginAdminAsync();
        var (eventId, seatId) = await admin.CreateSingleSeatEventAsync();
        var customer = await factory.RegisterCustomerAsync();
        var order = await HoldAsync(customer, eventId, seatId);
        await customer.PostAsJsonAsync($"/api/orders/{order.Id}/pay", new PayRequest("tok_ok"));

        await WithDbAsync(factory, async db =>
        {
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.OrderId == order.Id));
            await db.Orders.Where(o => o.Id == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentStartedAt, DateTimeOffset.UtcNow.AddHours(-1)));
        });

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<HoldExpiryService>().SweepAsync(CancellationToken.None);
        }

        await WithDbAsync(factory, async db =>
            Assert.Equal(0, await db.OutboxMessages.CountAsync(m => m.OrderId == order.Id)));
    }

    [Theory]
    [InlineData("a1", "A1")]
    [InlineData("A1", "A1 ")]
    public async Task Seat_labels_the_database_treats_as_equal_are_rejected(string first, string second)
    {
        await using var factory = containers.CreateFactory("TicketingTests", NoBackgroundServices);
        var admin = await factory.LoginAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/events", new CreateEventRequest(
            "X", "Y", DateTimeOffset.UtcNow.AddDays(1), [new SeatRequest(first, 1), new SeatRequest(second, 1)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
