using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

/// <summary>
/// Dangerous actions (managing users and tokens, a root shell) need the second factor again: re-authenticating
/// opens a short window on this session ("sudo mode"). API tokens can never do them.
/// </summary>
public static class SudoMode
{
    public static TBuilder RequireSudo<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var db = http.RequestServices.GetRequiredService<PanelDbContext>();
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            var session = await SessionManager.GetSessionAsync(http, db);

            return session?.SudoUntil > now ? await next(context) : Required();
        }).WithMetadata(SudoRequired.Instance);

    public static IResult Required() => Results.Problem(
        "Confirm it's you: enter a code from your authenticator app.",
        statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?> { ["sudo"] = true });
}

/// <summary>Marks an endpoint that needs sudo mode (shown in the API documentation).</summary>
public sealed class SudoRequired
{
    public static readonly SudoRequired Instance = new();
}
