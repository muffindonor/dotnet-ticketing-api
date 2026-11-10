using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ticketing.Api.Services;
using Ticketing.Contracts;

namespace Ticketing.Api.Messaging;

public sealed class PaymentResultConsumer(
    IServiceScopeFactory scopes,
    RabbitMqConnection rabbit,
    ILogger<PaymentResultConsumer> log) : BackgroundService
{
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
                    log.LogWarning(ex, "Payment result consumer failed. Retrying in {Delay}", RetryDelay);
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
        var connection = await rabbit.GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await RabbitTopology.DeclareAsync(channel, ct);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 16, global: false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            PaymentCompleted result;
            try
            {
                result = PaymentMessages.ParseCompleted(delivery.Body.Span);
            }
            catch (JsonException ex)
            {
                log.LogError(ex, "Malformed payment result sent to the dead-letter queue");
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
                return;
            }

            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OrderPaymentService>().ApplyResultAsync(result, ct);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A valid result can be a customer's money, so it is never given up on. It waits
                // here, however long an outage lasts, and stays on the queue until it applies.
                log.LogError(ex, "Could not apply result for order {OrderId}. Retrying in {Delay}", result.OrderId, RetryDelay);
                await Task.Delay(RetryDelay, ct);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
            }
        };

        await channel.BasicConsumeAsync(Queues.PaymentCompleted, autoAck: false, consumer, ct);
        await channel.WaitUntilClosedAsync(ct);
        log.LogWarning("Payment result channel closed. Reconnecting");
    }
}
