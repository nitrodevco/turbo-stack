using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Data;
using TurboManager.Panel.Databases;
using TurboManager.Panel.Deploy;
using TurboManager.Panel.Jobs;
using TurboManager.Panel.Recipes;
using TurboManager.Panel.Sites;

namespace TurboManager.Panel.Stacks;

public sealed record StackRequest(
    string? Name,
    string? Preset,
    string? SystemUser,
    string? Domain,
    TlsMode TlsMode = TlsMode.LetsEncrypt,
    bool CloudflareDns = false,
    Dictionary<string, string>? Hosts = null,
    Dictionary<string, string>? Settings = null,
    Dictionary<string, string>? Overrides = null,
    List<StackSource>? Sources = null,
    string? DatabaseEngine = null,
    bool Isolated = false);

public sealed class StackException(string message) : Exception(message);

/// <summary>One environment variable a re-render would change.</summary>
public sealed record EnvChange(string Key, string? From, string? To, bool Pinned);

/// <summary>What re-rendering one member of a hotel would change, and what that takes.</summary>
public sealed record MemberChange(
    string Role,
    string Name,
    List<EnvChange> Environment,
    string Site, // none | update | create | hand-edited
    List<string> DomainsFrom,
    List<string> DomainsTo,
    string Action); // none | reload | restart | redeploy

/// <summary>Hotels: creating one from a preset, wiring its apps together, and keeping them in step when answers change.</summary>
public sealed partial class StackService(
    PanelDbContext db,
    RecipeCatalog catalog,
    RecipeService recipes,
    DeployService deployments,
    SiteService sites,
    AgentChannelFactory agent,
    JobRunner jobs,
    AuditLog audit)
{
    public static string JobKind(int stackId) => $"stack-{stackId}";

    public static string MemberName(Stack stack, string role) => $"{stack.Name}-{role}";

    /// <summary>The Linux user a member runs as: one per hotel, or one per part when the hotel isolates them.</summary>
    public static string MemberUser(Stack stack, string role) => stack.Isolated ? $"{stack.Name}-{role}" : stack.SystemUser;

    public StackPreset Preset(Stack stack) => catalog.FindPreset(stack.Preset) ?? throw new StackException($"Unknown preset {stack.Preset}.");

    /// <summary>The preset's roles this hotel includes (an optional role with an empty host is left out).</summary>
    public static IEnumerable<PresetRole> Included(Stack stack, StackPreset preset) =>
        preset.Roles.Where(r => stack.Hosts.TryGetValue(r.Role, out var host) && host.Length > 0);

    public async Task<Stack> CreateAsync(StackRequest request, PanelUser? actor, CancellationToken cancellationToken)
    {
        var preset = catalog.FindPreset(request.Preset ?? "") ?? throw new StackException("Choose what to build.");
        var name = (request.Name ?? "").Trim();
        var domain = (request.Domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();

        if (!NamePattern().IsMatch(name))
        {
            throw new StackException("Name: lowercase letters, digits and dashes (up to 20), e.g. \"hotel\".");
        }

        if (!DomainPattern().IsMatch(domain))
        {
            throw new StackException("Enter the hotel's domain, like myhotel.com.");
        }

        if (await db.Stacks.AnyAsync(s => s.Name == name, cancellationToken))
        {
            throw new StackException($"There is already a hotel called {name}.");
        }

        var stack = new Stack
        {
            Name = name,
            Preset = preset.Id,
            SystemUser = (request.SystemUser ?? name).Trim(),
            Domain = domain,
            TlsMode = request.TlsMode,
            CloudflareDns = request.CloudflareDns,
            Hosts = DefaultHosts(preset, domain, request.Hosts),
            Settings = Clean(request.Settings),
            Overrides = Clean(request.Overrides),
            Sources = request.Sources ?? [],
            DatabaseEngine = string.IsNullOrWhiteSpace(request.DatabaseEngine) ? null : request.DatabaseEngine.Trim(),
            Isolated = request.Isolated,
        };

        await ValidateAsync(stack, preset, cancellationToken);
        db.Stacks.Add(stack);
        audit.Record("stack.created", actor, target: stack.Name, details: $"{preset.Name} on {stack.Domain}");
        await db.SaveChangesAsync(cancellationToken);

        return stack;
    }

    /// <summary>Changes answers (domains, settings, pinned values); members are re-rendered separately (preview, then apply).</summary>
    public async Task UpdateAsync(Stack stack, StackRequest request, PanelUser? actor, CancellationToken cancellationToken)
    {
        var preset = Preset(stack);

        if (request.Hosts is not null)
        {
            var hosts = DefaultHosts(preset, stack.Domain, request.Hosts);

            // Members exist for the roles already included; adding or dropping roles later isn't supported (yet).
            foreach (var role in preset.Roles.Where(r => string.IsNullOrEmpty(hosts[r.Role]) != string.IsNullOrEmpty(stack.Hosts.GetValueOrDefault(r.Role))))
            {
                throw new StackException($"{role.Label} can't be added to or removed from an existing hotel.");
            }

            stack.Hosts = hosts;
        }

        if (request.Settings is not null)
        {
            stack.Settings = Clean(request.Settings);
        }

        if (request.Overrides is not null)
        {
            stack.Overrides = Clean(request.Overrides);
        }

        stack.TlsMode = request.TlsMode;
        await ValidateAsync(stack, preset, cancellationToken);
        audit.Record("stack.updated", actor, target: stack.Name);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateAsync(Stack stack, StackPreset preset, CancellationToken cancellationToken)
    {
        if (!UserPattern().IsMatch(stack.SystemUser) || DeployModels.ReservedUsers.Contains(stack.SystemUser))
        {
            throw new StackException("Linux user: 2-32 lowercase letters, digits or dashes, and not a system account.");
        }

        foreach (var (role, host) in stack.Hosts.Where(h => h.Value.Length > 0))
        {
            if (!DomainPattern().IsMatch(host))
            {
                throw new StackException($"{preset.Roles.First(r => r.Role == role).Label}: \"{host}\" isn't a valid domain.");
            }
        }

        if (stack.Hosts.Values.Where(h => h.Length > 0).GroupBy(h => h).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new StackException($"{duplicate.Key} is used for two parts of the hotel; each needs its own domain.");
        }

        foreach (var setting in preset.Settings.Where(s => s.Required && string.IsNullOrWhiteSpace(stack.Settings.GetValueOrDefault(s.Key))))
        {
            throw new StackException($"{setting.Label} is required.");
        }

        foreach (var role in preset.Roles.Where(r => r.Optional && !Included(stack, preset).Contains(r)))
        {
            foreach (var export in catalog.Find(role.Recipe)!.Exports.Keys.Where(e => Wanted(preset, role.Role, e)))
            {
                if (string.IsNullOrWhiteSpace(stack.Settings.GetValueOrDefault($"{role.Role}.{export}")))
                {
                    throw new StackException($"Without {role.Label.ToLowerInvariant()} on this server, enter its address ({role.Role}.{export}).");
                }
            }
        }

        if (Included(stack, preset).Any(r => catalog.Find(r.Recipe)!.Site is { RequiresTls: true }) && stack.TlsMode == TlsMode.None)
        {
            throw new StackException("The admin panel needs HTTPS: choose a certificate.");
        }

        var ours = (await db.Sites.Where(s => s.StackId == stack.Id).Select(s => s.Id).ToListAsync(cancellationToken)).ToHashSet();
        var taken = (await db.Sites.Where(s => s.StackId != stack.Id || stack.Id == 0).ToListAsync(cancellationToken))
            .Where(s => !ours.Contains(s.Id)).SelectMany(s => s.Domains).ToHashSet();

        if (stack.Hosts.Values.FirstOrDefault(taken.Contains) is { } clash)
        {
            throw new StackException($"{clash} is already used by another site.");
        }
    }

    /// <summary>Whether some member of the preset uses this export (only those must be given for a left-out role).</summary>
    private bool Wanted(StackPreset preset, string role, string export) =>
        preset.Roles.Any(r => catalog.Find(r.Recipe)!.Inputs.Any(i => i.From == $"{role}.{export}"));

    private static Dictionary<string, string> DefaultHosts(StackPreset preset, string domain, Dictionary<string, string>? given)
    {
        var hosts = new Dictionary<string, string>();

        foreach (var role in preset.Roles)
        {
            hosts[role.Role] = given?.TryGetValue(role.Role, out var host) == true
                ? host.Trim().TrimEnd('.').ToLowerInvariant()
                : role.Host == "@" ? domain : $"{role.Host}.{domain}";

            if (hosts[role.Role].Length == 0 && !role.Optional)
            {
                throw new StackException($"{role.Label} needs a domain.");
            }
        }

        return hosts;
    }

    private static Dictionary<string, string> Clean(Dictionary<string, string>? values) =>
        (values ?? []).Where(v => v.Value is not null).ToDictionary(v => v.Key.Trim(), v => v.Value.Trim());

    // ----- Wiring -----

    /// <summary>Every member's exports (from its domain and ports), plus settings standing in for left-out roles.</summary>
    public async Task<Dictionary<string, string>> ExportsAsync(Stack stack, CancellationToken cancellationToken)
    {
        var preset = Preset(stack);
        var apps = await db.Apps.Where(a => a.StackId == stack.Id).ToListAsync(cancellationToken);
        var values = new Dictionary<string, string>();

        foreach (var (key, value) in stack.Settings)
        {
            values[key.Contains('.') ? key : $"settings.{key}"] = value;
        }

        foreach (var role in Included(stack, preset))
        {
            var ports = apps.FirstOrDefault(a => a.StackRole == role.Role)?.Ports ?? [];

            foreach (var (export, value) in RecipeService.Exports(catalog.Find(role.Recipe)!, stack.Hosts[role.Role], stack.TlsMode, ports))
            {
                values[$"{role.Role}.{export}"] = value;
            }
        }

        return values;
    }

    /// <summary>A member's inputs: its own (or the recipe's defaults), then wired values, then values pinned on the stack.</summary>
    public static Dictionary<string, string> Inputs(Stack stack, PresetRole role, RecipeManifest recipe, IReadOnlyDictionary<string, string> exports,
        IReadOnlyDictionary<string, string>? current)
    {
        var inputs = new Dictionary<string, string>();

        foreach (var input in recipe.Inputs)
        {
            var value = current?.GetValueOrDefault(input.Key) ?? input.Default;

            if (input.From.Length > 0)
            {
                value = exports.GetValueOrDefault(input.From, "");
            }

            if (stack.Overrides.TryGetValue($"{role.Role}.{input.Key}", out var pinned))
            {
                value = pinned;
            }

            inputs[input.Key] = value;
        }

        return inputs;
    }

    public StackSource Source(Stack stack, RecipeManifest recipe) =>
        stack.Sources.FirstOrDefault(s => s.Default == recipe.Source?.Repository)
        ?? new StackSource { Default = recipe.Source?.Repository ?? "", Repository = recipe.Source?.Repository ?? "", Branch = recipe.Source?.Branch ?? "main" };

    // ----- Creating the members -----

    /// <summary>Creates whatever members don't exist yet, in the preset's order (the emulator first: others use its ports).</summary>
    public async Task CreateMembersAsync(Stack stack, PanelUser? actor, Action<string> log, CancellationToken cancellationToken)
    {
        var preset = Preset(stack);

        foreach (var role in Included(stack, preset))
        {
            var exists = await db.Apps.AnyAsync(a => a.StackId == stack.Id && a.StackRole == role.Role, cancellationToken)
                || await db.Sites.AnyAsync(s => s.StackId == stack.Id && s.StackRole == role.Role, cancellationToken);

            if (exists)
            {
                continue;
            }

            var recipe = catalog.Find(role.Recipe)!;
            var source = Source(stack, recipe);
            var exports = await ExportsAsync(stack, cancellationToken);
            var request = new RecipeRequest(
                MemberName(stack, role.Role),
                MemberUser(stack, role.Role),
                Repository: source.Repository,
                Branch: source.Branch,
                Private: source.Private,
                PrivateSubmodules: source.PrivateSubmodules,
                Domains: [stack.Hosts[role.Role]],
                TlsMode: stack.TlsMode,
                CloudflareDns: stack.CloudflareDns,
                DatabaseEngine: stack.DatabaseEngine,
                Inputs: Inputs(stack, role, recipe, exports, null));

            try
            {
                var result = await recipes.CreateAsync(recipe, request, actor, cancellationToken, new StackLink(stack.Id, role.Role),
                    startCertificateJob: false);
                log($"✓ {role.Label}: {request.Name} on {stack.Hosts[role.Role]}{(result.Site?.Error is { Length: > 0 } error ? $" (site: {error})" : "")}");
            }
            catch (RecipeException e)
            {
                throw new StackException($"{role.Label}: {e.Message}");
            }
        }
    }

    // ----- Keeping members in step -----

    /// <summary>What re-rendering every member with the current answers would change.</summary>
    public async Task<List<MemberChange>> PlanChangesAsync(Stack stack, CancellationToken cancellationToken) =>
        (await RenderMembersAsync(stack, cancellationToken)).Select(m => m.Change).ToList();

    private sealed record MemberRender(
        PresetRole Role,
        App? App,
        Site? Site,
        Dictionary<string, string> Inputs,
        Dictionary<string, string>? Environment,
        Dictionary<string, string>? RenderedEnvironment,
        SiteModel? SiteModel,
        MemberChange Change);

    private async Task<List<MemberRender>> RenderMembersAsync(Stack stack, CancellationToken cancellationToken)
    {
        var preset = Preset(stack);
        var exports = await ExportsAsync(stack, cancellationToken);
        var members = new List<MemberRender>();

        foreach (var role in Included(stack, preset))
        {
            var recipe = catalog.Find(role.Recipe)!;
            var app = await db.Apps.FirstOrDefaultAsync(a => a.StackId == stack.Id && a.StackRole == role.Role, cancellationToken);
            var site = await db.Sites.Include(s => s.Routes).FirstOrDefaultAsync(s => s.StackId == stack.Id && s.StackRole == role.Role, cancellationToken);

            if (app is null && site is null)
            {
                continue; // not created yet: the install job creates it with the current answers
            }

            var inputs = Inputs(stack, role, recipe, exports, app?.RecipeInputs);
            var state = app is null ? null : recipes.GetState(app);
            var domains = new List<string> { stack.Hosts[role.Role] };
            var rendered = RecipeService.Render(recipe, new RecipeService.RenderState(
                app?.Name ?? site!.Name, app?.SystemUser ?? MemberUser(stack, role.Role), domains, stack.TlsMode, stack.CloudflareDns, inputs, app?.Ports ?? [],
                state?.Secrets ?? [], state?.Database, state?.DatabasePassword));

            var envChanges = new List<EnvChange>();
            Dictionary<string, string>? environment = null;

            if (app is not null && rendered.App is { } planned)
            {
                var current = deployments.GetEnvironment(app);
                var last = state?.Environment ?? [];
                environment = new Dictionary<string, string>(current);

                foreach (var key in last.Keys.Union(planned.Environment.Keys).Order(StringComparer.Ordinal))
                {
                    var before = last.GetValueOrDefault(key);
                    var after = planned.Environment.GetValueOrDefault(key);

                    if (before == after)
                    {
                        continue;
                    }

                    // Three-way: a value still equal to what we last rendered follows the recipe; one edited by hand stays.
                    var pinned = current.GetValueOrDefault(key) != before;
                    envChanges.Add(new EnvChange(key, Mask(key, current.GetValueOrDefault(key)), Mask(key, after), pinned));

                    if (!pinned)
                    {
                        if (after is null)
                        {
                            environment.Remove(key);
                        }
                        else
                        {
                            environment[key] = after;
                        }
                    }
                }
            }

            var siteStatus = "none";
            var domainsFrom = site?.Domains.ToList() ?? [];

            if (rendered.Site is { } model)
            {
                if (site is null)
                {
                    siteStatus = "create";
                }
                else if (site.RecipeRendered != RecipeService.Canonical(model))
                {
                    siteStatus = JsonSerializer.Serialize(SiteModel.From(site)) == site.RecipeRendered ? "update" : "hand-edited";
                }
            }

            var envApplied = envChanges.Any(c => !c.Pinned);
            var action = app is { CurrentRelease: not null } && envApplied
                ? app.ServiceCommand is null ? "redeploy" : "restart"
                : siteStatus is "update" or "create" ? "reload" : "none";

            members.Add(new MemberRender(role, app, site, inputs, environment, rendered.App?.Environment, rendered.Site,
                new MemberChange(role.Role, app?.Name ?? site!.Name, envChanges, siteStatus, domainsFrom, rendered.Site?.Domains ?? [], action)));
        }

        return members;
    }

    private static readonly string[] SecretWords = ["PASSWORD", "SECRET", "PRIVATEKEY", "CONNECTIONSTRING", "TOKEN"];

    private static string? Mask(string key, string? value) =>
        value is not null && SecretWords.Any(w => key.Replace("_", "", StringComparison.Ordinal).Contains(w, StringComparison.OrdinalIgnoreCase))
            ? "••••••"
            : value;

    /// <summary>Re-renders every member and applies it: environment, sites, then restarts and redeploys where needed.</summary>
    public async Task<bool> ApplyChangesAsync(Stack stack, PanelUser? actor, JobContext context)
    {
        var members = await RenderMembersAsync(stack, context.CancellationToken);
        var ok = true;

        foreach (var member in members.Where(m => m.Change.Environment.Count > 0 || m.Change.Site != "none"))
        {
            var change = member.Change;

            if (member.App is { } app)
            {
                app.RecipeInputs = member.Inputs;

                if (member.Environment is { } environment && recipes.GetState(app) is { } state)
                {
                    deployments.SetEnvironment(app, environment);
                    // The new render is what pinned (hand-edited) values are compared against next time.
                    recipes.SetState(app, state with { Environment = member.RenderedEnvironment! });
                }

                foreach (var pinned in change.Environment.Where(c => c.Pinned))
                {
                    context.Log($"  {app.Name}: {pinned.Key} was edited by hand; left as it is.");
                }
            }

            if (member.SiteModel is { } model && change.Site is "update" or "create")
            {
                var site = member.Site ?? new Site { Name = "" };
                model.ApplyTo(site);
                site.RecipeId = member.Role.Recipe;
                site.RecipeRendered = RecipeService.Canonical(model);
                site.StackId = stack.Id;
                site.StackRole = member.Role.Role;

                if (member.Site is null)
                {
                    db.Sites.Add(site);
                }

                await db.SaveChangesAsync(context.CancellationToken);
                var result = await sites.ApplyAsync(site, actor, requestCertificate: true, context.CancellationToken, startCertificateJob: false);
                context.Log(result.Applied ? $"✓ {site.Name}: site updated ({string.Join(", ", site.Domains)})" : $"✕ {site.Name}: {result.Error}");
                ok &= result.Applied;
            }
            else if (change.Site == "hand-edited")
            {
                context.Log($"  {change.Name}: the site was edited by hand; not changed (edit it, or reset it from the hotel page).");
            }

            await db.SaveChangesAsync(context.CancellationToken);

            if (member.App is { } changed && change.Action == "restart")
            {
                await deployments.WriteServiceAsync(changed, context.CancellationToken);
                var healthy = await deployments.RestartAndCheckAsync(changed, context);
                context.Log(healthy ? $"✓ {changed.Name}: restarted with the new settings" : $"✕ {changed.Name}: didn't come back after the restart");
                ok &= healthy;
            }
            else if (member.App is { } rebuilt && change.Action == "redeploy")
            {
                context.Log($"{rebuilt.Name}: rebuilding (its settings are used at build time)…");
                var outcome = await deployments.DeployAsync(rebuilt.Id, "hotel", actor?.Username, context);
                ok &= outcome.Success;
            }
        }

        await EnsureCertificatesAsync(stack, context);
        audit.Record("stack.applied", actor, target: stack.Name);
        await db.SaveChangesAsync(CancellationToken.None);

        return ok;
    }

    public async Task<Job> StartApplyAsync(Stack stack, PanelUser? actor)
    {
        var stackId = stack.Id;

        return await jobs.StartAsync(JobKind(stackId), $"Update {stack.Name}", actor, context =>
            context.Services.GetRequiredService<StackService>().ApplyChangesAsync(stackId, actor, context));
    }

    private async Task<bool> ApplyChangesAsync(int stackId, PanelUser? actor, JobContext context) =>
        await ApplyChangesAsync(await db.Stacks.FirstAsync(s => s.Id == stackId, context.CancellationToken), actor, context);

    // ----- Adding and removing parts -----

    private PresetRole OptionalRole(Stack stack, string roleName)
    {
        // Checked before anything changes: the hotel's answers and its members must move together.
        if (jobs.IsRunning(JobKind(stack.Id)))
        {
            throw new JobConflictException($"{stack.Name} is already being installed or updated.");
        }

        var role = Preset(stack).Roles.FirstOrDefault(r => r.Role == roleName) ?? throw new StackException($"This hotel has no part called {roleName}.");

        return role.Optional ? role : throw new StackException($"{role.Label} is always part of the hotel.");
    }

    /// <summary>
    /// Adds a part that was left out: it gets its domain (the default subdomain unless given), the addresses typed in
    /// for it are dropped (the wiring takes over), and the install job creates, wires, certifies and deploys it.
    /// </summary>
    public async Task<Job> StartAddRoleAsync(Stack stack, string roleName, string? host, PanelUser? actor, CancellationToken cancellationToken)
    {
        var role = OptionalRole(stack, roleName);

        if (Included(stack, Preset(stack)).Contains(role))
        {
            throw new StackException($"{role.Label} is already part of the hotel.");
        }

        var domain = string.IsNullOrWhiteSpace(host) ? role.Host == "@" ? stack.Domain : $"{role.Host}.{stack.Domain}" : host.Trim().TrimEnd('.').ToLowerInvariant();
        stack.Hosts = new Dictionary<string, string>(stack.Hosts) { [role.Role] = domain };
        stack.Settings = stack.Settings.Where(s => !s.Key.StartsWith($"{role.Role}.", StringComparison.Ordinal)).ToDictionary();
        await ValidateAsync(stack, Preset(stack), cancellationToken);
        audit.Record("stack.role_added", actor, target: stack.Name, details: $"{role.Label} on {domain}");
        await db.SaveChangesAsync(cancellationToken);

        return await StartInstallAsync(stack, actor, $"Add {role.Label.ToLowerInvariant()} to {stack.Name}");
    }

    /// <summary>
    /// Removes an optional part: the others are rewired to the addresses given for it (e.g. a shared imager), then its
    /// app (service and releases) and site go. Files outside the app's folder — uploaded game assets — stay.
    /// </summary>
    public async Task<Job> StartRemoveRoleAsync(Stack stack, string roleName, Dictionary<string, string>? replacements, PanelUser? actor,
        CancellationToken cancellationToken)
    {
        var role = OptionalRole(stack, roleName);
        var preset = Preset(stack);

        if (!Included(stack, preset).Contains(role))
        {
            throw new StackException($"{role.Label} isn't part of the hotel.");
        }

        var settings = new Dictionary<string, string>(stack.Settings);

        foreach (var (key, value) in replacements ?? [])
        {
            if (!key.StartsWith($"{role.Role}.", StringComparison.Ordinal) || !Wanted(preset, role.Role, key[(role.Role.Length + 1)..]))
            {
                throw new StackException($"{key} isn't an address of {role.Label.ToLowerInvariant()}.");
            }

            settings[key] = value.Trim();
        }

        stack.Hosts = new Dictionary<string, string>(stack.Hosts) { [role.Role] = "" };
        stack.Settings = settings;
        await ValidateAsync(stack, preset, cancellationToken);
        audit.Record("stack.role_removed", actor, target: stack.Name, details: role.Label);
        await db.SaveChangesAsync(cancellationToken);

        var stackId = stack.Id;

        return await jobs.StartAsync(JobKind(stackId), $"Remove {role.Label.ToLowerInvariant()} from {stack.Name}", actor, context =>
            context.Services.GetRequiredService<StackService>().RemoveRoleAsync(stackId, role, actor, context));
    }

    private async Task<bool> RemoveRoleAsync(int stackId, PresetRole role, PanelUser? actor, JobContext context)
    {
        var stack = await db.Stacks.FirstAsync(s => s.Id == stackId, context.CancellationToken);

        context.Log("① Rewiring the other parts");
        var ok = await ApplyChangesAsync(stack, actor, context);

        context.Log($"② Removing {role.Label.ToLowerInvariant()}");

        foreach (var app in await db.Apps.Where(a => a.StackId == stack.Id && a.StackRole == role.Role).ToListAsync(context.CancellationToken))
        {
            await agent.Deploy.RemoveAppAsync(new AppRef { User = app.SystemUser, App = app.Name }, cancellationToken: context.CancellationToken);
            db.Apps.Remove(app);
            await db.SaveChangesAsync(context.CancellationToken);
            context.Log($"✓ {app.Name}: service and releases removed");
        }

        foreach (var site in await db.Sites.Where(s => s.StackId == stack.Id && s.StackRole == role.Role).ToListAsync(context.CancellationToken))
        {
            await sites.RemoveAsync(site, context.CancellationToken);
            context.Log($"✓ {site.Name}: site removed ({string.Join(", ", site.Domains)}); its files stay");
        }

        context.Log(ok ? $"✓ {role.Label} is no longer part of {stack.Name}." : "✕ Some parts didn't take the new addresses; see above.");

        return ok;
    }

    // ----- Installing -----

    /// <summary>Runtimes, database engine, members, repository access, deploys, admin link. Safe to run again: it continues.</summary>
    public async Task<Job> StartInstallAsync(Stack stack, PanelUser? actor, string? title = null)
    {
        var stackId = stack.Id;
        var job = await jobs.StartAsync(JobKind(stackId), title ?? $"Install {stack.Name}", actor, async context =>
        {
            var service = context.Services.GetRequiredService<StackService>();

            return await service.InstallAsync(stackId, actor, context);
        });

        stack.InstallJobId = job.Id;
        await db.SaveChangesAsync();

        return job;
    }

    private async Task<bool> InstallAsync(int stackId, PanelUser? actor, JobContext context)
    {
        var stack = await db.Stacks.FirstAsync(s => s.Id == stackId, context.CancellationToken);
        var preset = Preset(stack);
        var roles = Included(stack, preset).ToList();
        var cancellationToken = context.CancellationToken;

        context.Log("① Runtimes");
        var runtimes = await agent.Deploy.ListRuntimesAsync(new Empty(), cancellationToken: cancellationToken);

        foreach (var runtime in roles.SelectMany(r => catalog.Find(r.Recipe)!.App?.Runtimes ?? []).Distinct())
        {
            if (runtimes.Items.Any(r => r.Name == runtime && r.Installed))
            {
                context.Log($"  {runtime} is installed");
                continue;
            }

            context.Log($"  Installing {runtime}…");

            if (!await StreamAsync(agent.Deploy.InstallRuntime(new RuntimeRequest { Name = runtime, Channel = runtime == "dotnet" ? "10.0" : "24" },
                    cancellationToken: cancellationToken), context))
            {
                context.Log($"✕ Installing {runtime} failed.");

                return false;
            }
        }

        var needsDatabase = roles.Select(r => catalog.Find(r.Recipe)!.Database).FirstOrDefault(d => d is not null);

        if (needsDatabase is not null)
        {
            context.Log("② Database");
            var engines = (await agent.Databases.GetEnginesAsync(new Empty(), cancellationToken: cancellationToken)).Engines;
            var engine = stack.DatabaseEngine
                ?? needsDatabase.Engines.FirstOrDefault(e => engines.Any(s => DatabaseService.Slug(s.Engine) == e && s.Installed))
                ?? needsDatabase.Engines.FirstOrDefault(e => engines.Any(s => DatabaseService.Slug(s.Engine) == e && s.Recommended))
                ?? needsDatabase.Engines[0];
            var status = engines.First(s => DatabaseService.Slug(s.Engine) == engine);

            if (!status.Installed)
            {
                if (status.BlockedReason.Length > 0)
                {
                    context.Log($"✕ {engine}: {status.BlockedReason}");

                    return false;
                }

                context.Log($"  Installing {engine}…");

                if (!await StreamAsync(agent.Databases.InstallEngine(new EngineRef { Engine = status.Engine }, cancellationToken: cancellationToken), context))
                {
                    context.Log($"✕ Installing {engine} failed.");

                    return false;
                }
            }
            else
            {
                context.Log($"  {engine} is installed");
            }

            stack.DatabaseEngine = engine;
            await db.SaveChangesAsync(cancellationToken);
        }

        context.Log("③ Apps and sites");

        try
        {
            await CreateMembersAsync(stack, actor, context.Log, cancellationToken);
        }
        catch (StackException e)
        {
            context.Log($"✕ {e.Message}");

            return false;
        }

        // Members created before later ones existed may miss a wired value (e.g. a port): bring them in step.
        if ((await PlanChangesAsync(stack, cancellationToken)).Any(c => c.Environment.Count > 0 || c.Site != "none"))
        {
            await ApplyChangesAsync(stack, actor, context);
        }

        context.Log("④ Certificates");
        await EnsureCertificatesAsync(stack, context);

        context.Log("⑤ Repositories");
        var apps = await db.Apps.Include(a => a.Repository).Where(a => a.StackId == stack.Id).ToListAsync(cancellationToken);
        var missingKeys = false;

        foreach (var repository in apps.Select(a => a.Repository!).DistinctBy(r => r.Id))
        {
            var access = await deployments.TestAccessAsync(repository, cancellationToken);

            if (access.Ok)
            {
                context.Log($"  {repository.FullName}: reachable");
                continue;
            }

            missingKeys = true;
            context.Log(repository.Auth == RepositoryAuth.DeployKey
                ? $"✕ {repository.FullName}: add its deploy key on GitHub (Settings → Deploy keys), then run the install again:\n    {repository.PublicKey}"
                : $"✕ {repository.FullName}: {access.Error}");

            foreach (var submodule in repository.SubmoduleKeys)
            {
                context.Log($"    and for {submodule.Owner}/{submodule.Name}: {submodule.PublicKey}");
            }
        }

        if (missingKeys)
        {
            return false;
        }

        context.Log("⑥ Deploying");
        var ok = true;

        foreach (var roleName in preset.DeployOrder.Where(r => roles.Any(i => i.Role == r)))
        {
            var app = apps.FirstOrDefault(a => a.StackRole == roleName);

            if (app is null)
            {
                continue;
            }

            var last = await db.Releases.Where(r => r.AppId == app.Id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);

            if (app.CurrentRelease is not null && last?.Status != ReleaseStatus.Failed)
            {
                context.Log($"  {app.Name} is live ({app.CurrentRelease})");
                continue;
            }

            context.Log($"── {app.Name} ──");
            var outcome = await deployments.DeployAsync(app.Id, "hotel", actor?.Username, context);
            ok &= outcome.Success;
        }

        context.Log("⑦ Admin panel");
        var link = await FindAdminSetupLinkAsync(stack, cancellationToken);
        context.Log(link is null
            ? $"  Sign up on the website as {stack.Settings.GetValueOrDefault("ownerName")}: your admin panel setup link then appears on the hotel page."
            : "  Your admin panel setup link is on the hotel page.");

        context.Log(ok ? "✓ The hotel is installed." : "✕ Some parts didn't deploy; see above. Fix them and run the install again.");

        return ok;
    }

    /// <summary>
    /// Certificates for the hotel's sites, one after another (only one certificates job may run at a time). A site
    /// whose certificate fails keeps working over HTTP; the hourly renewal check retries it.
    /// </summary>
    private async Task EnsureCertificatesAsync(Stack stack, JobContext context)
    {
        foreach (var site in await db.Sites.Include(s => s.Routes).Where(s => s.StackId == stack.Id).ToListAsync(context.CancellationToken))
        {
            if (!await sites.EnsureCertificateAsync(site, line => context.Log($"  {line}"), context.CancellationToken))
            {
                context.Log($"  ! {site.Name}: no certificate yet (is {site.Domains[0]} pointing at this server?); it is retried every hour.");
            }
        }
    }

    private static async Task<bool> StreamAsync(AsyncServerStreamingCall<OutputLine> call, JobContext context)
    {
        using (call)
        {
            var ok = false;

            await foreach (var line in call.ResponseStream.ReadAllAsync(context.CancellationToken))
            {
                if (line.Finished)
                {
                    ok = line.ExitCode == 0;
                }
                else
                {
                    context.Log($"    {line.Line}");
                }
            }

            return ok;
        }
    }

    /// <summary>Turbo logs the owner's one-time admin setup link when they sign up (or start-up, if they exist).</summary>
    public async Task<string?> FindAdminSetupLinkAsync(Stack stack, CancellationToken cancellationToken)
    {
        var emulator = await db.Apps.FirstOrDefaultAsync(a => a.StackId == stack.Id && a.RecipeId == "turbo-emulator", cancellationToken);

        if (emulator is null)
        {
            return null;
        }

        try
        {
            var entries = await agent.Logs.QueryJournalAsync(new JournalQuery
            {
                Unit = $"tm-app-{emulator.Name}.service",
                Grep = "Admin panel setup for the owner",
                Limit = 5,
            }, cancellationToken: cancellationToken);

            return entries.Entries.Select(e => SetupLinkPattern().Match(e.Message)).LastOrDefault(m => m.Success)?.Groups["url"].Value;
        }
        catch (RpcException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,19}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"open (?<url>https?://\S+/setup#token=\S+?)(\s|$)")]
    private static partial Regex SetupLinkPattern();
}
