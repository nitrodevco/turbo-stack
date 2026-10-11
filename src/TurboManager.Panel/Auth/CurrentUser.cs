using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

public static class CurrentUser
{
    /// <summary>The signed-in user for endpoints that already require a session (used for audit entries).</summary>
    public static async Task<PanelUser> GetAsync(HttpContext http, PanelDbContext db) =>
        await SessionManager.GetUserAsync(http, AuthSchemes.Session, db)
        ?? throw new InvalidOperationException("No signed-in user.");
}
