using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Contracts.Agent;
using TurboManager.Core;

namespace TurboManager.Agent.Update;

/// <summary>A channel file from the install domain (channels/&lt;channel&gt;.json), as written by scripts/package.sh.</summary>
public sealed record ReleaseChannel(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("artifacts")] Dictionary<string, ReleaseArtifact> Artifacts,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("published")] string? Published = null);

public sealed record ReleaseArtifact([property: JsonPropertyName("url")] string Url, [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// Finds, verifies and starts panel updates. Trust comes only from the release key written at install time: the channel
/// file must carry a valid signature, and the bundle must match the checksum inside it.
/// </summary>
public sealed partial class UpdateManager(ICommandRunner commands, IHttpClientFactory httpClients, IOptions<AgentOptions> options,
    ILogger<UpdateManager> logger)
{
    public const string HttpClientName = "updates";

    public const string Unit = $"{Branding.ShortName}-update";

    public static readonly string[] Channels = ["stable", "beta"];

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private string HostPath(string path) => Path.Combine(options.Value.HostRoot, path.TrimStart('/'));

    private string InstallEnvPath => HostPath($"/etc/{Branding.Slug}/install.env");

    private string ReleaseKeyPath => HostPath($"/etc/{Branding.Slug}/release-key.pem");

    private string VersionPath => HostPath($"{ManagerPaths.InstallRoot}/VERSION");

    private string StatusPath => HostPath($"{options.Value.UpdateRoot}/status.json");

    public string CurrentVersion => File.Exists(VersionPath) ? File.ReadAllText(VersionPath).Trim() : "";

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken)
    {
        var info = new UpdateInfo { CurrentVersion = CurrentVersion, Channel = ReadInstallEnv().GetValueOrDefault("TS_CHANNEL", "stable") };

        try
        {
            var release = await FetchChannelAsync(cancellationToken);
            info.LatestVersion = release.Version;
            info.Notes = release.Notes ?? "";
            info.Published = release.Published ?? "";
            info.Available = info.CurrentVersion.Length > 0 && ReleaseVersion.Compare(release.Version, info.CurrentVersion) > 0;
        }
        catch (RpcException e)
        {
            info.Error = e.Status.Detail;
        }

        return info;
    }

    public async Task StartAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            if (await IsRunningAsync(cancellationToken))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "An update is already running."));
            }

            var release = await FetchChannelAsync(cancellationToken);
            var current = CurrentVersion;

            if (release.Version != request.Version)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"The channel now offers {release.Version}, not {request.Version}. Check for updates again."));
            }

            if (current.Length > 0 && ReleaseVersion.Compare(release.Version, current) <= 0)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{current} is installed; {release.Version} is not newer."));
            }

            var arch = Architecture();

            if (!release.Artifacts.TryGetValue(arch, out var artifact) || !Sha256Pattern().IsMatch(artifact.Sha256))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Release {release.Version} has no {arch} build."));
            }

            // Both paths: the real one for systemd-run, the host-rooted one for our own file access.
            var directory = $"{options.Value.UpdateRoot}/{release.Version}";
            var local = HostPath(directory);

            if (Directory.Exists(local))
            {
                Directory.Delete(local, recursive: true);
            }

            Directory.CreateDirectory(local);
            SetPrivate(HostPath(options.Value.UpdateRoot));

            var bundle = Path.Combine(local, "bundle.tar.gz");
            await DownloadAsync(Absolute(artifact.Url), bundle, artifact.Sha256, cancellationToken);
            await ExtractAsync(bundle, Path.Combine(local, "bundle"), cancellationToken);

            var bundleVersion = File.ReadAllText(Path.Combine(local, "bundle", "VERSION")).Trim();

            if (bundleVersion != release.Version || !File.Exists(Path.Combine(local, "bundle", "bin", "ts-update")))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The {release.Version} bundle is not a {Branding.ProductName} release."));
            }

            // A transient unit outside the agent's own cgroup: it stops and restarts the agent and the panel.
            await commands.RunCheckedAsync("systemd-run", ["--unit", Unit, "--collect", "--no-block", "--quiet",
                "--description", $"{Branding.ProductName} update to {release.Version}", "--property", "TimeoutStopSec=15min",
                "/bin/bash", $"{directory}/bundle/bin/ts-update", "apply", $"{directory}/bundle"], cancellationToken: cancellationToken);
            logger.LogInformation("Update from {From} to {To} started", current, release.Version);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<UpdateStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var status = new UpdateStatus { Running = await IsRunningAsync(cancellationToken) };

        if (!File.Exists(StatusPath))
        {
            return status;
        }

        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StatusPath, cancellationToken));
            string Get(string name) => json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

            status.State = Get("state");
            status.FromVersion = Get("from");
            status.ToVersion = Get("to");
            status.Message = Get("message");
            status.UpdatedAt = Get("at");
        }
        catch (JsonException)
        {
            status.State = "failed";
            status.Message = "The update status file is unreadable.";
        }

        return status;
    }

    public async Task SetChannelAsync(string channel, CancellationToken cancellationToken)
    {
        if (!Channels.Contains(channel))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Channels: {string.Join(", ", Channels)}."));
        }

        var content = File.Exists(InstallEnvPath) ? await File.ReadAllTextAsync(InstallEnvPath, cancellationToken) : "";
        await File.WriteAllTextAsync(InstallEnvPath, SetEnvValue(content, "TS_CHANNEL", channel), cancellationToken);
    }

    /// <summary>Reads and verifies the channel file named in install.env.</summary>
    private async Task<ReleaseChannel> FetchChannelAsync(CancellationToken cancellationToken)
    {
        var env = ReadInstallEnv();
        var channel = env.GetValueOrDefault("TS_CHANNEL", "stable");

        if (!Channels.Contains(channel) || env.GetValueOrDefault("TS_BASE_URL", "") is not { Length: > 0 } baseUrl)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "install.env has no release address or channel."));
        }

        if (!File.Exists(ReleaseKeyPath))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"No release key ({ReleaseKeyPath}): this server was installed from a bundle without one. Reinstall with install.sh to enable updates."));
        }

        var client = httpClients.CreateClient(HttpClientName);
        byte[] json;
        string signature;

        try
        {
            json = await client.GetByteArrayAsync($"{baseUrl.TrimEnd('/')}/channels/{channel}.json", cancellationToken);
            signature = await client.GetStringAsync($"{baseUrl.TrimEnd('/')}/channels/{channel}.json.sig", cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"Could not read the {channel} channel from {baseUrl}: {e.Message}"));
        }

        if (!VerifySignature(json, signature, await File.ReadAllTextAsync(ReleaseKeyPath, cancellationToken)))
        {
            throw new RpcException(new Status(StatusCode.Aborted, $"The {channel} channel's signature doesn't match the release key; nothing was changed."));
        }

        ReleaseChannel? release;

        try
        {
            release = JsonSerializer.Deserialize<ReleaseChannel>(json);
        }
        catch (JsonException)
        {
            release = null;
        }

        if (release is null || release.Channel != channel || !VersionPattern().IsMatch(release.Version ?? "") || release.Artifacts is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The {channel} channel file is not valid."));
        }

        return release;
    }

    private string Absolute(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http")
        {
            return absolute.ToString();
        }

        return $"{ReadInstallEnv()["TS_BASE_URL"].TrimEnd('/')}/{url.TrimStart('/')}";
    }

    private async Task DownloadAsync(string url, string path, string sha256, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(HttpClientName);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(path);
            await source.CopyToAsync(file, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"Downloading {url} failed: {e.Message}"));
        }

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));

        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The downloaded bundle doesn't match the signed checksum; nothing was changed."));
        }
    }

    private static async Task ExtractAsync(string bundle, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        await using var file = File.OpenRead(bundle);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, cancellationToken);
    }

    private async Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
        (await commands.RunAsync("systemctl", ["is-active", "--quiet", $"{Unit}.service"], cancellationToken: cancellationToken)).Success;

    private Dictionary<string, string> ReadInstallEnv() =>
        File.Exists(InstallEnvPath) ? ParseEnv(File.ReadAllText(InstallEnvPath)) : [];

    private static string Architecture() => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    private static void SetPrivate(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>An ECDSA P-256 signature (base64 DER, from <c>openssl dgst -sha256 -sign</c>) over the exact bytes.</summary>
    public static bool VerifySignature(byte[] data, string signatureBase64, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);

            return key.VerifyData(data, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public static Dictionary<string, string> ParseEnv(string content) =>
        content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..]))
            .GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);

    /// <summary>Replaces KEY=… (or appends it), keeping every other line as it was.</summary>
    public static string SetEnvValue(string content, string key, string value)
    {
        var lines = content.Split('\n').ToList();

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var index = lines.FindIndex(l => l.StartsWith($"{key}=", StringComparison.Ordinal));

        if (index >= 0)
        {
            lines[index] = $"{key}={value}";
        }
        else
        {
            lines.Add($"{key}={value}");
        }

        return string.Join('\n', lines) + "\n";
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(-[0-9A-Za-z.]{1,30})?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}
