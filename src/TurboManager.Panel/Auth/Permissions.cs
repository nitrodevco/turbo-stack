using System.Security.Claims;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

/// <summary>
/// Every endpoint declares the permission it needs. Built-in roles map to permission sets here; a custom role is a
/// set picked from the matrix (its permissions reach the principal on every request, so edits apply at once); an API
/// token carries a subset of its user's permissions, and a user can be limited to some hotels and apps (AccessScope).
/// </summary>
public static class Permissions
{
    public const string SystemView = "system.view";
    public const string ServicesView = "services.view";
    public const string ServicesControl = "services.control";
    public const string LogsView = "logs.view";
    public const string FirewallView = "firewall.view";
    public const string FirewallManage = "firewall.manage";
    public const string SshView = "ssh.view";
    public const string SshManage = "ssh.manage";
    public const string PackagesView = "packages.view";
    public const string PackagesManage = "packages.manage";
    public const string ServerReboot = "server.reboot";
    public const string AuditView = "audit.view";
    public const string SitesView = "sites.view";
    public const string SitesManage = "sites.manage";
    public const string DatabasesView = "databases.view";
    public const string DatabasesManage = "databases.manage";
    public const string AppsView = "apps.view";
    public const string AppsDeploy = "apps.deploy";
    public const string AppsManage = "apps.manage";
    public const string AppsConsole = "apps.console";
    public const string FilesManage = "files.manage";
    public const string TerminalApp = "terminal.app";
    public const string TerminalRoot = "terminal.root";
    public const string UsersManage = "users.manage";
    public const string AlertsManage = "alerts.manage";
    public const string PanelUpdate = "panel.update";
    public const string BackupsView = "backups.view";
    public const string BackupsManage = "backups.manage";

    public static readonly string[] All =
    [
        SystemView, ServicesView, ServicesControl, LogsView, FirewallView, FirewallManage, SshView, SshManage, PackagesView, PackagesManage,
        ServerReboot, AuditView, SitesView, SitesManage, DatabasesView, DatabasesManage, AppsView, AppsDeploy, AppsManage, AppsConsole,
        FilesManage, TerminalApp, TerminalRoot, UsersManage, AlertsManage, PanelUpdate, BackupsView, BackupsManage,
    ];

    /// <summary>Claim type of a token's permissions (a principal without any is a full session).</summary>
    public const string TokenPermissionClaim = "ts:perm";

    /// <summary>A custom role's current permissions (added to the principal on every request).</summary>
    public const string RolePermissionClaim = "ts:role-perm";

    /// <summary>Owner and admin only: a custom role can't hold these (no root shell, no editing who may do what).</summary>
    public static readonly IReadOnlySet<string> NotForCustomRoles = new HashSet<string> { TerminalRoot, UsersManage };

    /// <summary>The matrix: area, then what each permission allows (shown when building a role).</summary>
    public static readonly IReadOnlyList<(string Permission, string Area, string Description)> Catalog =
    [
        (SystemView, "Server", "See the dashboard, metrics, jobs and alerts"),
        (ServicesView, "Server", "See services and their state"),
        (ServicesControl, "Server", "Start, stop and restart services"),
        (LogsView, "Server", "Read logs"),
        (PackagesView, "Server", "See pending updates"),
        (PackagesManage, "Server", "Install updates"),
        (ServerReboot, "Server", "Reboot the server"),
        (PanelUpdate, "Server", "Update the panel"),
        (FirewallView, "Security", "See firewall rules"),
        (FirewallManage, "Security", "Change firewall rules"),
        (SshView, "Security", "See SSH keys and settings"),
        (SshManage, "Security", "Change SSH keys, settings and SFTP accounts"),
        (AuditView, "Security", "Read the activity log"),
        (AlertsManage, "Security", "Configure alert channels and thresholds"),
        (SitesView, "Sites", "See sites, certificates and Cloudflare"),
        (SitesManage, "Sites", "Create and change sites, certificates and Cloudflare settings"),
        (AppsView, "Apps", "See hotels, apps, repositories and deploys"),
        (AppsDeploy, "Apps", "Deploy, roll back and restart apps"),
        (AppsManage, "Apps", "Create and change hotels, apps and repositories"),
        (AppsConsole, "Apps", "Type into app consoles (e.g. the emulator)"),
        (FilesManage, "Apps", "Browse and edit app files"),
        (TerminalApp, "Apps", "Open a terminal as an app user"),
        (DatabasesView, "Data", "See databases and their backups"),
        (DatabasesManage, "Data", "Create, restore and drop databases and users"),
        (BackupsView, "Data", "See backup targets, plans and snapshots"),
        (BackupsManage, "Data", "Configure backups and restore snapshots"),
        (TerminalRoot, "Owner and admins", "Open a root terminal (owner only)"),
        (UsersManage, "Owner and admins", "Invite members and change roles"),
    ];

    private static readonly Dictionary<PanelRole, HashSet<string>> RolePermissions = new()
    {
        [PanelRole.Owner] = [.. All],
        // A root shell is the owner's alone.
        [PanelRole.Admin] = [.. All.Where(p => p != TerminalRoot)],
        [PanelRole.Operator] = [SystemView, ServicesView, ServicesControl, LogsView, FirewallView, PackagesView, SitesView, DatabasesView, BackupsView, AppsView, AppsDeploy,
            AppsConsole],
        [PanelRole.Developer] = [SystemView, ServicesView, LogsView, SitesView, DatabasesView, AppsView, AppsDeploy, AppsConsole, FilesManage, TerminalApp],
        [PanelRole.Viewer] = [SystemView, ServicesView],
    };

    public static bool RoleHas(PanelRole role, string permission) =>
        RolePermissions.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlyCollection<string> For(PanelRole role) =>
        RolePermissions.TryGetValue(role, out var set) ? set : [];

    /// <summary>What a user's role allows: a built-in set, or their custom role's (minus what custom roles can't hold).</summary>
    public static IReadOnlyCollection<string> For(PanelUser user, CustomRole? custom) =>
        user.Role == PanelRole.Custom ? [.. (custom?.Permissions ?? []).Where(p => All.Contains(p) && !NotForCustomRoles.Contains(p))] : For(user.Role);

    /// <summary>The role allows it and, for an API token, the token was given it.</summary>
    public static bool Has(ClaimsPrincipal user, string permission)
    {
        if (!Enum.TryParse<PanelRole>(user.FindFirstValue(ClaimTypes.Role), out var role))
        {
            return false;
        }

        var allowed = role == PanelRole.Custom
            ? !NotForCustomRoles.Contains(permission) && user.HasClaim(RolePermissionClaim, permission)
            : RoleHas(role, permission);

        if (!allowed)
        {
            return false;
        }

        var tokenPermissions = user.FindAll(TokenPermissionClaim).Select(c => c.Value).ToList();

        return user.FindFirstValue(AuthSchemes.TokenIdClaim) is null || tokenPermissions.Contains(permission);
    }

    /// <summary>Registers one authorization policy per permission (policy name = permission), for sessions and API tokens.</summary>
    public static void AddPermissionPolicies(this Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        foreach (var permission in All)
        {
            options.AddPolicy(permission, policy => policy
                .AddAuthenticationSchemes(AuthSchemes.Session, AuthSchemes.Token)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => Has(context.User, permission)));
        }
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(permission);
}
