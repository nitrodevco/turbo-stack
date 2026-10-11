using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OtpNet;
using TurboManager.Panel.Http;
using TurboManager.Panel.Setup;

namespace TurboManager.Panel.Tests;

/// <summary>A panel instance with its own temporary data directory and a controllable clock.</summary>
public sealed class PanelFactory : WebApplicationFactory<PanelEntryPoint>
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "tm-panel-" + Guid.NewGuid().ToString("N"));

    // Starts at the real clock because setup-token expiry compares against the token file's mtime.
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public string TokenFile => Path.Combine(_dataDirectory, "setup-token.sha256");

    /// <summary>Where the panel looks for the agent; start a <see cref="FakeAgent"/> here when a test needs one.</summary>
    public string AgentSocket => Path.Combine(_dataDirectory, "agent.sock");

    public string DataDirectory => _dataDirectory;

    /// <summary>Extra configuration (e.g. Panel:CloudflareApiBase) applied before the host starts.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseEnvironment("Testing");
        builder.UseSetting("Panel:DataDirectory", _dataDirectory);
        builder.UseSetting("Panel:SetupTokenHashFile", TokenFile);
        builder.UseSetting("Panel:AgentSocket", AgentSocket);
        builder.UseSetting("Panel:AuthLogFile", Path.Combine(_dataDirectory, "auth.log"));
        builder.UseSetting("Panel:SecureCookies", "false");
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }

    /// <summary>Writes a setup token hash the way install.sh does and returns the plain token.</summary>
    public string IssueSetupToken()
    {
        Directory.CreateDirectory(_dataDirectory);
        const string token = "test-setup-token-0123456789abcdef";
        File.WriteAllText(TokenFile, SetupTokenStore.Hash(token) + "\n");

        return token;
    }

    public HttpClient CreateApiClient(string baseAddress = "http://localhost")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri(baseAddress) });
        client.DefaultRequestHeaders.Add(SecurityMiddleware.CsrfHeader, "1");

        return client;
    }

    public string CurrentTotp(string base32Secret) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp(Time.GetUtcNow().UtcDateTime);

    /// <summary>Runs the whole setup wizard and returns the owner's TOTP secret and recovery codes.</summary>
    public async Task<(string Secret, string[] RecoveryCodes)> CompleteSetupAsync(HttpClient client, string username = "owner",
        string password = "correct horse battery staple")
    {
        var token = IssueSetupToken();
        (await client.PostAsJsonAsync("/api/setup/claim", new { token })).EnsureSuccessStatusCode();

        var owner = await (await client.PostAsJsonAsync("/api/setup/owner", new { username, password }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<OwnerResponse>();

        var confirm = await (await client.PostAsJsonAsync("/api/setup/owner/confirm", new { code = CurrentTotp(owner!.Secret) }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ConfirmResponse>();

        return (owner.Secret, confirm!.RecoveryCodes);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(_dataDirectory))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    public sealed record OwnerResponse(string Secret, string OtpauthUri);

    public sealed record ConfirmResponse(string[] RecoveryCodes);
}
