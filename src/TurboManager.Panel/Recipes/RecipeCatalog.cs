using System.Text.RegularExpressions;
using TurboManager.Panel.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TurboManager.Panel.Recipes;

/// <summary>
/// The recipes and hotel presets shipped with the panel (embedded Recipes/Manifests/*.yaml and Recipes/Presets/*.yaml),
/// validated when loaded.
/// </summary>
public sealed partial class RecipeCatalog
{
    public IReadOnlyList<RecipeManifest> All { get; }

    public IReadOnlyList<StackPreset> Presets { get; }

    public RecipeCatalog() : this(LoadEmbedded("Manifests"), LoadEmbedded("Presets"))
    {
    }

    public RecipeCatalog(IEnumerable<(string Source, string Yaml)> manifests, IEnumerable<(string Source, string Yaml)>? presets = null)
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var all = new List<RecipeManifest>();

        foreach (var (source, yaml) in manifests)
        {
            var manifest = Parse<RecipeManifest>(deserializer, source, yaml);

            if (Validate(manifest) is { } error)
            {
                throw new InvalidOperationException($"Recipe {source}: {error}");
            }

            if (all.Any(m => m.Id == manifest.Id))
            {
                throw new InvalidOperationException($"Recipe {source}: the id \"{manifest.Id}\" is used twice.");
            }

            all.Add(manifest);
        }

        All = all;

        var loaded = new List<StackPreset>();

        foreach (var (source, yaml) in presets ?? [])
        {
            var preset = Parse<StackPreset>(deserializer, source, yaml);

            if (ValidatePreset(preset) is { } error)
            {
                throw new InvalidOperationException($"Preset {source}: {error}");
            }

            loaded.Add(preset);
        }

        Presets = loaded;
    }

    public RecipeManifest? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    public StackPreset? FindPreset(string id) => Presets.FirstOrDefault(p => p.Id == id);

    private static T Parse<T>(IDeserializer deserializer, string source, string yaml)
    {
        try
        {
            return deserializer.Deserialize<T>(yaml);
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new InvalidOperationException($"{source}: {e.Message}", e);
        }
    }

    private static IEnumerable<(string, string)> LoadEmbedded(string folder)
    {
        var assembly = typeof(RecipeCatalog).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains($".{folder}.", StringComparison.Ordinal) && n.EndsWith(".yaml", StringComparison.Ordinal)).Order())
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);

            yield return (name, reader.ReadToEnd());
        }
    }

    /// <summary>Roles and recipes exist, and every wired input of a member points at a real export or setting.</summary>
    public string? ValidatePreset(StackPreset preset)
    {
        if (!SlugPattern().IsMatch(preset.Id) || preset.Name.Length == 0 || preset.Roles.Count == 0)
        {
            return "needs an id, a name and roles.";
        }

        if (preset.Roles.GroupBy(r => r.Role).FirstOrDefault(g => g.Count() > 1 || !SlugPattern().IsMatch(g.Key)) is { } badRole)
        {
            return $"role \"{badRole.Key}\" is duplicated or not a valid name.";
        }

        if (preset.Roles.FirstOrDefault(r => Find(r.Recipe) is null) is { } missing)
        {
            return $"role {missing.Role} uses the unknown recipe \"{missing.Recipe}\".";
        }

        if (preset.Roles.FirstOrDefault(r => r.Host != "@" && !HostPattern().IsMatch(r.Host)) is { } host)
        {
            return $"role {host.Role} has an invalid host \"{host.Host}\".";
        }

        if (preset.DeployOrder.FirstOrDefault(d => preset.Roles.All(r => r.Role != d)) is { } unknown)
        {
            return $"deployOrder names the unknown role \"{unknown}\".";
        }

        foreach (var role in preset.Roles)
        {
            foreach (var input in Find(role.Recipe)!.Inputs.Where(i => i.From.Length > 0))
            {
                var (source, export) = (input.From[..input.From.IndexOf('.')], input.From[(input.From.IndexOf('.') + 1)..]);

                if (source == "settings")
                {
                    if (preset.Settings.All(s => s.Key != export))
                    {
                        return $"{role.Role}.{input.Key} comes from the setting \"{export}\", which the preset doesn't ask for.";
                    }
                }
                else if (preset.Roles.FirstOrDefault(r => r.Role == source) is not { } other)
                {
                    return $"{role.Role}.{input.Key} comes from the role \"{source}\", which the preset doesn't have.";
                }
                else if (!Find(other.Recipe)!.Exports.ContainsKey(export))
                {
                    return $"{role.Role}.{input.Key} comes from {source}.{export}, which {other.Recipe} doesn't export.";
                }
            }
        }

        return null;
    }

    public static readonly string[] InputKinds = ["text", "url", "bool", "number"];

    public static readonly string[] SecretKinds = ["base64-32", "hex-32", "password", "rsa-turbo"];

    public static readonly string[] Engines = ["mysql", "mariadb", "postgres"];

    /// <summary>Every value a template may refer to, with sample values (validation and previews).</summary>
    public static Dictionary<string, string> SampleValues(RecipeManifest recipe)
    {
        var values = new Dictionary<string, string>
        {
            ["user"] = "hotel",
            ["app.name"] = "sample",
            ["app.dir"] = "/home/hotel/apps/sample",
            ["app.shared"] = "/home/hotel/apps/sample/shared",
            ["app.static"] = "/home/hotel/apps/sample/current/dist",
            ["site.domain"] = "example.com",
            ["site.url"] = "https://example.com",
            ["site.dir"] = "/home/hotel/sites/sample",
        };

        foreach (var input in recipe.Inputs)
        {
            values[$"input.{input.Key}"] = input.Kind switch { "url" => "https://example.com", "bool" => "true", "number" => "1", _ => "sample" };
        }

        foreach (var port in recipe.Ports)
        {
            values[$"port.{port.Name}"] = port.Default.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var secret in recipe.Secrets)
        {
            foreach (var (key, value) in SecretValues(secret, "sample"))
            {
                values[key] = value;
            }
        }

        if (recipe.Database is not null)
        {
            foreach (var key in new[] { "engine", "host", "port", "name", "user", "password" })
            {
                values[$"db.{key}"] = "sample";
            }
        }

        return values;
    }

    /// <summary>The template values a secret provides (rsa-turbo has three parts).</summary>
    public static IEnumerable<(string Key, string Value)> SecretValues(RecipeSecret secret, string value)
    {
        if (secret.Kind != "rsa-turbo")
        {
            yield return ($"secret.{secret.Key}", value);
            yield break;
        }

        var parts = value.Split(':');

        yield return ($"secret.{secret.Key}.exponent", parts[0]);
        yield return ($"secret.{secret.Key}.modulus", parts.ElementAtOrDefault(1) ?? value);
        yield return ($"secret.{secret.Key}.private", parts.ElementAtOrDefault(2) ?? value);
    }

    /// <summary>Every template in a recipe, with where it is (for error messages).</summary>
    public static IEnumerable<(string Where, string Template)> Templates(RecipeManifest recipe)
    {
        if (recipe.Database is { } database)
        {
            yield return ("database.name", database.Name);
        }

        if (recipe.App is { } app)
        {
            yield return ("app.build", app.Build);
            yield return ("app.static", app.Static);

            if (app.Service is { } service)
            {
                yield return ("app.service.command", service.Command);
                yield return ("app.service.memoryMaxMb", service.MemoryMaxMb);
                yield return ("app.service.health", service.Health);
            }

            foreach (var (key, value) in app.Environment)
            {
                foreach (var template in EnvironmentTemplates(value))
                {
                    yield return ($"app.environment.{key}", template);
                }
            }
        }

        if (recipe.Site is { } site)
        {
            yield return ("site.serverSnippet", site.ServerSnippet);

            foreach (var route in site.Routes)
            {
                yield return ($"site.routes[{route.Path}].kind", route.Kind);
                yield return ($"site.routes[{route.Path}].target", route.Target);
                yield return ($"site.routes[{route.Path}].snippet", route.Snippet);
            }
        }

        foreach (var note in recipe.Notes)
        {
            yield return ("notes", note);
        }
    }

    private static IEnumerable<string> EnvironmentTemplates(object value) => value switch
    {
        string text => [text],
        IDictionary<object, object> map => map.Values.Select(v => v?.ToString() ?? ""),
        _ => [],
    };

    public static string? Validate(RecipeManifest recipe)
    {
        if (!SlugPattern().IsMatch(recipe.Id) || recipe.Name.Length == 0)
        {
            return "needs an id (lowercase slug) and a name.";
        }

        if (recipe.Inputs.GroupBy(i => i.Key).FirstOrDefault(g => g.Count() > 1 || !KeyPattern().IsMatch(g.Key)) is { } badInput)
        {
            return $"input \"{badInput.Key}\" is duplicated or not a valid key.";
        }

        if (recipe.Inputs.FirstOrDefault(i => !InputKinds.Contains(i.Kind) || i.Label.Length == 0) is { } input)
        {
            return $"input \"{input.Key}\" needs a label and a kind ({string.Join(", ", InputKinds)}).";
        }

        if (recipe.Ports.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1 || !KeyPattern().IsMatch(g.Key)) is { } badPort)
        {
            return $"port \"{badPort.Key}\" is duplicated or not a valid name.";
        }

        if (recipe.Ports.FirstOrDefault(p => p.Default is < 1024 or > 65535) is { } port)
        {
            return $"port \"{port.Name}\" needs a default between 1024 and 65535.";
        }

        if (recipe.Secrets.FirstOrDefault(s => !SecretKinds.Contains(s.Kind) || !KeyPattern().IsMatch(s.Key)) is { } secret)
        {
            return $"secret \"{secret.Key}\" needs a valid key and kind ({string.Join(", ", SecretKinds)}).";
        }

        if (recipe.Database is { } database && (database.Engines.Count == 0 || database.Engines.Any(e => !Engines.Contains(e))))
        {
            return $"database engines must be some of {string.Join(", ", Engines)}.";
        }

        if (recipe.App is null && recipe.Site is not { Directory: true })
        {
            return "needs an app, or a site that serves a directory.";
        }

        if (recipe.App?.Service is { } service && service.Port.Length > 0 && recipe.Ports.All(p => p.Name != service.Port))
        {
            return $"the service checks port \"{service.Port}\", which isn't in ports.";
        }

        if (recipe.App?.Runtimes.FirstOrDefault(r => r is not ("dotnet" or "node")) is { } runtime)
        {
            return $"unknown runtime \"{runtime}\".";
        }

        var values = SampleValues(recipe);

        if (recipe.Site?.Routes.FirstOrDefault(r => !IsRouteKind(r.Kind, values)) is { } route)
        {
            return $"route {route.Path} has an unknown kind \"{route.Kind}\".";
        }

        foreach (var (where, template) in Templates(recipe))
        {
            try
            {
                RecipeTemplate.Render(template, values);
            }
            catch (RecipeTemplateException e)
            {
                return $"{where}: {e.Message}";
            }
        }

        var exportValues = values.Where(v => v.Key.StartsWith("site.", StringComparison.Ordinal) || v.Key.StartsWith("port.", StringComparison.Ordinal))
            .ToDictionary();

        foreach (var (key, template) in recipe.Exports)
        {
            try
            {
                RecipeTemplate.Render(template, exportValues);
            }
            catch (RecipeTemplateException e)
            {
                return $"exports.{key}: {e.Message} Exports may only use site.* and port.* values.";
            }
        }

        if (recipe.Inputs.FirstOrDefault(i => i.From.Length > 0 && !FromPattern().IsMatch(i.From)) is { } wired)
        {
            return $"input \"{wired.Key}\": from must be \"<role>.<export>\" or \"settings.<key>\".";
        }

        return null;
    }

    private static bool IsRouteKind(string kind, IReadOnlyDictionary<string, string> values)
    {
        try
        {
            return Enum.TryParse<RouteKind>(RecipeTemplate.Render(kind, values).Text, out _);
        }
        catch (RecipeTemplateException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,39}\.[a-zA-Z][a-zA-Z0-9_]{0,63}$")]
    private static partial Regex FromPattern();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();
}
