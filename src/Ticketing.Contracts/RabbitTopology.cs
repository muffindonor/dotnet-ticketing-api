using RabbitMQ.Client;

namespace Ticketing.Contracts;

public static class RabbitTopology
{
    public static string DeadLetterQueue(string queue) => $"{queue}.dead";

    /// <summary>
    /// Declares every queue with identical arguments, whichever service starts first.
    /// A message rejected without requeue moves to its dead-letter queue instead of vanishing.
    /// </summary>
    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        foreach (var queue in new[] { Queues.PaymentRequested, Queues.PaymentCompleted })
        {
            var deadLetter = DeadLetterQueue(queue);
            await channel.QueueDeclareAsync(
                deadLetter, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await channel.QueueDeclareAsync(
                queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = deadLetter,
                },
                cancellationToken: ct);
        }
    }

    /// <summary>
    /// Completes when the channel closes, including when the broker goes away, so the
    /// caller's loop can reconnect instead of waiting forever on a dead consumer.
    /// </summary>
    public static async Task WaitUntilClosedAsync(this IChannel channel, CancellationToken ct)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        if (channel.IsClosed)
        {
            closed.TrySetResult();
        }

        await closed.Task.WaitAsync(ct);
    }
}
