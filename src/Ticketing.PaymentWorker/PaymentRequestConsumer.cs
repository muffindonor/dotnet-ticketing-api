using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ticketing.Contracts;

namespace Ticketing.PaymentWorker;

/// <summary>
/// Stands in for a real payment provider. A real integration would pass OrderId to the
/// provider as its idempotency key, so a redelivered message could not charge twice.
/// </summary>
public sealed class PaymentRequestConsumer(
    IConfiguration configuration,
    ILogger<PaymentRequestConsumer> log) : BackgroundService
{
    public const string DeclinedToken = "tok_declined";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConsumeAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Payment consumer failed. Retrying in {Delay}", RetryDelay);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var uri = configuration["RabbitMq:Uri"]
            ?? throw new InvalidOperationException("RabbitMq:Uri is not configured.");
        var delay = TimeSpan.FromMilliseconds(configuration.GetValue("Payments:SimulatedDelayMs", 200));

        var factory = new ConnectionFactory
        {
            Uri = new Uri(uri),
            // The loop in ExecuteAsync reconnects, so the library's own recovery stays off.
            AutomaticRecoveryEnabled = false,
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            ct);

        await RabbitTopology.DeclareAsync(channel, ct);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 16, global: false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            PaymentRequested request;
            try
            {
                request = PaymentMessages.ParseRequested(delivery.Body.Span);
            }
            catch (JsonException ex)
            {
                log.LogError(ex, "Malformed payment request sent to the dead-letter queue");
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
                return;
            }

            try
            {
                PaymentCompleted result;
                if (request.ChargeBefore < DateTimeOffset.UtcNow)
                {
                    // Sat in a queue too long. The order may have timed out, so don't take money.
                    result = new PaymentCompleted(request.OrderId, false, "Payment request expired.");
                }
                else
                {
                    await Task.Delay(delay, ct);
                    var declined = request.PaymentToken == DeclinedToken;
                    result = new PaymentCompleted(request.OrderId, !declined, declined ? "Card declined." : null);
                }

                // Publish first, then ack. A crash between the two causes a duplicate
                // result, which the API ignores, rather than a lost one.
                await channel.BasicPublishAsync(
                    exchange: "",
                    routingKey: Queues.PaymentCompleted,
                    mandatory: true,
                    basicProperties: new BasicProperties { Persistent = true, ContentType = "application/json" },
                    body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result)),
                    cancellationToken: ct);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not process order {OrderId}. Retrying in {Delay}", request.OrderId, RetryDelay);
                await Task.Delay(RetryDelay, ct);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
            }
        };

        await channel.BasicConsumeAsync(Queues.PaymentRequested, autoAck: false, consumer, ct);
        await channel.WaitUntilClosedAsync(ct);
        log.LogWarning("Payment channel closed. Reconnecting");
    }
}
