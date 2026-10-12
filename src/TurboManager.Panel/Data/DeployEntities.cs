namespace TurboManager.Panel.Data;

public enum RepositoryAuth
{
    /// <summary>Public repository, cloned over HTTPS.</summary>
    Public,

    /// <summary>Private repository, cloned over SSH with a deploy key the panel generated.</summary>
    DeployKey,
}

/// <summary>A GitHub repository checked out on this server for one app user (several apps may build from it).</summary>
public sealed class GitRepository
{
    public int Id { get; set; }

    /// <summary>Slug; also the checkout directory ~/repos/&lt;Name&gt; and the deploy key name.</summary>
    public required string Name { get; set; }

    public required string SystemUser { get; set; }

    public required string Owner { get; set; }

    public required string RepositoryName { get; set; }

    public string Branch { get; set; } = "main";

    public RepositoryAuth Auth { get; set; }

    public string? PublicKey { get; set; }

    public string? KeyFingerprint { get; set; }

    public bool Submodules { get; set; }

    /// <summary>Private submodules, each with its own deploy key (GitHub deploy keys cover one repository).</summary>
    public List<SubmoduleKey> SubmoduleKeys { get; set; } = [];

    public required string WebhookSecretProtected { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public string? LastCommit { get; set; }

    public string? LastCommitMessage { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string FullName => $"{Owner}/{RepositoryName}";
}

public sealed class SubmoduleKey
{
    public required string Owner { get; set; }

    public required string Name { get; set; }

    public required string KeyName { get; set; }

    public string? PublicKey { get; set; }

    public string? Fingerprint { get; set; }
}

public sealed class AppSharedPath
{
    public required string Path { get; set; }

    public bool Directory { get; set; }
}

/// <summary>Something deployed from a repository: static files for a site, a long-running service, or both.</summary>
public sealed class App
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string SystemUser { get; set; }

    public int RepositoryId { get; set; }

    public GitRepository? Repository { get; set; }

    /// <summary>Where the build runs and the service starts, relative to the repository root ("" = root).</summary>
    public string WorkingDirectory { get; set; } = "";

    public string BuildScript { get; set; } = "";

    /// <summary>Toolchains on PATH for build and service: "dotnet", "node".</summary>
    public List<string> Runtimes { get; set; } = [];

    public List<AppSharedPath> SharedPaths { get; set; } = [];

    /// <summary>Environment variables (JSON object), protected with Data Protection.</summary>
    public string EnvironmentProtected { get; set; } = "";

    /// <summary>Built files a site serves, relative to the release root (e.g. "dist"); null if none.</summary>
    public string? StaticOutput { get; set; }

    /// <summary>Command for the systemd service (runs as the app user in current/WorkingDirectory); null if none.</summary>
    public string? ServiceCommand { get; set; }

    /// <summary>Port the service listens on (127.0.0.1), used for the health check.</summary>
    public int? ServicePort { get; set; }

    /// <summary>HTTP path checked after a deploy (e.g. "/health"); with only a port, a TCP connect is checked.</summary>
    public string? HealthPath { get; set; }

    public int MemoryMaxMb { get; set; }

    public int StopTimeoutSeconds { get; set; } = 30;

    /// <summary>The service reads commands from a console the panel can type into (stdin).</summary>
    public bool Console { get; set; }

    public int KeepReleases { get; set; } = 5;

    public bool AutoDeploy { get; set; }

    /// <summary>Only auto-deploy when a push touches one of these paths (empty = any change).</summary>
    public List<string> DeployPaths { get; set; } = [];

    /// <summary>Purge Cloudflare's cache for sites serving this app after a deploy.</summary>
    public bool PurgeCloudflare { get; set; } = true;

    public string? CurrentRelease { get; set; }

    /// <summary>Where the service starts, relative to the release root (null = the working directory).</summary>
    public string? ServiceDirectory { get; set; }

    /// <summary>The recipe the app was created from (null for a hand-made app).</summary>
    public string? RecipeId { get; set; }

    /// <summary>The recipe inputs it was created with.</summary>
    public Dictionary<string, string> RecipeInputs { get; set; } = [];

    /// <summary>Generated secrets, database credentials and the last rendered environment (encrypted JSON; see RecipeState).</summary>
    public string? RecipeStateProtected { get; set; }

    public int? StackId { get; set; }

    public string? StackRole { get; set; }

    /// <summary>Ports reserved for the app on 127.0.0.1, by the recipe's name for them.</summary>
    public Dictionary<string, int> Ports { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string AppDirectory => $"/home/{SystemUser}/apps/{Name}";
}

public enum ReleaseStatus
{
    Building,
    Active,
    Inactive,
    Failed,
}

public sealed class AppRelease
{
    public int Id { get; set; }

    public int AppId { get; set; }

    /// <summary>Directory name: yyyyMMddHHmmss-&lt;commit&gt;.</summary>
    public required string Name { get; set; }

    public string? Commit { get; set; }

    public string? CommitMessage { get; set; }

    public string? CommitAuthor { get; set; }

    public ReleaseStatus Status { get; set; } = ReleaseStatus.Building;

    /// <summary>manual | webhook | rollback</summary>
    public required string Trigger { get; set; }

    public string? TriggeredBy { get; set; }

    public long? JobId { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Error { get; set; }
}

/// <summary>Every webhook GitHub sent, by its delivery id (also blocks replays).</summary>
public sealed class WebhookDelivery
{
    public required string Id { get; set; }

    public int RepositoryId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public required string Event { get; set; }

    public string? Ref { get; set; }

    public required string Result { get; set; }
}
