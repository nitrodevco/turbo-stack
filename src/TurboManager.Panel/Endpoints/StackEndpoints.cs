using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Http;
using TurboManager.Panel.Jobs;
using TurboManager.Panel.Recipes;
using TurboManager.Panel.Stacks;

namespace TurboManager.Panel.Endpoints;

public sealed record RoleAddRequest(string? Host);

public sealed record RoleRemoveRequest(Dictionary<string, string>? Replacements);

public static class StackEndpoints
{
    public static void MapStackEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.AppsView).RequireScope();
        // A hotel creates apps, sites and a database.
        var manage = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.AppsManage).RequirePermission(Permissions.SitesManage)
            .RequirePermission(Permissions.DatabasesManage);

        view.MapGet("/presets", (RecipeCatalog catalog) => Results.Ok(catalog.Presets.Select(p => new
        {
            p.Id,
            p.Name,
            p.Summary,
            p.Description,
            settings = p.Settings,
            roles = p.Roles.Select(r =>
            {
                var recipe = catalog.Find(r.Recipe)!;

                return new
                {
                    r.Role,
                    r.Recipe,
                    r.Host,
                    r.Label,
                    r.Optional,
                    source = recipe.Source,
                    requiresTls = recipe.Site?.RequiresTls ?? false,
                    // What other apps need from it: asked for when the role is left out.
                    exports = recipe.Exports.Keys.Where(e => p.Roles.Any(o => catalog.Find(o.Recipe)!.Inputs.Any(i => i.From == $"{r.Role}.{e}"))),
                };
            }),
            databaseEngines = p.Roles.Select(r => catalog.Find(r.Recipe)!.Database).FirstOrDefault(d => d is not null)?.Engines,
        })));

        view.MapGet("/stacks", async (PanelDbContext db, AccessScope scope) =>
        {
            var limits = await scope.GetAsync();

            return Results.Ok((await db.Stacks.OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Domain, s.Preset }).ToListAsync())
                .Where(s => limits.Stack(s.Id)));
        });

        view.MapGet("/stacks/{id:int}", async (int id, PanelDbContext db, StackService stacks, RecipeCatalog recipes, AgentChannelFactory agent,
            CancellationToken cancellationToken) =>
            await db.Stacks.FindAsync([id], cancellationToken) is { } stack
                ? Results.Ok(await DescribeAsync(stack, db, stacks, recipes, agent, cancellationToken))
                : Results.NotFound());

        manage.MapPost("/stacks", async (StackRequest request, HttpContext http, PanelDbContext db, StackService stacks, CancellationToken cancellationToken) =>
        {
            try
            {
                var stack = await stacks.CreateAsync(request, await CurrentUser.GetAsync(http, db), cancellationToken);

                return Results.Ok(new { stack.Id });
            }
            catch (StackException e)
            {
                return Invalid(e.Message);
            }
        });

        // Changes the answers and returns what re-rendering would change; nothing is applied until /apply.
        manage.MapPut("/stacks/{id:int}", async (int id, StackRequest request, HttpContext http, PanelDbContext db, StackService stacks,
            CancellationToken cancellationToken) =>
        {
            if (await db.Stacks.FindAsync([id], cancellationToken) is not { } stack)
            {
                return Results.NotFound();
            }

            try
            {
                await stacks.UpdateAsync(stack, request, await CurrentUser.GetAsync(http, db), cancellationToken);

                return Results.Ok(await stacks.PlanChangesAsync(stack, cancellationToken));
            }
            catch (StackException e)
            {
                return Invalid(e.Message);
            }
        });

        manage.MapGet("/stacks/{id:int}/changes", async (int id, PanelDbContext db, StackService stacks, CancellationToken cancellationToken) =>
            await db.Stacks.FindAsync([id], cancellationToken) is { } stack
                ? Results.Ok(await stacks.PlanChangesAsync(stack, cancellationToken))
                : Results.NotFound());

        // The answers as a file, for rebuilding this hotel elsewhere (install.sh --config hotel.yaml).
        manage.MapGet("/stacks/{id:int}/answers", async (int id, PanelDbContext db, CancellationToken cancellationToken) =>
            await db.Stacks.FindAsync([id], cancellationToken) is { } stack
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(StackAnswers.From(stack).ToYaml()), "application/yaml", $"{stack.Name}.yaml")
                : Results.NotFound());

        manage.MapPost("/stacks/{id:int}/apply", (int id, HttpContext http, PanelDbContext db, StackService stacks) =>
            StartAsync(id, http, db, (stack, user) => stacks.StartApplyAsync(stack, user)));

        manage.MapPost("/stacks/{id:int}/install", (int id, HttpContext http, PanelDbContext db, StackService stacks) =>
            StartAsync(id, http, db, (stack, user) => stacks.StartInstallAsync(stack, user)));

        // Optional parts (client, imager, admin panel, game assets) can be added to or removed from an existing hotel.
        manage.MapPost("/stacks/{id:int}/roles/{role}", (int id, string role, RoleAddRequest request, HttpContext http, PanelDbContext db,
            StackService stacks, CancellationToken cancellationToken) =>
            StartAsync(id, http, db, (stack, user) => stacks.StartAddRoleAsync(stack, role, request.Host, user, cancellationToken)));

        manage.MapDelete("/stacks/{id:int}/roles/{role}", (int id, string role, [Microsoft.AspNetCore.Mvc.FromBody] RoleRemoveRequest request, HttpContext http,
            PanelDbContext db, StackService stacks, CancellationToken cancellationToken) =>
            StartAsync(id, http, db, (stack, user) => stacks.StartRemoveRoleAsync(stack, role, request.Replacements, user, cancellationToken)));

        // The owner's admin setup link: found in the emulator's log, then kept (encrypted) for the owner.
        var owner = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.AppsManage);

        owner.MapGet("/stacks/{id:int}/admin-setup-link", async (int id, PanelDbContext db, StackService stacks, IDataProtectionProvider protection,
            CancellationToken cancellationToken) =>
        {
            if (await db.Stacks.FindAsync([id], cancellationToken) is not { } stack)
            {
                return Results.NotFound();
            }

            var protector = protection.CreateProtector("TurboManager.Stacks.v1");

            if (await stacks.FindAdminSetupLinkAsync(stack, cancellationToken) is { } found)
            {
                stack.AdminSetupLinkProtected = protector.Protect(found);
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.Ok(new { url = stack.AdminSetupLinkProtected is { } link ? protector.Unprotect(link) : null });
        });
    }

    private static async Task<IResult> StartAsync(int id, HttpContext http, PanelDbContext db, Func<Stack, PanelUser?, Task<Job>> start)
    {
        if (await db.Stacks.FindAsync(id) is not { } stack)
        {
            return Results.NotFound();
        }

        try
        {
            var job = await start(stack, await CurrentUser.GetAsync(http, db));

            return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
        }
        catch (JobConflictException)
        {
            return Results.Problem($"{stack.Name} is already being installed or updated.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (StackException e)
        {
            return Invalid(e.Message);
        }
    }

    /// <summary>The hotel's map: every role with its domain, app, ports, health and the values wired into it.</summary>
    private static async Task<object> DescribeAsync(Stack stack, PanelDbContext db, StackService stacks, RecipeCatalog recipes, AgentChannelFactory agent,
        CancellationToken cancellationToken)
    {
        var preset = stacks.Preset(stack);
        var apps = await db.Apps.Include(a => a.Repository).Where(a => a.StackId == stack.Id).ToListAsync(cancellationToken);
        var siteList = await db.Sites.Where(s => s.StackId == stack.Id).ToListAsync(cancellationToken);
        var appIds = apps.Select(a => a.Id).ToList();
        var releases = await db.Releases.Where(r => appIds.Contains(r.AppId)).GroupBy(r => r.AppId)
            .Select(g => g.OrderByDescending(r => r.Id).First()).ToListAsync(cancellationToken);
        var exports = await stacks.ExportsAsync(stack, cancellationToken);
        Dictionary<string, string> units = [];

        try
        {
            var services = await agent.Services.ListServicesAsync(new Contracts.Agent.ListServicesRequest(), cancellationToken: cancellationToken);
            units = services.Services.Where(u => u.Name.StartsWith("tm-app-", StringComparison.Ordinal)).ToDictionary(u => u.Name, u => u.ActiveState);
        }
        catch (Grpc.Core.RpcException)
        {
            // The map still works without live service states.
        }


        return new
        {
            stack.Id,
            stack.Name,
            stack.Preset,
            presetName = preset.Name,
            stack.Domain,
            stack.SystemUser,
            tlsMode = stack.TlsMode,
            stack.CloudflareDns,
            stack.Hosts,
            stack.Settings,
            stack.Overrides,
            stack.Sources,
            stack.DatabaseEngine,
            stack.Isolated,
            stack.InstallJobId,
            roles = preset.Roles.Select(role =>
            {
                var app = apps.FirstOrDefault(a => a.StackRole == role.Role);
                var site = siteList.FirstOrDefault(s => s.StackRole == role.Role);
                var release = app is null ? null : releases.FirstOrDefault(r => r.AppId == app.Id);
                var recipe = recipes.Find(role.Recipe)!;

                return new
                {
                    role.Role,
                    role.Label,
                    role.Recipe,
                    role.Optional,
                    included = StackService.Included(stack, preset).Contains(role),
                    domain = stack.Hosts.GetValueOrDefault(role.Role),
                    app = app is null ? null : new
                    {
                        app.Id,
                        app.Name,
                        repository = app.Repository!.FullName,
                        app.CurrentRelease,
                        app.Ports,
                        lastRelease = release is null ? null : new { release.Name, status = release.Status.ToString().ToLowerInvariant(), release.Error },
                        service = app.ServiceCommand is null ? null : units.GetValueOrDefault($"tm-app-{app.Name}.service", "unknown"),
                    },
                    site = site is null ? null : new { site.Id, site.Name, applied = site.LastAppliedRevision is not null && site.LastError is null, site.LastError },
                    // Where each wired input of this role comes from, and its value.
                    wiring = recipe.Inputs.Where(i => i.From.Length > 0).Select(i => new
                    {
                        input = i.Key,
                        i.Label,
                        from = i.From,
                        value = stack.Overrides.GetValueOrDefault($"{role.Role}.{i.Key}") ?? exports.GetValueOrDefault(i.From, ""),
                        pinned = stack.Overrides.ContainsKey($"{role.Role}.{i.Key}"),
                    }),
                };
            }),
        };
    }

    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]> { ["stack"] = [message] });
}
