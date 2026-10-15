using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Data;
using TurboManager.Panel.Databases;
using TurboManager.Panel.Deploy;
using TurboManager.Panel.Sites;

namespace TurboManager.Panel.Recipes;

public sealed record RecipeRequest(
    string? Name,
    string? SystemUser,
    int? RepositoryId = null,
    string? Repository = null,
    string? Branch = null,
    bool Private = false,
    List<string>? PrivateSubmodules = null,
    List<string>? Domains = null,
    TlsMode TlsMode = TlsMode.LetsEncrypt,
    bool CloudflareDns = false,
    string? DatabaseEngine = null,
    Dictionary<string, string>? Inputs = null);

public sealed class RecipeException(string message) : Exception(message);

public sealed record PlannedRepository(int? Id, string Name, string FullName, string Branch, bool Submodules, bool Private, bool Reused);

public sealed record PlannedDatabase(string Engine, string Name, string User);

public sealed record PlannedService(string Command, int? Port, string? HealthPath, int MemoryMaxMb, int StopTimeoutSeconds, bool Console = false);

public sealed record PlannedApp(
    string WorkingDirectory,
    string? ServiceDirectory,
    List<string> Runtimes,
    string Build,
    string? StaticOutput,
    List<SharedPathModel> SharedPaths,
    List<string> DeployPaths,
    PlannedService? Service,
    Dictionary<string, string> Environment);

/// <summary>Everything a recipe will create, computed before anything is created (and shown as the preview).</summary>
public sealed record RecipePlan(
    string Recipe,
    string Name,
    string SystemUser,
    PlannedRepository? Repository,
    PlannedDatabase? Database,
    Dictionary<string, int> Ports,
    PlannedApp? App,
    SiteModel? Site,
    string? SiteDirectory,
    Dictionary<string, string> Inputs,
    List<string> Notes)
{
    /// <summary>Generated secrets (empty in a preview).</summary>
    internal Dictionary<string, string> Secrets { get; init; } = [];

    internal string? DatabasePassword { get; init; }
}

public sealed record RecipeResult(int? AppId, int? SiteId, int? RepositoryId, bool RepositoryCreated, SiteApplyResult? Site, List<string> Notes);

/// <summary>Which hotel (stack) and role an app or site is created for.</summary>
public sealed record StackLink(int StackId, string Role);

/// <summary>What re-rendering an app needs besides its inputs and ports (kept encrypted on the app).</summary>
public sealed record RecipeState(
    Dictionary<string, string> Secrets,
    PlannedDatabase? Database,
    string? DatabasePassword,
    Dictionary<string, string> Environment);

/// <summary>Turns a recipe plus the user's answers into a repository, database, app and site.</summary>
public sealed partial class RecipeService(
    PanelDbContext db,
    AgentChannelFactory agent,
    DeployService deployments,
    GitHubService github,
    DatabaseService databases,
    SiteService sites,
    IDataProtectionProvider dataProtection,
    AuditLog audit)
{
    private const string Generated = "(generated)";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.Recipes.v1");

    public RecipeState? GetState(App app) =>
        string.IsNullOrEmpty(app.RecipeStateProtected) ? null : JsonSerializer.Deserialize<RecipeState>(_protector.Unprotect(app.RecipeStateProtected));

    public void SetState(App app, RecipeState state) => app.RecipeStateProtected = _protector.Protect(JsonSerializer.Serialize(state));

    private static readonly HashSet<int> ReservedPorts = [22, 25, 53, 80, 443, 3306, 5432, 7800, 8443];

    public async Task<RecipePlan> PlanAsync(RecipeManifest recipe, RecipeRequest request, bool preview, CancellationToken cancellationToken)
    {
        var name = (request.Name ?? "").Trim();
        var user = (request.SystemUser ?? "").Trim();

        if (!NamePattern().IsMatch(name))
        {
            throw new RecipeException("Name: lowercase letters, digits and dashes (up to 40).");
        }

        if (!UserPattern().IsMatch(user) || DeployModels.ReservedUsers.Contains(user))
        {
            throw new RecipeException("Linux user: 2-32 lowercase letters, digits or dashes, and not a system account (e.g. \"hotel\").");
        }

        if (recipe.App is not null && await db.Apps.AnyAsync(a => a.Name == name, cancellationToken))
        {
            throw new RecipeException($"An app called {name} already exists.");
        }

        var domains = (request.Domains ?? []).Select(d => d.Trim().TrimEnd('.').ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
        var site = recipe.Site;

        if (site is { Required: true } && domains.Count == 0)
        {
            throw new RecipeException("This recipe needs a domain.");
        }

        if (site is { RequiresTls: true } && domains.Count > 0 && request.TlsMode == TlsMode.None)
        {
            throw new RecipeException($"{recipe.Name} needs HTTPS: choose a certificate.");
        }

        if (domains.Count > 0 && site is not null && await db.Sites.AnyAsync(s => s.Name == name, cancellationToken))
        {
            throw new RecipeException($"A site called {name} already exists.");
        }

        var inputs = ResolveInputs(recipe, request.Inputs);
        var repository = recipe.App is null ? null : await PlanRepositoryAsync(recipe, request, user, cancellationToken);
        var ports = await AllocatePortsAsync(recipe, cancellationToken);
        var secrets = recipe.Secrets.ToDictionary(s => s.Key, s => preview ? PreviewSecret(s) : GenerateSecret(s));
        var database = recipe.Database is null ? null : await PlanDatabaseAsync(recipe, request, name, cancellationToken);
        var databasePassword = database is null ? null : preview ? Generated : DatabaseService.GeneratePassword();

        var rendered = Render(recipe, new RenderState(name, user, domains, request.TlsMode, request.CloudflareDns, inputs, ports, secrets, database,
            databasePassword));

        if (rendered.App is { } app && DeployModels.ValidateApp(ToAppRequest(name, 0, app)) is { } appError)
        {
            throw new RecipeException(appError);
        }

        if (rendered.Site is { } siteModel)
        {
            var entity = new Site { Name = "" };
            siteModel.ApplyTo(entity);

            if (SiteValidator.Validate(entity, await sites.DomainsInUseAsync(null)) is { } error)
            {
                throw new RecipeException(error);
            }
        }

        return new RecipePlan(recipe.Id, name, user, repository, database, ports, rendered.App, rendered.Site, rendered.SiteDirectory, inputs, rendered.Notes)
        {
            Secrets = preview ? [] : secrets,
            DatabasePassword = preview ? null : databasePassword,
        };
    }

    /// <summary>Everything a recipe's templates are rendered from; kept (encrypted) so an app can be re-rendered later.</summary>
    public sealed record RenderState(
        string Name,
        string User,
        List<string> Domains,
        TlsMode TlsMode,
        bool CloudflareDns,
        Dictionary<string, string> Inputs,
        Dictionary<string, int> Ports,
        Dictionary<string, string> Secrets,
        PlannedDatabase? Database,
        string? DatabasePassword);

    public sealed record Rendered(PlannedApp? App, SiteModel? Site, string? SiteDirectory, List<string> Notes);

    /// <summary>Renders the app, site and notes. Pure: the same state always gives the same result.</summary>
    public static Rendered Render(RecipeManifest recipe, RenderState state)
    {
        var values = Values(recipe, state);
        var app = recipe.App is null ? null : PlanApp(recipe.App, values, state.Ports);
        var site = recipe.Site is null || state.Domains.Count == 0 ? null : PlanSite(recipe.Site, state, values);
        var notes = recipe.Notes.Select(n => Render(n, values)).Where(n => n.Length > 0).ToList();

        return new Rendered(app, site, values["site.dir"] is { Length: > 0 } dir ? dir : null, notes);
    }

    /// <summary>What an app offers the rest of its hotel (exports only see the site and ports).</summary>
    public static Dictionary<string, string> Exports(RecipeManifest recipe, string? domain, TlsMode tlsMode, IReadOnlyDictionary<string, int> ports)
    {
        var values = new Dictionary<string, string>
        {
            ["site.domain"] = domain ?? "",
            ["site.url"] = SiteUrl(domain, tlsMode),
            ["site.dir"] = "",
        };

        foreach (var port in recipe.Ports)
        {
            values[$"port.{port.Name}"] = ports.TryGetValue(port.Name, out var value) ? value.ToString(CultureInfo.InvariantCulture) : "";
        }

        return recipe.Exports.ToDictionary(e => e.Key, e => RecipeTemplate.Render(e.Value, values) is { UsedEmptyValue: false } r ? r.Text : "");
    }

    /// <summary>A site model as it reads back once saved (trimmed, normalised): comparable with SiteModel.From(site).</summary>
    public static string Canonical(SiteModel model)
    {
        var site = new Site { Name = "" };
        model.ApplyTo(site);

        return JsonSerializer.Serialize(SiteModel.From(site));
    }

    private static string SiteUrl(string? domain, TlsMode tlsMode) =>
        string.IsNullOrEmpty(domain) ? "" : $"{(tlsMode == TlsMode.None ? "http" : "https")}://{domain}";

    private static Dictionary<string, string> Values(RecipeManifest recipe, RenderState state)
    {
        var appDirectory = $"/home/{state.User}/apps/{state.Name}";
        var values = new Dictionary<string, string>
        {
            ["user"] = state.User,
            ["app.name"] = state.Name,
            ["app.dir"] = appDirectory,
            ["app.shared"] = $"{appDirectory}/shared",
            ["app.static"] = "",
            ["site.domain"] = state.Domains.FirstOrDefault() ?? "",
            ["site.url"] = SiteUrl(state.Domains.FirstOrDefault(), state.TlsMode),
            ["site.dir"] = recipe.Site is { Directory: true } ? $"/home/{state.User}/sites/{state.Name}" : "",
        };

        foreach (var (key, value) in state.Inputs)
        {
            values[$"input.{key}"] = value;
        }

        foreach (var input in recipe.Inputs.Where(i => !values.ContainsKey($"input.{i.Key}")))
        {
            values[$"input.{input.Key}"] = "";
        }

        foreach (var (key, port) in state.Ports)
        {
            values[$"port.{key}"] = port.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var secret in recipe.Secrets)
        {
            foreach (var (key, value) in RecipeCatalog.SecretValues(secret, state.Secrets.GetValueOrDefault(secret.Key, "")))
            {
                values[key] = value;
            }
        }

        if (state.Database is { } database)
        {
            values["db.engine"] = database.Engine;
            values["db.host"] = "127.0.0.1";
            values["db.port"] = DatabaseService.Port(DatabaseService.Parse(database.Engine)!.Value).ToString(CultureInfo.InvariantCulture);
            values["db.name"] = database.Name;
            values["db.user"] = database.User;
            values["db.password"] = state.DatabasePassword ?? "";
        }

        return values;
    }

    public async Task<RecipeResult> CreateAsync(RecipeManifest recipe, RecipeRequest request, PanelUser? actor, CancellationToken cancellationToken,
        StackLink? stack = null, bool startCertificateJob = true)
    {
        var plan = await PlanAsync(recipe, request, preview: false, cancellationToken);

        await agent.Deploy.EnsureAppUserAsync(new AppUserRequest { User = plan.SystemUser }, cancellationToken: cancellationToken);

        GitRepository? repository = null;
        var repositoryCreated = false;

        if (plan.Repository is { } planned)
        {
            if (planned.Id is { } id)
            {
                repository = await db.Repositories.FirstAsync(r => r.Id == id, cancellationToken);
                repository.Submodules |= planned.Submodules;
            }
            else
            {
                repository = new GitRepository
                {
                    Name = planned.Name,
                    SystemUser = plan.SystemUser,
                    Owner = "",
                    RepositoryName = "",
                    WebhookSecretProtected = deployments.Protect(DeployService.NewWebhookSecret()),
                };
                DeployModels.Apply(repository, new RepositoryRequest(planned.Name, plan.SystemUser, planned.FullName, planned.Branch, planned.Private,
                    planned.Submodules, request.PrivateSubmodules));
                await deployments.PrepareRepositoryAsync(repository, cancellationToken);
                db.Repositories.Add(repository);
                repositoryCreated = true;
                audit.Record("repository.added", actor, target: repository.FullName, details: $"user {repository.SystemUser}, recipe {recipe.Id}");
            }

            await db.SaveChangesAsync(cancellationToken);

            // With GitHub connected, private repositories get their deploy keys without a trip to GitHub's settings.
            if (repositoryCreated && repository.Auth == RepositoryAuth.DeployKey && await github.GetAccountAsync() is { } account)
            {
                var steps = await github.InstallAsync(repository, null, cancellationToken);
                plan.Notes.AddRange(steps.Select(s => s.Status == "ok"
                    ? $"{s.Label}: added through GitHub account {account.Login}."
                    : $"{s.Label}: {s.Detail} Add it by hand."));
            }
        }

        if (plan.Database is { } database)
        {
            await databases.CreateDatabaseAsync(DatabaseService.Parse(database.Engine)!.Value, database.Name, database.User, plan.DatabasePassword,
                cancellationToken);
            audit.Record("database.created", actor, target: $"{database.Engine}/{database.Name}", details: $"recipe {recipe.Id}");
        }

        if (plan.SiteDirectory is not null)
        {
            await agent.Deploy.EnsureSiteDirectoryAsync(new AppRef { User = plan.SystemUser, App = plan.Name }, cancellationToken: cancellationToken);
        }

        App? app = null;

        if (plan.App is { } planned2)
        {
            app = new App { Name = "", SystemUser = plan.SystemUser };
            DeployModels.Apply(app, ToAppRequest(plan.Name, repository!.Id, planned2));
            app.RecipeId = recipe.Id;
            app.RecipeInputs = plan.Inputs;
            app.Ports = plan.Ports;
            app.StackId = stack?.StackId;
            app.StackRole = stack?.Role;
            SetState(app, new RecipeState(plan.Secrets, plan.Database, plan.DatabasePassword, planned2.Environment));
            deployments.SetEnvironment(app, planned2.Environment);
            db.Apps.Add(app);
            audit.Record("app.created", actor, target: plan.Name, details: $"recipe {recipe.Id}, {repository.FullName}");
        }

        await db.SaveChangesAsync(cancellationToken);

        Site? site = null;
        SiteApplyResult? applied = null;

        if (plan.Site is { } siteModel)
        {
            site = new Site { Name = "" };
            siteModel.ApplyTo(site);
            site.RecipeId = recipe.Id;
            site.RecipeRendered = Canonical(siteModel);
            site.StackId = stack?.StackId;
            site.StackRole = stack?.Role;
            db.Sites.Add(site);
            audit.Record("site.created", actor, target: site.Name, details: $"{string.Join(", ", site.Domains)} (recipe {recipe.Id})");
            await db.SaveChangesAsync(cancellationToken);
            applied = await sites.ApplyAsync(site, actor, requestCertificate: true, cancellationToken, startCertificateJob);
        }

        return new RecipeResult(app?.Id, site?.Id, repository?.Id, repositoryCreated, applied, plan.Notes);
    }

    private static Dictionary<string, string> ResolveInputs(RecipeManifest recipe, Dictionary<string, string>? given)
    {
        var inputs = new Dictionary<string, string>();

        foreach (var input in recipe.Inputs)
        {
            var value = (given?.GetValueOrDefault(input.Key) ?? input.Default).Trim();

            if (value.Contains('\n') || value.Contains('\r') || value.Length > 2000)
            {
                throw new RecipeException($"{input.Label}: a single line, please.");
            }

            if (value.Length == 0)
            {
                if (input.Required)
                {
                    throw new RecipeException($"{input.Label} is required.");
                }
            }
            else if (input.Kind == "url" && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ws" or "wss")))
            {
                throw new RecipeException($"{input.Label}: enter a full address, like https://example.com.");
            }
            else if (input.Kind == "number" && !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new RecipeException($"{input.Label}: enter a whole number.");
            }
            else if (input.Kind == "bool")
            {
                value = value is "true" or "on" or "1" ? "true" : "";
            }

            inputs[input.Key] = value;
        }

        return inputs;
    }

    private async Task<PlannedRepository> PlanRepositoryAsync(RecipeManifest recipe, RecipeRequest request, string user, CancellationToken cancellationToken)
    {
        var submodules = recipe.Source?.Submodules ?? false;

        if (request.RepositoryId is { } id)
        {
            var existing = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new RecipeException("That repository doesn't exist.");

            if (existing.SystemUser != user)
            {
                throw new RecipeException($"{existing.FullName} belongs to {existing.SystemUser}; apps run as the repository's user.");
            }

            return new PlannedRepository(existing.Id, existing.Name, existing.FullName, existing.Branch, existing.Submodules || submodules,
                existing.Auth == RepositoryAuth.DeployKey, true);
        }

        var source = string.IsNullOrWhiteSpace(request.Repository) ? recipe.Source?.Repository : request.Repository;

        if (DeployModels.ParseRepository(source) is not var (owner, repoName))
        {
            throw new RecipeException("Repository: use owner/name or the GitHub URL.");
        }

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? recipe.Source?.Branch ?? "main" : request.Branch.Trim();

        // Several apps build from one checkout (turbo-cloud: emulator, admin, web).
        var same = await db.Repositories.Where(r => r.SystemUser == user && r.Branch == branch).ToListAsync(cancellationToken);

        if (same.FirstOrDefault(r => string.Equals(r.FullName, $"{owner}/{repoName}", StringComparison.OrdinalIgnoreCase)) is { } reuse)
        {
            return new PlannedRepository(reuse.Id, reuse.Name, reuse.FullName, reuse.Branch, reuse.Submodules || submodules,
                reuse.Auth == RepositoryAuth.DeployKey, true);
        }

        var names = await db.Repositories.Where(r => r.SystemUser == user).Select(r => r.Name).ToListAsync(cancellationToken);
        var baseName = SlugCleanup().Replace(repoName.ToLowerInvariant(), "-").Trim('-');
        baseName = baseName.Length == 0 ? "repo" : baseName[..Math.Min(36, baseName.Length)];
        var name = baseName;

        for (var i = 2; names.Contains(name); i++)
        {
            name = $"{baseName}-{i}";
        }

        var model = new RepositoryRequest(name, user, $"{owner}/{repoName}", branch, request.Private, submodules, request.PrivateSubmodules);

        if (DeployModels.ValidateRepository(model) is { } error)
        {
            throw new RecipeException(error);
        }

        return new PlannedRepository(null, name, $"{owner}/{repoName}", branch, submodules, request.Private, false);
    }

    /// <summary>Each port gets its default unless another app has it or something already listens there.</summary>
    private async Task<Dictionary<string, int>> AllocatePortsAsync(RecipeManifest recipe, CancellationToken cancellationToken)
    {
        var used = new HashSet<int>(ReservedPorts);

        foreach (var app in await db.Apps.ToListAsync(cancellationToken))
        {
            used.UnionWith(app.Ports.Values);

            if (app.ServicePort is { } port)
            {
                used.Add(port);
            }
        }

        if (recipe.Ports.Count > 0)
        {
            try
            {
                var listening = await agent.Firewall.ListListeningSocketsAsync(new Empty(), cancellationToken: cancellationToken);
                used.UnionWith(listening.Sockets.Where(s => s.Protocol == "tcp").Select(s => s.Port));
            }
            catch (RpcException)
            {
                // Best effort: the panel's own records still prevent clashes between apps.
            }
        }

        var ports = new Dictionary<string, int>();

        foreach (var port in recipe.Ports)
        {
            var candidate = port.Default;

            while (used.Contains(candidate))
            {
                candidate++;
            }

            if (candidate > 65535)
            {
                throw new RecipeException($"No free port for {port.Label}.");
            }

            used.Add(candidate);
            ports[port.Name] = candidate;
        }

        return ports;
    }

    private async Task<PlannedDatabase> PlanDatabaseAsync(RecipeManifest recipe, RecipeRequest request, string name, CancellationToken cancellationToken)
    {
        var engines = await agent.Databases.GetEnginesAsync(new Empty(), cancellationToken: cancellationToken);
        var installed = engines.Engines.Where(e => e.Installed).Select(e => DatabaseService.Slug(e.Engine)).ToList();
        var allowed = recipe.Database!.Engines;
        var engine = string.IsNullOrWhiteSpace(request.DatabaseEngine) ? allowed.FirstOrDefault(installed.Contains) : request.DatabaseEngine;

        if (engine is null)
        {
            throw new RecipeException($"{recipe.Name} needs {string.Join(" or ", allowed.Select(Display))}. Install one on the Databases page first.");
        }

        if (!allowed.Contains(engine))
        {
            throw new RecipeException($"{recipe.Name} works with {string.Join(" or ", allowed.Select(Display))}, not {Display(engine)}.");
        }

        if (!installed.Contains(engine))
        {
            throw new RecipeException($"{Display(engine)} isn't installed. Install it on the Databases page first.");
        }

        var databaseName = RecipeTemplate.Render(recipe.Database.Name, new Dictionary<string, string> { ["app.name"] = name }).Text;

        if (!DatabaseNamePattern().IsMatch(databaseName))
        {
            throw new RecipeException($"\"{databaseName}\" can't be a database name.");
        }

        var existing = await agent.Databases.ListDatabasesAsync(new EngineRef { Engine = DatabaseService.Parse(engine)!.Value }, cancellationToken: cancellationToken);

        if (existing.Databases.Any(d => d.Name == databaseName))
        {
            throw new RecipeException($"{Display(engine)} already has a database called {databaseName}. Pick another app name.");
        }

        return new PlannedDatabase(engine, databaseName, databaseName);
    }

    private static PlannedApp PlanApp(RecipeApp app, Dictionary<string, string> values, Dictionary<string, int> ports)
    {
        var staticOutput = Render(app.Static, values);

        if (staticOutput.Length > 0)
        {
            values["app.static"] = $"{values["app.dir"]}/current/{staticOutput.Trim('/')}";
        }

        PlannedService? service = null;

        if (app.Service is { } daemon)
        {
            var health = Render(daemon.Health, values);
            var memory = Render(daemon.MemoryMaxMb, values);

            service = new PlannedService(
                Render(daemon.Command, values),
                daemon.Port.Length > 0 ? ports[daemon.Port] : null,
                health is "" or "tcp" ? null : health,
                int.TryParse(memory, NumberStyles.None, CultureInfo.InvariantCulture, out var mb) ? mb : 0,
                daemon.StopTimeoutSeconds,
                daemon.Console);

            if (service.Command.Length == 0)
            {
                throw new RecipeException("The service needs a start command.");
            }
        }

        var environment = new Dictionary<string, string>();

        foreach (var (key, value) in app.Environment)
        {
            switch (value)
            {
                case string template:
                    var rendered = RecipeTemplate.Render(template, values);

                    if (!rendered.UsedEmptyValue)
                    {
                        environment[key] = rendered.Text;
                    }

                    break;
                case IDictionary<object, object> map:
                    var json = new SortedDictionary<string, string>(StringComparer.Ordinal);

                    foreach (var (entryKey, entryValue) in map)
                    {
                        var entry = RecipeTemplate.Render(entryValue?.ToString() ?? "", values);

                        if (!entry.UsedEmptyValue)
                        {
                            json[entryKey.ToString()!] = entry.Text;
                        }
                    }

                    if (json.Count > 0)
                    {
                        environment[key] = JsonSerializer.Serialize(json);
                    }

                    break;
            }
        }

        return new PlannedApp(
            app.WorkingDirectory,
            app.ServiceDirectory.Length == 0 ? null : app.ServiceDirectory,
            app.Runtimes,
            Render(app.Build, values),
            staticOutput.Length == 0 ? null : staticOutput,
            app.Shared.Select(s => new SharedPathModel(s.Path, s.Directory)).ToList(),
            app.DeployPaths,
            service,
            environment);
    }

    private static SiteModel PlanSite(RecipeSite site, RenderState state, Dictionary<string, string> values) => new(
        state.Name,
        state.Domains,
        site.Routes.Select(r => new SiteRouteModel(
            r.Path,
            System.Enum.Parse<RouteKind>(Render(r.Kind, values)),
            Render(r.Target, values),
            r.Cors,
            r.CacheSeconds,
            Render(r.Snippet, values) is { Length: > 0 } snippet ? snippet : null)).ToList(),
        state.TlsMode,
        ForceHttps: state.TlsMode != TlsMode.None,
        SecurityHeaders: site.SecurityHeaders,
        ClientMaxBodyMb: site.ClientMaxBodyMb,
        ServerSnippet: Render(site.ServerSnippet, values) is { Length: > 0 } server ? server : null,
        CloudflareDns: state.CloudflareDns);

    private static AppRequest ToAppRequest(string name, int repositoryId, PlannedApp app) => new(
        name,
        repositoryId,
        app.WorkingDirectory,
        app.Build,
        app.Runtimes,
        app.SharedPaths,
        app.StaticOutput,
        app.Service?.Command,
        app.Service?.Port,
        app.Service?.HealthPath,
        app.Service?.MemoryMaxMb ?? 0,
        app.Service?.StopTimeoutSeconds ?? 30,
        DeployPaths: app.DeployPaths,
        ServiceDirectory: app.ServiceDirectory,
        Console: app.Service?.Console ?? false);

    private static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        try
        {
            return RecipeTemplate.Render(template, values).Text;
        }
        catch (RecipeTemplateException e)
        {
            throw new RecipeException(e.Message);
        }
    }

    private static string GenerateSecret(RecipeSecret secret) => secret.Kind switch
    {
        "base64-32" => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        "password" => DatabaseService.GeneratePassword(),
        "rsa-turbo" => TurboCryptoKeys.Generate() is var keys ? $"{keys.Exponent}:{keys.Modulus}:{keys.PrivateExponent}" : "",
        _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
    };

    private static string PreviewSecret(RecipeSecret secret) => secret.Kind == "rsa-turbo" ? $"3:{Generated}:{Generated}" : Generated;

    private static string Display(string engine) => engine switch { "mysql" => "MySQL", "mariadb" => "MariaDB", "postgres" => "PostgreSQL", _ => engine };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex DatabaseNamePattern();

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex SlugCleanup();
}
