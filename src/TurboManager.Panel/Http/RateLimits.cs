using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TurboManager.Panel.Http;

public static class RateLimits
{
    /// <summary>Login, second factor and setup-token attempts: 10 per minute per client IP.</summary>
    public const string Auth = "auth";

    public static IServiceCollection AddPanelRateLimits(this IServiceCollection services) => services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });
}
