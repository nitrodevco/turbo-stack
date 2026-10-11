using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

public static class SessionManager
{
    public static Task SignInAsync(HttpContext http, string scheme, PanelUser user)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(AuthSchemes.UserIdClaim, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(AuthSchemes.StampClaim, user.SecurityStamp),
            ],
            scheme);

        return http.SignInAsync(scheme, new ClaimsPrincipal(identity));
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

    /// <summary>Rejects session cookies whose user was deleted or whose security stamp rotated.</summary>
    public static async Task ValidatePrincipalAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<PanelDbContext>();
        var principal = context.Principal;

        if (principal is null || !int.TryParse(principal.FindFirstValue(AuthSchemes.UserIdClaim), out var id))
        {
            context.RejectPrincipal();

            return;
        }

        var stamp = await db.Users.Where(u => u.Id == id).Select(u => u.SecurityStamp).FirstOrDefaultAsync();

        if (stamp is null || stamp != principal.FindFirstValue(AuthSchemes.StampClaim))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }
}
