namespace TurboManager.Panel.Recipes;

/// <summary>
/// A recipe: how to set up one kind of app (Turbo emulator, Nitro client, ...) on this server. Recipes are
/// YAML files shipped with the panel (Recipes/Manifests). Strings marked "template" may use
/// <c>{{ ... }}</c> expressions, see <see cref="RecipeTemplate"/>.
/// </summary>
public sealed class RecipeManifest
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>One line for the recipe picker.</summary>
    public string Summary { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>turbo | nitro | general</summary>
    public string Category { get; set; } = "general";

    /// <summary>The repository it is usually deployed from (pre-filled, can be changed).</summary>
    public RecipeSource? Source { get; set; }

    public List<RecipeInput> Inputs { get; set; } = [];

    public RecipeDatabase? Database { get; set; }

    /// <summary>Ports on 127.0.0.1, allocated by the panel (the default when it's free).</summary>
    public List<RecipePort> Ports { get; set; } = [];

    /// <summary>Values generated once at creation (kept in the app's encrypted environment).</summary>
    public List<RecipeSecret> Secrets { get; set; } = [];

    /// <summary>The app built from the repository; null for a site that only serves uploaded files.</summary>
    public RecipeApp? App { get; set; }

    public RecipeSite? Site { get; set; }

    /// <summary>What to do next, shown after creation (templates).</summary>
    public List<string> Notes { get; set; } = [];
}

public sealed class RecipeSource
{
    /// <summary>owner/name on GitHub.</summary>
    public string Repository { get; set; } = "";

    public string Branch { get; set; } = "main";

    public bool Submodules { get; set; }
}

public sealed class RecipeInput
{
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string Help { get; set; } = "";

    /// <summary>text | url | bool | number</summary>
    public string Kind { get; set; } = "text";

    public string Default { get; set; } = "";

    public string Placeholder { get; set; } = "";

    public bool Required { get; set; }
}

public sealed class RecipeDatabase
{
    /// <summary>Engines the app works with, preferred first: mysql | mariadb | postgres.</summary>
    public List<string> Engines { get; set; } = [];

    /// <summary>Template for the database (and user) name.</summary>
    public string Name { get; set; } = "{{ app.name | ident }}";
}

public sealed class RecipePort
{
    public string Name { get; set; } = "";

    public int Default { get; set; }

    public string Label { get; set; } = "";
}

public sealed class RecipeSecret
{
    public string Key { get; set; } = "";

    /// <summary>base64-32 | hex-32 | password | rsa-turbo (exposes .exponent, .modulus, .private)</summary>
    public string Kind { get; set; } = "hex-32";
}

public sealed class RecipeApp
{
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Where the service starts, relative to the release (defaults to the working directory).</summary>
    public string ServiceDirectory { get; set; } = "";

    public List<string> Runtimes { get; set; } = [];

    /// <summary>Template.</summary>
    public string Build { get; set; } = "";

    /// <summary>Built files a site serves, relative to the release root.</summary>
    public string Static { get; set; } = "";

    public List<RecipeSharedPath> Shared { get; set; } = [];

    public List<string> DeployPaths { get; set; } = [];

    public RecipeDaemon? Service { get; set; }

    /// <summary>
    /// Templates. A value may also be a mapping, rendered as a JSON object. An entry whose template uses an
    /// empty value is left out, so the app's own default applies.
    /// </summary>
    public Dictionary<string, object> Environment { get; set; } = [];
}

public sealed class RecipeSharedPath
{
    public string Path { get; set; } = "";

    public bool Directory { get; set; }
}

public sealed class RecipeDaemon
{
    public string Command { get; set; } = "";

    /// <summary>Name of the port (from Ports) checked after a deploy.</summary>
    public string Port { get; set; } = "";

    /// <summary>"tcp" (connect) or an HTTP path such as "/health".</summary>
    public string Health { get; set; } = "tcp";

    /// <summary>Template, in MB; "" or 0 for no limit.</summary>
    public string MemoryMaxMb { get; set; } = "";

    public int StopTimeoutSeconds { get; set; } = 30;
}

public sealed class RecipeSite
{
    /// <summary>The app is useless without a domain (e.g. a client people open in a browser).</summary>
    public bool Required { get; set; }

    /// <summary>Needs HTTPS (e.g. passkeys); plain HTTP isn't offered.</summary>
    public bool RequiresTls { get; set; }

    /// <summary>Serve /home/&lt;user&gt;/sites/&lt;name&gt; (uploaded files) instead of an app's build.</summary>
    public bool Directory { get; set; }

    public bool SecurityHeaders { get; set; } = true;

    public int ClientMaxBodyMb { get; set; } = 10;

    /// <summary>Template.</summary>
    public string ServerSnippet { get; set; } = "";

    public List<RecipeRoute> Routes { get; set; } = [];
}

public sealed class RecipeRoute
{
    public string Path { get; set; } = "/";

    /// <summary>Static | Spa | Proxy | WebSocket | Redirect</summary>
    public string Kind { get; set; } = "Static";

    /// <summary>Template.</summary>
    public string Target { get; set; } = "";

    public bool Cors { get; set; }

    public int? CacheSeconds { get; set; }

    /// <summary>Template.</summary>
    public string Snippet { get; set; } = "";
}
