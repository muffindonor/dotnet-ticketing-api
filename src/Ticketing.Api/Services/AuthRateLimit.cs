using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Ticketing.Api.Options;

namespace Ticketing.Api.Services;

public static class AuthRateLimit
{
    public const string Policy = "auth";

    /// <summary>Limits login and registration per client address to slow password guessing.</summary>
    public static IServiceCollection AddAuthRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<AuthRateLimitOptions>>((options, limits) =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.Value.AuthRequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                    }));
            });
        return services;
    }
}
