using System.Security.Claims;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

/// <summary>
/// Every endpoint declares the permission it needs. Built-in roles map to permission sets here; an API token
/// carries a subset of its user's permissions, and a user can be limited to some hotels and apps (AccessScope).
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
    public const string TokenPermissionClaim = "tm:perm";

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

    /// <summary>The role allows it and, for an API token, the token was given it.</summary>
    public static bool Has(ClaimsPrincipal user, string permission)
    {
        if (!Enum.TryParse<PanelRole>(user.FindFirstValue(ClaimTypes.Role), out var role) || !RoleHas(role, permission))
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
