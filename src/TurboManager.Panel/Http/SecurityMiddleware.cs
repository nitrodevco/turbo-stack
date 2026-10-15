namespace TurboManager.Panel.Http;

public static class SecurityMiddleware
{
    /// <summary>Header every state-changing API call must carry. Browsers can't add custom headers to cross-site form posts.</summary>
    public const string CsrfHeader = "X-TS-Request";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

        await next();
    });

    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));

        // API tokens travel in the Authorization header, which a cross-site form can't send either.
        var bearer = request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal);

        if (unsafeMethod && !bearer && request.Path.StartsWithSegments("/api") && !request.Headers.ContainsKey(CsrfHeader))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = $"Missing {CsrfHeader} header." });

            return;
        }

        await next();
    });
}
