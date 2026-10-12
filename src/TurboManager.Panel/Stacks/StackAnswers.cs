using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboManager.Core;
using TurboManager.Panel.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TurboManager.Panel.Stacks;

/// <summary>
/// A hotel's answers as a file (hotel.yaml): downloaded from a hotel to rebuild it elsewhere, or given to
/// <c>install.sh --config</c> for a headless install. Contains no secrets (those are generated on the server).
/// </summary>
public sealed class StackAnswers
{
    public string Name { get; set; } = "hotel";

    public string Preset { get; set; } = "turbo-nitro";

    public string? SystemUser { get; set; }

    public string Domain { get; set; } = "";

    public TlsMode TlsMode { get; set; } = TlsMode.LetsEncrypt;

    public bool CloudflareDns { get; set; }

    public Dictionary<string, string> Hosts { get; set; } = [];

    public Dictionary<string, string> Settings { get; set; } = [];

    public Dictionary<string, string> Overrides { get; set; } = [];

    public List<StackSource> Sources { get; set; } = [];

    public string? DatabaseEngine { get; set; }

    private static readonly IDeserializer Reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    private static readonly ISerializer Writer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections).Build();

    public static StackAnswers Parse(string yaml) => Reader.Deserialize<StackAnswers>(yaml) ?? new StackAnswers();

    public string ToYaml() => $"# {Branding.ProductName} hotel answers. Install with: install.sh --config hotel.yaml\n" + Writer.Serialize(this);

    public StackRequest ToRequest() =>
        new(Name, Preset, SystemUser, Domain, TlsMode, CloudflareDns, Hosts, Settings, Overrides, Sources, DatabaseEngine);

    public static StackAnswers From(Stack stack) => new()
    {
        Name = stack.Name,
        Preset = stack.Preset,
        SystemUser = stack.SystemUser,
        Domain = stack.Domain,
        TlsMode = stack.TlsMode,
        CloudflareDns = stack.CloudflareDns,
        Hosts = stack.Hosts,
        Settings = stack.Settings,
        Overrides = stack.Overrides,
        Sources = stack.Sources,
        DatabaseEngine = stack.DatabaseEngine,
    };
}

/// <summary>
/// Headless installs: when the installer was given <c>--config hotel.yaml</c> and no hotel exists yet, creates it
/// and starts its install at start-up. The panel still has to be claimed through the setup link afterwards.
/// </summary>
public sealed class UnattendedHotelService(IServiceScopeFactory scopes, IOptions<PanelOptions> options, ILogger<UnattendedHotelService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = options.Value.UnattendedConfig;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();

        if (await db.Stacks.AnyAsync(stoppingToken))
        {
            return; // already done (the file stays for reference)
        }

        try
        {
            var answers = StackAnswers.Parse(await File.ReadAllTextAsync(path, stoppingToken));
            var stacks = scope.ServiceProvider.GetRequiredService<StackService>();
            var stack = await stacks.CreateAsync(answers.ToRequest(), actor: null, stoppingToken);
            var job = await stacks.StartInstallAsync(stack, actor: null);
            logger.LogInformation("Unattended install: hotel {Name} created from {Path}; installing (job {Job})", stack.Name, path, job.Id);
        }
        catch (Exception e) when (e is StackException or YamlDotNet.Core.YamlException or IOException)
        {
            logger.LogError("Unattended install from {Path} failed: {Error}", path, e.Message);
        }
    }
}
