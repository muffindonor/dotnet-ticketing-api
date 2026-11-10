using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Ticketing.Api.Options;

namespace Ticketing.Api.Messaging;

public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true } existing)
            {
                return existing;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Value.Uri),
                // Each background loop reconnects itself. The library's recovery is off because
                // two recovery mechanisms on one connection can tear each other's work down.
                AutomaticRecoveryEnabled = false,
            };
            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
