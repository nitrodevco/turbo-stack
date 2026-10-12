using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

/// <summary>
/// What the current user may see and act on when they're limited to some hotels and apps (PanelUser.ScopeStacks /
/// ScopeApps). Covers apps and their deploys, hotels, app services and logs, and sites; server-wide pages follow
/// the role's permissions.
/// </summary>
public sealed class AccessScope(IHttpContextAccessor http, PanelDbContext db)
{
    public sealed record Limits(bool All, IReadOnlySet<int> Stacks, IReadOnlySet<int> Apps, IReadOnlyList<App> AllowedApps)
    {
        public bool App(App app) => All || Apps.Contains(app.Id) || (app.StackId is { } stack && Stacks.Contains(stack));

        public bool Stack(int stackId) => All || Stacks.Contains(stackId);

        /// <summary>A systemd unit belonging to an allowed app (tm-app-&lt;name&gt;.service).</summary>
        public bool Unit(string unit) => All || AllowedApps.Any(a => unit == $"tm-app-{a.Name}.service");

        /// <summary>Jobs of allowed apps (deploys) and hotels (installs, updates).</summary>
        public bool Job(string kind) =>
            All || AllowedApps.Any(a => kind == $"deploy-app-{a.Id}") || Stacks.Any(s => kind == $"stack-{s}");

        public bool Site(Site site) =>
            All || (site.StackId is { } stack && Stacks.Contains(stack))
            || AllowedApps.Any(a => site.Routes.Any(r => r.Target.StartsWith(a.AppDirectory + "/", StringComparison.Ordinal)));
    }

    private Limits? _limits;

    public async Task<Limits> GetAsync()
    {
        if (_limits is not null)
        {
            return _limits;
        }

        var user = http.HttpContext is { } context ? await CurrentUser.FindAsync(context, db) : null;

        if (user is null || user.Role is PanelRole.Owner or PanelRole.Admin || (user.ScopeStacks.Count == 0 && user.ScopeApps.Count == 0))
        {
            return _limits = new Limits(true, new HashSet<int>(), new HashSet<int>(), []);
        }

        var stacks = user.ScopeStacks.ToHashSet();
        var apps = user.ScopeApps.ToHashSet();
        var allowed = await db.Apps.Where(a => apps.Contains(a.Id) || (a.StackId != null && stacks.Contains(a.StackId.Value))).ToListAsync();

        return _limits = new Limits(false, stacks, apps, allowed);
    }

    public async Task<bool> CanAccessAppAsync(int appId)
    {
        var limits = await GetAsync();

        return limits.All || limits.AllowedApps.Any(a => a.Id == appId);
    }

    /// <summary>
    /// The app users (Linux accounts) this person may act as for a permission such as terminal.app or files.manage:
    /// those of the apps they can access, or every app user when they're not limited.
    /// </summary>
    public async Task<List<string>> SystemUsersAsync(string permission)
    {
        if (http.HttpContext is not { } context || !Permissions.Has(context.User, permission))
        {
            return [];
        }

        var limits = await GetAsync();

        if (!limits.All)
        {
            return limits.AllowedApps.Select(a => a.SystemUser).Distinct().Order().ToList();
        }

        return (await db.Apps.Select(a => a.SystemUser).ToListAsync())
            .Concat(await db.Repositories.Select(r => r.SystemUser).ToListAsync())
            .Concat(await db.Stacks.Select(s => s.SystemUser).ToListAsync())
            .Distinct().Order().ToList();
    }

    public static IResult Denied() => Results.Problem("You don't have access to this.", statusCode: StatusCodes.Status403Forbidden);
}

public static class AccessScopeExtensions
{

    /// <summary>
    /// For routes with an {id} under /api/apps, /api/repositories, /api/stacks or /api/sites: a limited user only gets
    /// through for their own. Routes without an id (lists) filter their results themselves.
    /// </summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;

            if (!int.TryParse(http.GetRouteValue("id")?.ToString(), out var id))
            {
                return await next(context);
            }

            var limits = await http.RequestServices.GetRequiredService<AccessScope>().GetAsync();

            if (limits.All)
            {
                return await next(context);
            }

            var path = http.Request.Path;
            var db = http.RequestServices.GetRequiredService<PanelDbContext>();
            var allowed =
                path.StartsWithSegments("/api/apps") ? limits.AllowedApps.Any(a => a.Id == id)
                : path.StartsWithSegments("/api/repositories") ? limits.AllowedApps.Any(a => a.RepositoryId == id)
                : path.StartsWithSegments("/api/stacks") ? limits.Stack(id)
                : path.StartsWithSegments("/api/sites") && await db.Sites.Include(s => s.Routes).FirstOrDefaultAsync(s => s.Id == id) is { } site
                    && limits.Site(site);

            return allowed ? await next(context) : AccessScope.Denied();
        });
}
