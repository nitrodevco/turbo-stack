using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;

namespace TurboManager.Agent.Nginx;

/// <summary>Writes panel-managed nginx configs and certificates with test-and-rollback.</summary>
public sealed partial class NginxManager(ICommandRunner commands, IOptions<AgentOptions> options, ILogger<NginxManager> logger)
{
    public const string AcmeWebRoot = "/var/lib/turbo-stack-acme";

    public const string CertificateRoot = "/etc/turbo-stack/certs";

    private static readonly string[] AllowedWebRoots = ["/var/www/", "/srv/"];

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string HostPath(string absolutePath) => Path.Combine(options.Value.HostRoot, absolutePath.TrimStart('/'));

    public async Task<NginxStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        CommandResult version;

        try
        {
            version = await commands.RunAsync("nginx", ["-v"], cancellationToken: cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new NginxStatus { Installed = false };
        }

        var active = await commands.RunAsync("systemctl", ["is-active", "nginx"], cancellationToken: cancellationToken);
        var test = await commands.RunAsync("nginx", ["-t"], cancellationToken: cancellationToken);

        return new NginxStatus
        {
            Installed = true,
            Version = (version.StdErr + version.StdOut).Trim().Replace("nginx version: ", "", StringComparison.Ordinal),
            Running = active.StdOut.Trim() == "active",
            ConfigOk = test.Success,
            TestOutput = test.StdErr.Trim(),
        };
    }

    public async Task<NginxApplyResult> ApplyAsync(NginxConfig config, CancellationToken cancellationToken)
    {
        var (path, link) = Paths(config.Name, config.Kind);
        var violations = NginxConfigPolicy.Check(config.Content);

        if (violations.Count > 0)
        {
            return new NginxApplyResult { Applied = false, Error = string.Join('\n', violations) };
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var previous = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
            var hadLink = link is not null && File.Exists(link);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteAtomicallyAsync(path, config.Content, cancellationToken);

            if (link is not null && !hadLink)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                EnableLink(link, path);
            }

            var test = await commands.RunAsync("nginx", ["-t"], cancellationToken: cancellationToken);

            if (!test.Success)
            {
                await RestoreAsync(path, previous, link, hadLink, cancellationToken);
                logger.LogWarning("nginx rejected config {Name}; previous version restored", config.Name);

                return new NginxApplyResult { Applied = false, Error = CleanTestOutput(test.ErrorText) };
            }

            await ReloadAsync(cancellationToken);

            return new NginxApplyResult { Applied = true };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NginxApplyResult> RemoveAsync(NginxConfigRef reference, CancellationToken cancellationToken)
    {
        var (path, link) = Paths(reference.Name, reference.Kind);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(path))
            {
                return new NginxApplyResult { Applied = true };
            }

            var previous = await File.ReadAllTextAsync(path, cancellationToken);
            var hadLink = link is not null && File.Exists(link);

            if (hadLink)
            {
                File.Delete(link!);
            }

            File.Delete(path);

            var test = await commands.RunAsync("nginx", ["-t"], cancellationToken: cancellationToken);

            if (!test.Success)
            {
                await RestoreAsync(path, previous, link, hadLink, cancellationToken);

                return new NginxApplyResult { Applied = false, Error = CleanTestOutput(test.ErrorText) };
            }

            await ReloadAsync(cancellationToken);

            return new NginxApplyResult { Applied = true };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the installer's https://&lt;ip&gt;:8443 entry point; ts-panel-recover reopens it.</summary>
    public async Task<NginxApplyResult> CloseSetupAddressAsync(CancellationToken cancellationToken)
    {
        var result = await RemoveAsync(new NginxConfigRef { Name = "setup", Kind = NginxConfigKind.Site }, cancellationToken);

        if (result.Applied)
        {
            var marker = HostPath($"/etc/{Branding.Slug}/setup-closed");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, $"Closed from the panel at {DateTimeOffset.UtcNow:u}. Reopen with: sudo ts-panel-recover\n", cancellationToken);
        }

        return result;
    }

    public NginxConfigs List()
    {
        var reply = new NginxConfigs();

        foreach (var (kind, directory) in new[] { (NginxConfigKind.Site, "/etc/nginx/sites-available"), (NginxConfigKind.Global, "/etc/nginx/conf.d") })
        {
            var host = HostPath(directory);

            if (!Directory.Exists(host))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(host, "ts-*.conf").Order(StringComparer.Ordinal))
            {
                reply.Configs.Add(new NginxConfig
                {
                    Name = Path.GetFileNameWithoutExtension(file)["ts-".Length..],
                    Kind = kind,
                    Content = File.ReadAllText(file),
                });
            }
        }

        return reply;
    }

    public async Task<CertificateInfo> InstallCertificateAsync(InstallCertificateRequest request, CancellationToken cancellationToken)
    {
        var name = CertificateName(request.Name);
        X509Certificate2 certificate;

        try
        {
            // Throws if the key doesn't belong to the certificate.
            certificate = X509Certificate2.CreateFromPem(request.FullchainPem, request.PrivateKeyPem);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw Validate.Invalid($"The certificate or key is invalid, or they don't match: {e.Message}");
        }

        var directory = HostPath($"{CertificateRoot}/{name}");
        Directory.CreateDirectory(directory);
        SetMode(HostPath(CertificateRoot), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SetMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await WriteAtomicallyAsync(Path.Combine(directory, "privkey.pem"), request.PrivateKeyPem, cancellationToken,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await WriteAtomicallyAsync(Path.Combine(directory, "fullchain.pem"), request.FullchainPem, cancellationToken,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        logger.LogInformation("Installed certificate {Name} valid until {NotAfter:u}", name, certificate.NotAfter);

        return Describe(name, certificate);
    }

    public void RemoveCertificate(string name)
    {
        var directory = HostPath($"{CertificateRoot}/{CertificateName(name)}");

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public CertificateList ListCertificates()
    {
        var reply = new CertificateList();
        var root = HostPath(CertificateRoot);

        if (!Directory.Exists(root))
        {
            return reply;
        }

        foreach (var directory in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            var chain = Path.Combine(directory, "fullchain.pem");

            if (!File.Exists(chain))
            {
                continue;
            }

            try
            {
                reply.Certificates.Add(Describe(Path.GetFileName(directory), X509Certificate2.CreateFromPem(File.ReadAllText(chain))));
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                logger.LogWarning(e, "Unreadable certificate in {Directory}", directory);
            }
        }

        return reply;
    }

    public async Task WriteAcmeChallengeAsync(AcmeChallenge challenge, CancellationToken cancellationToken)
    {
        var path = AcmeChallengePath(challenge.Token);

        if (!AcmeKeyAuthorizationPattern().IsMatch(challenge.KeyAuthorization))
        {
            throw Validate.Invalid("Invalid ACME key authorization.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicallyAsync(path, challenge.KeyAuthorization, cancellationToken,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public void RemoveAcmeChallenge(AcmeChallenge challenge)
    {
        var path = AcmeChallengePath(challenge.Token);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public WebRootResult EnsureWebRoot(WebRootRequest request)
    {
        var path = request.Path.TrimEnd('/');

        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains("/.", StringComparison.Ordinal)
            || !AllowedWebRoots.Any(r => path.StartsWith(r, StringComparison.Ordinal) && path.Length > r.Length)
            || !WebRootPattern().IsMatch(path))
        {
            throw Validate.Invalid($"Web roots must be a directory under {string.Join(" or ", AllowedWebRoots)}.");
        }

        var host = HostPath(path);
        var created = !Directory.Exists(host);
        Directory.CreateDirectory(host);

        if (request.CreatePlaceholder && !Directory.EnumerateFileSystemEntries(host).Any())
        {
            File.WriteAllText(Path.Combine(host, "index.html"),
                $"<!doctype html><meta charset=\"utf-8\"><title>It works</title><p style=\"font-family:system-ui;margin:3rem\">This site is set up with {Branding.ProductName}. Deploy something here to replace this page.</p>\n");
        }

        return new WebRootResult { Path = path, Created = created };
    }

    private (string Path, string? Link) Paths(string name, NginxConfigKind kind)
    {
        if (!ConfigNamePattern().IsMatch(name))
        {
            throw Validate.Invalid($"Invalid config name: {name}");
        }

        return kind switch
        {
            NginxConfigKind.Site => (HostPath($"/etc/nginx/sites-available/ts-{name}.conf"), HostPath($"/etc/nginx/sites-enabled/ts-{name}.conf")),
            NginxConfigKind.Global => (HostPath($"/etc/nginx/conf.d/ts-{name}.conf"), null),
            _ => throw Validate.Invalid("Unknown config kind."),
        };
    }

    private string AcmeChallengePath(string token) =>
        AcmeTokenPattern().IsMatch(token)
            ? HostPath($"{AcmeWebRoot}/.well-known/acme-challenge/{token}")
            : throw Validate.Invalid("Invalid ACME token.");

    private static string CertificateName(string name) =>
        CertificateNamePattern().IsMatch(name) ? name : throw Validate.Invalid($"Invalid certificate name: {name}");

    private static CertificateInfo Describe(string name, X509Certificate2 certificate)
    {
        var info = new CertificateInfo
        {
            Name = name,
            NotBeforeUnixMs = new DateTimeOffset(certificate.NotBefore.ToUniversalTime()).ToUnixTimeMilliseconds(),
            NotAfterUnixMs = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).ToUnixTimeMilliseconds(),
            Issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            FullchainPath = $"{CertificateRoot}/{name}/fullchain.pem",
            PrivateKeyPath = $"{CertificateRoot}/{name}/privkey.pem",
        };

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        info.Domains.AddRange(san?.EnumerateDnsNames() ?? [certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false)]);

        return info;
    }

    private async Task RestoreAsync(string path, string? previous, string? link, bool hadLink, CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            File.Delete(path);

            if (link is not null && File.Exists(link) && !hadLink)
            {
                File.Delete(link);
            }

            return;
        }

        await WriteAtomicallyAsync(path, previous, cancellationToken);

        if (link is not null && hadLink && !File.Exists(link))
        {
            EnableLink(link, path);
        }
    }

    /// <summary>sites-enabled entries are symlinks; Windows (dev/tests only) needs privileges for those, so copy there.</summary>
    private static void EnableLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Copy(target, link, overwrite: true);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }
    }

    /// <summary>
    /// Reloads nginx and waits until it has replaced its worker processes. `nginx -s reload` (what
    /// systemd runs) returns before the new workers exist, so a request right after it could still
    /// see the old config.
    /// </summary>
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var before = WorkerPids();

        await commands.RunCheckedAsync("systemctl", ["reload-or-restart", "nginx"], cancellationToken: cancellationToken);

        if (before.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var current = WorkerPids();

            // New workers exist, and every old one has stopped accepting connections: it exited, or got the
            // master's signal and closed its listening sockets (it then lingers only to finish open requests).
            if (current.Except(before).Any() && before.All(IsGoneOrShuttingDown))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        logger.LogWarning("nginx workers were not replaced within 5 seconds after a reload");
    }

    /// <summary>nginx retitles a worker "nginx: worker process is shutting down" once it stops accepting.</summary>
    private bool IsGoneOrShuttingDown(int pid)
    {
        try
        {
            var path = HostPath($"/proc/{pid}/cmdline");

            return !File.Exists(path) || File.ReadAllText(path).Contains("shutting down", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return true; // exited while we looked
        }
    }

    /// <summary>Child processes of the nginx master (from /run/nginx.pid); empty if unknown.</summary>
    private HashSet<int> WorkerPids()
    {
        try
        {
            var master = File.ReadAllText(HostPath("/run/nginx.pid")).Trim();
            var children = HostPath($"/proc/{master}/task/{master}/children");

            return File.Exists(children)
                ? File.ReadAllText(children).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return [];
        }
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken, UnixFileMode? mode = null)
    {
        var temp = path + ".ts-tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken);
        SetMode(temp, mode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        File.Move(temp, path, overwrite: true);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    /// <summary>nginx -t prints "nginx: [emerg] ..." lines; keep the useful ones.</summary>
    private static string CleanTestOutput(string output) =>
        string.Join('\n', output.Split('\n').Where(l => !l.Contains("test failed", StringComparison.Ordinal)).Select(l => l.Replace("nginx: ", "", StringComparison.Ordinal).Trim()))
            .Trim();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex ConfigNamePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,100}$")]
    private static partial Regex CertificateNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{10,200}$")]
    private static partial Regex AcmeTokenPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,200}\.[A-Za-z0-9_-]{20,100}$")]
    private static partial Regex AcmeKeyAuthorizationPattern();

    [GeneratedRegex("^/[a-z0-9/_.-]{1,200}$")]
    private static partial Regex WebRootPattern();
}
