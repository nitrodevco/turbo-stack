using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

public static class CurrentUser
{
    /// <summary>
    /// The user behind the request (session or API token) for endpoints that already require one; used for audit
    /// entries and checks.
    /// </summary>
    public static async Task<PanelUser> GetAsync(HttpContext http, PanelDbContext db) =>
        await FindAsync(http, db) ?? throw new InvalidOperationException("No signed-in user.");

    public static async Task<PanelUser?> FindAsync(HttpContext http, PanelDbContext db)
    {
        if (!int.TryParse(http.User.FindFirstValue(AuthSchemes.UserIdClaim), out var id))
        {
            return null;
        }

        var user = await db.Users.Include(u => u.RecoveryCodes).FirstOrDefaultAsync(u => u.Id == id);

        return user is not null && user.SecurityStamp == http.User.FindFirstValue(AuthSchemes.StampClaim) ? user : null;
    }
}
