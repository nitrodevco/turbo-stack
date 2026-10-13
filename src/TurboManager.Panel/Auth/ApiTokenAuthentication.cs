using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

/// <summary>"Authorization: Bearer tm_…" for scripts and CI. The token's permissions narrow its user's role.</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PanelDbContext db,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Prefix = "tm_";

    public static string NewToken() => Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer " + Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = HashOf(header["Bearer ".Length..].Trim());
        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Hash == hash);
        var now = time.GetUtcNow();

        if (token is null || token.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail("Unknown or expired token.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId && u.TotpEnabled);

        if (user is null)
        {
            return AuthenticateResult.Fail("The token's user no longer exists.");
        }

        if (token.LastUsedAt is null || now - token.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync();
        }

        var claims = new List<Claim>
        {
            new(AuthSchemes.UserIdClaim, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(AuthSchemes.StampClaim, user.SecurityStamp),
            new(AuthSchemes.TokenIdClaim, token.Id.ToString()),
        };
        claims.AddRange(token.Permissions.Select(p => new Claim(Permissions.TokenPermissionClaim, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));

        if (user.Role == PanelRole.Custom)
        {
            principal = await SessionManager.WithRolePermissionsAsync(principal, user.CustomRoleId, db);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
