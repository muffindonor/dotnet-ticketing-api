using System.ComponentModel.DataAnnotations;

namespace Ticketing.Api.Options;

public sealed class JwtOptions
{
    [Required, MinLength(32)] public string Key { get; set; } = "";
    [Required] public string Issuer { get; set; } = "ticketing-api";
    [Required] public string Audience { get; set; } = "ticketing-clients";
    [Range(1, 1440)] public int LifetimeMinutes { get; set; } = 60;
}

public sealed class BookingOptions
{
    [Range(1, 120)] public int HoldMinutes { get; set; } = 10;
    [Range(1, 3600)] public int ExpirySweepSeconds { get; set; } = 5;
    [Range(1, 1440)] public int PaymentTimeoutMinutes { get; set; } = 15;
}

public sealed class AuthRateLimitOptions
{
    [Range(1, int.MaxValue)] public int AuthRequestsPerMinute { get; set; } = 20;
}

public sealed class RabbitMqOptions
{
    [Required] public string Uri { get; set; } = "";
}

public sealed class SeedOptions
{
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
}
