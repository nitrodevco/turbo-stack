using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TurboManager.Panel.Http;

public static class RateLimits
{
    /// <summary>Login, second factor and setup-token attempts: 10 per minute per client IP.</summary>
    public const string Auth = "auth";

    /// <summary>GitHub webhooks: 60 per minute per source IP.</summary>
    public const string Webhooks = "webhooks";

    public static IServiceCollection AddPanelRateLimits(this IServiceCollection services) => services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        options.AddPolicy(Webhooks, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });
}
