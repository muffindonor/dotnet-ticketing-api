using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Ticketing.Api.Data;
using Ticketing.Contracts;

namespace Ticketing.Api.Messaging;

/// <summary>
/// Publishes saved outbox rows to RabbitMQ. A row is deleted only after the broker confirms
/// it, so delivery is at-least-once and consumers must tolerate duplicates.
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes,
    RabbitMqConnection rabbit,
    ILogger<OutboxPublisher> log) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (channel is not { IsOpen: true })
                    {
                        channel = await OpenChannelAsync(stoppingToken);
                    }

                    var published = await PublishBatchAsync(channel, stoppingToken);
                    if (published == 0)
                    {
                        await Task.Delay(IdleDelay, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Outbox publish failed. Retrying in {Delay}", RetryDelay);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
        }
    }

    private async Task<IChannel> OpenChannelAsync(CancellationToken ct)
    {
        var connection = await rabbit.GetAsync(ct);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            ct);
        await RabbitTopology.DeclareAsync(channel, ct);
        return channel;
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // UPDLOCK claims these rows and READPAST skips rows another API instance has claimed,
        // so several instances can run side by side without publishing the same message.
        var batch = await db.OutboxMessages
            .FromSql($"SELECT TOP ({BatchSize}) * FROM [OutboxMessages] WITH (UPDLOCK, READPAST, ROWLOCK) ORDER BY [CreatedAt]")
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.Id.ToString(),
            };
            await channel.BasicPublishAsync(
                exchange: "",
                routingKey: message.Destination,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.Payload),
                cancellationToken: ct);
        }

        db.OutboxMessages.RemoveRange(batch);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }
}
