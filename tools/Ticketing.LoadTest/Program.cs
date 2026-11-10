using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// Fires a burst of simultaneous hold requests, from different customers, at one seat.
// Passes only if exactly one customer wins over HTTP and the database agrees.
//
// Usage: dotnet run -c Release --project tools/Ticketing.LoadTest -- [--requests 500] [--rounds 5]
var options = ParseArgs(args);
var baseUrl = options.GetValueOrDefault("url", "http://localhost:5080");
var requests = int.Parse(options.GetValueOrDefault("requests", "500"));
var rounds = int.Parse(options.GetValueOrDefault("rounds", "5"));
var adminEmail = options.GetValueOrDefault("admin-email", "admin@ticketing.local");
var adminPassword = options.GetValueOrDefault("admin-password", "Local_Dev_Admin_1!");
var sql = options.GetValueOrDefault(
    "sql", "Server=localhost,14333;Database=Ticketing;User Id=sa;Password=Local_Dev_Only_1!;TrustServerCertificate=True");

ThreadPool.SetMinThreads(requests, requests);
using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = requests,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
})
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(100),
};

var adminToken = await AuthAsync("login", adminEmail, adminPassword);

Console.WriteLine($"Registering {requests} customers...");
var runId = Guid.NewGuid().ToString("N")[..8];
var signupLimit = new SemaphoreSlim(Environment.ProcessorCount);
var tokens = await Task.WhenAll(Enumerable.Range(0, requests).Select(async i =>
{
    await signupLimit.WaitAsync();
    try
    {
        return await AuthAsync("register", $"load-{runId}-{i}@tests.local", "Load_Test_Password_1!");
    }
    finally
    {
        signupLimit.Release();
    }
}));

Console.WriteLine();
Console.WriteLine("| Round | Requests | 201 Held | 409 Refused | Other | Live orders in DB | p50 ms | p95 ms | p99 ms |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|");

var failed = false;
for (var round = 1; round <= rounds; round++)
{
    var (eventId, seatId) = await CreateSeatAsync(adminToken, round);
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = tokens.Select(token => HoldAsync(token, eventId, seatId, gate.Task)).ToArray();

    // Every request is built and waiting on the gate, so they all leave together.
    await Task.Delay(250);
    gate.SetResult();
    var results = await Task.WhenAll(calls);

    var held = results.Count(r => r.Status == HttpStatusCode.Created);
    var refused = results.Count(r => r.Status == HttpStatusCode.Conflict);
    var other = results.Length - held - refused;
    var liveOrders = await CountLiveOrdersAsync(seatId);
    var sorted = results.Select(r => r.Milliseconds).Order().ToArray();

    Console.WriteLine(
        $"| {round} | {requests} | {held} | {refused} | {other} | {liveOrders} " +
        $"| {Percentile(sorted, 50):F0} | {Percentile(sorted, 95):F0} | {Percentile(sorted, 99):F0} |");

    failed |= held != 1 || refused != requests - 1 || other != 0 || liveOrders != 1;

    foreach (var group in results
        .Where(r => r.Status is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
        .GroupBy(r => r.Error ?? $"HTTP {(int)r.Status}"))
    {
        Console.Error.WriteLine($"Round {round} other: {group.Count()} x {group.Key}");
    }
}

Console.WriteLine();
Console.WriteLine(failed ? "RESULT: FAILED" : $"RESULT: PASSED. {rounds} rounds, each with exactly 1 winner and 0 double bookings.");
return failed ? 1 : 0;

async Task<string> AuthAsync(string action, string email, string password)
{
    var response = await http.PostAsJsonAsync($"/api/auth/{action}", new { email, password });
    response.EnsureSuccessStatusCode();
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return body.RootElement.GetProperty("token").GetString()!;
}

async Task<(Guid EventId, Guid SeatId)> CreateSeatAsync(string token, int round)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
    {
        Content = JsonContent.Create(new
        {
            name = $"Load test {runId} round {round}",
            venue = "Load test hall",
            startsAt = DateTimeOffset.UtcNow.AddDays(30),
            seats = new[] { new { label = "A1", priceCents = 15000 } },
        }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return (body.RootElement.GetProperty("id").GetGuid(),
        body.RootElement.GetProperty("seats")[0].GetProperty("id").GetGuid());
}

async Task<(HttpStatusCode Status, double Milliseconds, string? Error)> HoldAsync(string token, Guid eventId, Guid seatId, Task gate)
{
    await gate;
    var timer = Stopwatch.StartNew();
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/events/{eventId}/seats/{seatId}/hold");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    try
    {
        using var response = await http.SendAsync(request);
        return (response.StatusCode, timer.Elapsed.TotalMilliseconds, null);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return (0, timer.Elapsed.TotalMilliseconds, ex.InnerException?.Message ?? ex.Message);
    }
}

// Checked straight in the database, independently of what the API reported.
async Task<int> CountLiveOrdersAsync(Guid seatId)
{
    await using var connection = new SqlConnection(sql);
    await connection.OpenAsync();
    await using var command = new SqlCommand(
        "SELECT COUNT(*) FROM Orders WHERE SeatId = @seat AND Status IN (0, 1, 2)", connection);
    command.Parameters.AddWithValue("@seat", seatId);
    return (int)(await command.ExecuteScalarAsync())!;
}

static double Percentile(double[] sorted, int percentile) =>
    sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];

static Dictionary<string, string> ParseArgs(string[] args)
{
    var parsed = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected an option name but found '{args[i]}'.");
        }

        parsed[args[i][2..]] = args[i + 1];
    }

    return parsed;
}
