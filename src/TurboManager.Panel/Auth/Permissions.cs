using System.Security.Claims;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

/// <summary>
/// Every endpoint declares the permission it needs. Built-in roles map to permission sets here;
/// custom roles and per-resource scopes come with user management (M7).
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

    public static readonly string[] All =
    [
        SystemView, ServicesView, ServicesControl, LogsView, FirewallView, FirewallManage,
        SshView, SshManage, PackagesView, PackagesManage, ServerReboot, AuditView,
    ];

    private static readonly Dictionary<PanelRole, HashSet<string>> RolePermissions = new()
    {
        [PanelRole.Owner] = [.. All],
        [PanelRole.Admin] = [.. All],
        [PanelRole.Operator] = [SystemView, ServicesView, ServicesControl, LogsView, FirewallView, PackagesView],
        [PanelRole.Developer] = [SystemView, ServicesView, LogsView],
        [PanelRole.Viewer] = [SystemView, ServicesView],
    };

    public static bool RoleHas(PanelRole role, string permission) =>
        RolePermissions.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlyCollection<string> For(PanelRole role) =>
        RolePermissions.TryGetValue(role, out var set) ? set : [];

    public static bool Has(ClaimsPrincipal user, string permission) =>
        Enum.TryParse<PanelRole>(user.FindFirstValue(ClaimTypes.Role), out var role) && RoleHas(role, permission);

    /// <summary>Registers one authorization policy per permission (policy name = permission).</summary>
    public static void AddPermissionPolicies(this Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        foreach (var permission in All)
        {
            options.AddPolicy(permission, policy => policy
                .AddAuthenticationSchemes(AuthSchemes.Session)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => Has(context.User, permission)));
        }
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(permission);
}
