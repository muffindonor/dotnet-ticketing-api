using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Ticketing.Api;
using Ticketing.Api.Controllers;
using Ticketing.PaymentWorker;
using Xunit;

namespace Ticketing.Tests;

[CollectionDefinition(Name)]
public sealed class ContainersCollection : ICollectionFixture<ContainersFixture>
{
    public const string Name = "containers";
}

/// <summary>One real SQL Server and one real RabbitMQ, shared by every test.</summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@tests.local";
    public const string AdminPassword = "Test_Admin_Password_1!";

    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();

    public string RabbitUri => _rabbit.GetConnectionString();

    public string SqlConnectionString(string database) =>
        new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    public TicketingFactory CreateFactory(string database, Action<IServiceCollection> configure) =>
        new(SqlConnectionString(database), RabbitUri, configure);

    /// <summary>The API plus an in-process payment worker, wired to the real broker.</summary>
    public TicketingFactory CreateFullSystem(string database) =>
        CreateFactory(database, services => services.AddHostedService<PaymentRequestConsumer>());

    public Task InitializeAsync() => Task.WhenAll(_sql.StartAsync(), _rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await _sql.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

public sealed class TicketingFactory(
    string sqlConnectionString,
    string rabbitUri,
    Action<IServiceCollection> configure) : WebApplicationFactory<ApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Sql", sqlConnectionString);
        builder.UseSetting("RabbitMq:Uri", rabbitUri);
        builder.UseSetting("Jwt:Key", "integration-test-signing-key-0123456789");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Seed:AdminEmail", ContainersFixture.AdminEmail);
        builder.UseSetting("Seed:AdminPassword", ContainersFixture.AdminPassword);
        builder.UseSetting("Payments:SimulatedDelayMs", "10");
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "1000000");
        builder.ConfigureTestServices(configure);
    }
}

public static class ApiClient
{
    public static async Task<HttpClient> RegisterCustomerAsync(this TicketingFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new CredentialsRequest($"{Guid.NewGuid():N}@tests.local", "Test_Password_1!"));
        response.EnsureSuccessStatusCode();
        await client.UseTokenFromAsync(response);
        return client;
    }

    public static async Task<HttpClient> LoginAdminAsync(this TicketingFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new CredentialsRequest(ContainersFixture.AdminEmail, ContainersFixture.AdminPassword));
        response.EnsureSuccessStatusCode();
        await client.UseTokenFromAsync(response);
        return client;
    }

    public static async Task<(Guid EventId, Guid SeatId)> CreateSingleSeatEventAsync(this HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/events", new CreateEventRequest(
            "Test concert",
            "Test hall",
            DateTimeOffset.UtcNow.AddDays(30),
            [new SeatRequest("A1", 15_000)]));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<EventResponseDto>();
        return (created!.Id, created.Seats.Single().Id);
    }

    private static async Task UseTokenFromAsync(this HttpClient client, HttpResponseMessage response)
    {
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);
    }
}

/// <summary>Records error-level log messages so a test can assert an alert was raised.</summary>
public sealed class CapturedLogs : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            _messages.Enqueue(formatter(state, exception));
        }
    }

    public void Dispose()
    {
    }
}

// The API writes enums as strings, so tests read them as strings too.
public sealed record SeatDto(Guid Id, string Label, long PriceCents, string Status);
public sealed record EventResponseDto(Guid Id, string Name, List<SeatDto> Seats);
public sealed record OrderDto(Guid Id, Guid SeatId, long AmountCents, string Status, string? FailureReason);
