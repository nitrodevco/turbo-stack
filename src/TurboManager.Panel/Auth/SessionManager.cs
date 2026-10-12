using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

public static class SessionManager
{
    /// <summary>How long re-authenticating ("sudo mode") allows dangerous actions.</summary>
    public static readonly TimeSpan SudoDuration = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    public static async Task SignInAsync(HttpContext http, string scheme, PanelUser user)
    {
        var claims = new List<Claim>
        {
            new(AuthSchemes.UserIdClaim, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(AuthSchemes.StampClaim, user.SecurityStamp),
        };

        if (scheme == AuthSchemes.Session)
        {
            // A full session is a row, so it can be listed and revoked.
            var db = http.RequestServices.GetRequiredService<PanelDbContext>();
            var session = new PanelSession
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
                UserId = user.Id,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                UserAgent = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent[..Math.Min(300, agent.Length)] : null,
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            claims.Add(new Claim(AuthSchemes.SessionIdClaim, session.Id));
        }

        await http.SignInAsync(scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)));
    }

    public static async Task<PanelUser?> GetUserAsync(HttpContext http, string scheme, PanelDbContext db)
    {
        var result = await http.AuthenticateAsync(scheme);

        if (!result.Succeeded || !int.TryParse(result.Principal.FindFirstValue(AuthSchemes.UserIdClaim), out var id))
        {
            return null;
        }

        var user = await db.Users.Include(u => u.RecoveryCodes).FirstOrDefaultAsync(u => u.Id == id);

        return user is not null && user.SecurityStamp == result.Principal.FindFirstValue(AuthSchemes.StampClaim) ? user : null;
    }

    /// <summary>The current session row (null for API tokens and other schemes).</summary>
    public static async Task<PanelSession?> GetSessionAsync(HttpContext http, PanelDbContext db) =>
        http.User.FindFirstValue(AuthSchemes.SessionIdClaim) is { } id ? await db.Sessions.FindAsync(id) : null;

    /// <summary>Rejects session cookies whose session was revoked, or whose user was deleted or changed credentials.</summary>
    public static async Task ValidatePrincipalAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<PanelDbContext>();
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var principal = context.Principal;

        if (principal is null || !int.TryParse(principal.FindFirstValue(AuthSchemes.UserIdClaim), out var id)
            || principal.FindFirstValue(AuthSchemes.SessionIdClaim) is not { } sessionId)
        {
            await RejectAsync(context);

            return;
        }

        var stamp = await db.Users.Where(u => u.Id == id).Select(u => u.SecurityStamp).FirstOrDefaultAsync();
        var session = await db.Sessions.FindAsync(sessionId);

        if (stamp is null || stamp != principal.FindFirstValue(AuthSchemes.StampClaim) || session is null || session.UserId != id)
        {
            await RejectAsync(context);

            return;
        }

        var now = time.GetUtcNow();

        if (now - session.LastSeenAt > LastSeenResolution)
        {
            session.LastSeenAt = now;
            session.IpAddress = context.HttpContext.Connection.RemoteIpAddress?.ToString();
            await db.SaveChangesAsync();
        }
    }

    private static async Task RejectAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }
}
