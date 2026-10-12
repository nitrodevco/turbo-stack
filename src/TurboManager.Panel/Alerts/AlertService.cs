using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Core;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Data;
using TurboManager.Panel.Updates;

namespace TurboManager.Panel.Alerts;

public sealed record AlertCondition(string Key, AlertSeverity Severity, string Title, string? Detail);

/// <summary>
/// Watches the server and tells people when something needs them: thresholds (disk, memory), failed or crash-looping
/// services, certificates, security updates, and failed jobs (deploys, installs, backups, certificates). A condition
/// is announced when it starts and again when it clears; nothing is repeated while it lasts.
/// </summary>
public sealed class AlertService(
    PanelDbContext db,
    AgentChannelFactory agent,
    IHttpClientFactory httpClients,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    AlertState state,
    ILogger<AlertService> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.Alerts.v1");



    private static readonly string[] WatchedUnits = ["nginx.service", "mysql.service", "mariadb.service", "fail2ban.service", "ssh.service"];

    private static readonly string[] IgnoredFileSystems = ["tmpfs", "devtmpfs", "squashfs", "efivarfs", "ramfs"];

    private static readonly string[] AlertedJobKinds = ["deploy-app-", "stack-", "databases", "backups", "certificates", "runtimes"];

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string value) => _protector.Unprotect(value);

    public async Task<AlertSettings> GetSettingsAsync()
    {
        var settings = await db.AlertSettings.FindAsync(1);

        if (settings is null)
        {
            settings = new AlertSettings();
            db.AlertSettings.Add(settings);
            await db.SaveChangesAsync();
        }

        return settings;
    }

    /// <summary>One check: fires new conditions, resolves cleared ones, records failed jobs, and notifies about each change.</summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        // The minute timer and "check now" must not both see the same new failure.
        await state.Gate.WaitAsync(cancellationToken);

        try
        {
            await EvaluateLockedAsync(cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task EvaluateLockedAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync();
        var now = time.GetUtcNow();
        var conditions = new Dictionary<string, AlertCondition>();

        foreach (var condition in await CheckAsync(settings, now, cancellationToken))
        {
            conditions[condition.Key] = condition;
        }

        var open = await db.AlertEvents.Where(e => e.ResolvedAt == null && !e.OneOff).ToListAsync(cancellationToken);
        var changes = new List<(AlertEvent Event, bool Firing)>();

        foreach (var condition in conditions.Values)
        {
            var existing = open.FirstOrDefault(e => e.Key == condition.Key);

            if (existing is not null && existing.Severity == condition.Severity)
            {
                continue;
            }

            if (existing is not null)
            {
                existing.ResolvedAt = now; // it got worse (or better): announce the new state
            }

            var alert = new AlertEvent { Key = condition.Key, Severity = condition.Severity, Title = condition.Title, Detail = condition.Detail, FiredAt = now };
            db.AlertEvents.Add(alert);
            changes.Add((alert, true));
        }

        foreach (var cleared in open.Where(e => !conditions.ContainsKey(e.Key)))
        {
            cleared.ResolvedAt = now;
            changes.Add((cleared, false));
        }

        foreach (var failed in await FailedJobsAsync(settings, now, cancellationToken))
        {
            db.AlertEvents.Add(failed);
            changes.Add((failed, true));
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var (alert, firing) in changes)
        {
            await NotifyAsync(alert, firing, cancellationToken);
        }

        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<List<AlertCondition>> CheckAsync(AlertSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var conditions = new List<AlertCondition>();

        try
        {
            var metrics = await agent.Metrics.GetMetricsAsync(new MetricsRequest(), cancellationToken: cancellationToken);

            foreach (var disk in metrics.Disks.Where(d => d.TotalBytes > 0 && !IgnoredFileSystems.Contains(d.FsType)))
            {
                var used = 100.0 * (disk.TotalBytes - disk.FreeBytes) / disk.TotalBytes;

                if (used >= settings.DiskPercent)
                {
                    conditions.Add(new AlertCondition($"disk:{disk.Mount}", used >= 97 ? AlertSeverity.Critical : AlertSeverity.Warning,
                        $"Disk {disk.Mount} is {used:0}% full", $"{Gb(disk.FreeBytes)} free of {Gb(disk.TotalBytes)}."));
                }
            }

            if (metrics.MemoryTotalBytes > 0)
            {
                var used = 100.0 * (metrics.MemoryTotalBytes - metrics.MemoryAvailableBytes) / metrics.MemoryTotalBytes;

                if (used >= settings.MemoryPercent)
                {
                    conditions.Add(new AlertCondition("memory", AlertSeverity.Warning, $"Memory is {used:0}% used",
                        $"{Gb(metrics.MemoryAvailableBytes)} available of {Gb(metrics.MemoryTotalBytes)}."));
                }
            }

            var services = await agent.Services.ListServicesAsync(new ListServicesRequest(), cancellationToken: cancellationToken);

            foreach (var unit in services.Services.Where(u => u.Name.StartsWith("tm-app-", StringComparison.Ordinal) || WatchedUnits.Contains(u.Name)
                         || u.Name.StartsWith("postgresql", StringComparison.Ordinal)))
            {
                if (unit.ActiveState == "failed")
                {
                    conditions.Add(new AlertCondition($"unit:{unit.Name}", AlertSeverity.Critical, $"{unit.Name} has failed",
                        "It stopped and systemd gave up restarting it. Its log on the Services page says why."));
                }
                else if (state.IsCrashLooping(unit, now))
                {
                    conditions.Add(new AlertCondition($"unit:{unit.Name}", AlertSeverity.Critical, $"{unit.Name} keeps crashing",
                        $"It has restarted {unit.Restarts} times; systemd keeps starting it again."));
                }
            }
        }
        catch (RpcException e)
        {
            conditions.Add(new AlertCondition("agent", AlertSeverity.Critical, "The panel can't reach its agent", e.Status.Detail));
        }

        foreach (var certificate in await db.Certificates.ToListAsync(cancellationToken))
        {
            var name = string.Join(", ", certificate.Domains);

            if (certificate.Status == CertificateStatus.Failed)
            {
                conditions.Add(new AlertCondition($"certificate:{certificate.Id}", AlertSeverity.Critical, $"Certificate for {name} failed", certificate.LastError));
            }
            else if (certificate.Status == CertificateStatus.Valid && certificate.NotAfter is { } notAfter && notAfter - now < TimeSpan.FromDays(settings.CertificateDays))
            {
                conditions.Add(new AlertCondition($"certificate:{certificate.Id}", notAfter < now ? AlertSeverity.Critical : AlertSeverity.Warning,
                    notAfter < now ? $"Certificate for {name} has expired" : $"Certificate for {name} expires in {(int)Math.Ceiling((notAfter - now).TotalDays)} days",
                    certificate.AutoRenew ? "Automatic renewal hasn't managed it yet; check the Certificates page." : "It doesn't renew automatically."));
            }
        }

        if (settings.SecurityUpdates)
        {
            try
            {
                var upgradable = await agent.Packages.ListUpgradableAsync(new ListUpgradableRequest(), cancellationToken: cancellationToken);
                var security = upgradable.Packages.Count(p => p.Security);

                if (security > 0)
                {
                    conditions.Add(new AlertCondition("updates:security", AlertSeverity.Warning, $"{security} security update{(security == 1 ? "" : "s")} waiting",
                        "Install them on the Updates page."));
                }
            }
            catch (RpcException)
            {
                // Package lists are best effort (apt may be busy).
            }
        }

        if (await db.PanelUpdates.FindAsync([1], cancellationToken) is { } update)
        {
            var current = PanelUpdateService.CurrentVersion;

            if (update.LastState is "rolled-back" or "failed" && update.LastTo is { } failedTo && ReleaseVersion.Compare(current, failedTo) < 0)
            {
                conditions.Add(new AlertCondition("panel:update-failed", AlertSeverity.Critical, $"The update to {Branding.ProductName} {failedTo} didn't work",
                    update.LastMessage));
            }
            else if (update.LatestVersion is { Length: > 0 } latest && ReleaseVersion.Compare(latest, current) > 0)
            {
                conditions.Add(new AlertCondition("panel:update", AlertSeverity.Warning, $"{Branding.ProductName} {latest} is available",
                    $"{current} is installed. Update on the Updates page."));
            }
        }

        return conditions;
    }


    private async Task<List<AlertEvent>> FailedJobsAsync(AlertSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The first check starts from now: no alerts about old failures.
        var since = settings.LastJobScanAt ?? now;
        settings.LastJobScanAt = now;

        var failed = await db.Jobs.Where(j => j.Status == JobStatus.Failed && j.FinishedAt > since && j.FinishedAt <= now).ToListAsync(cancellationToken);

        return failed.Where(j => AlertedJobKinds.Any(k => j.Kind.StartsWith(k, StringComparison.Ordinal))).Select(j => new AlertEvent
        {
            Key = $"job:{j.Id}",
            Severity = AlertSeverity.Critical,
            Title = $"Failed: {j.Title}",
            Detail = string.Join('\n', (j.Output ?? "").Split('\n').TakeLast(12)).Trim(),
            FiredAt = j.FinishedAt ?? now,
            ResolvedAt = now,
            OneOff = true,
        }).ToList();
    }

    // ----- Sending -----

    private async Task NotifyAsync(AlertEvent alert, bool firing, CancellationToken cancellationToken)
    {
        foreach (var channel in await db.AlertChannels.Where(c => c.Enabled).ToListAsync(cancellationToken))
        {
            if (channel.CriticalOnly && alert.Severity != AlertSeverity.Critical)
            {
                continue;
            }

            channel.LastError = await SendAsync(channel, alert, firing, cancellationToken);
        }
    }

    /// <summary>Sends one alert to one channel; returns the error, or null if it went out.</summary>
    public async Task<string?> SendAsync(AlertChannel channel, AlertEvent alert, bool firing, CancellationToken cancellationToken)
    {
        var server = Environment.MachineName;
        var title = firing ? alert.Title : $"Resolved: {alert.Title}";

        try
        {
            var target = Unprotect(channel.TargetProtected);

            switch (channel.Kind)
            {
                case AlertChannelKind.Discord:
                    var color = !firing ? 0x2E7D32 : alert.Severity == AlertSeverity.Critical ? 0xC62828 : 0xEF6C00;
                    using (var response = await httpClients.CreateClient("alerts").PostAsJsonAsync(target, new
                    {
                        username = Branding.ProductName,
                        embeds = new[]
                        {
                            new
                            {
                                title = $"{(firing ? alert.Severity == AlertSeverity.Critical ? "🔴" : "🟠" : "✅")} {title}",
                                description = Truncate(alert.Detail, 3500),
                                color,
                                footer = new { text = server },
                                timestamp = (firing ? alert.FiredAt : alert.ResolvedAt ?? alert.FiredAt).ToString("O", CultureInfo.InvariantCulture),
                            },
                        },
                    }, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    break;
                case AlertChannelKind.Webhook:
                    using (var response = await httpClients.CreateClient("alerts").PostAsJsonAsync(target, new
                    {
                        status = firing ? "firing" : "resolved",
                        key = alert.Key,
                        severity = alert.Severity.ToString().ToLowerInvariant(),
                        title = alert.Title,
                        detail = alert.Detail,
                        firedAt = alert.FiredAt,
                        resolvedAt = alert.ResolvedAt,
                        server,
                    }, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    break;
                case AlertChannelKind.Email:
                    await SendMailAsync(target, $"[{server}] {title}", $"{title}\n\n{alert.Detail}\n\n— {Branding.ProductName} on {server}", cancellationToken);
                    break;
            }

            return null;
        }
        catch (Exception e) when (e is HttpRequestException or SmtpException or InvalidOperationException or TaskCanceledException
                                      or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning("Alert to {Channel} failed: {Error}", channel.Name, e.Message);

            return e.Message;
        }
    }

    private async Task SendMailAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.SmtpFrom))
        {
            throw new InvalidOperationException("Set up the mail server (SMTP) first.");
        }

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort) { EnableSsl = settings.SmtpPort != 25 };

        if (!string.IsNullOrEmpty(settings.SmtpUser))
        {
            client.Credentials = new NetworkCredential(settings.SmtpUser,
                settings.SmtpPasswordProtected is null ? "" : Unprotect(settings.SmtpPasswordProtected));
        }

        using var message = new MailMessage(settings.SmtpFrom, to, subject, body);
        await client.SendMailAsync(message, cancellationToken);
    }

    private static string Gb(long bytes) => $"{bytes / 1024d / 1024 / 1024:0.0} GB";

    private static string? Truncate(string? text, int length) => text is null || text.Length <= length ? text : text[..length] + "…";
}

/// <summary>Runs the alert checks every minute.</summary>
public sealed class AlertMonitor(IServiceScopeFactory scopes, TimeProvider time, ILogger<AlertMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AlertService>().EvaluateAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Alert checks failed");
            }
        }
    }
}

/// <summary>What the checks remember between runs (one per panel process): restart counts, to notice crash loops.</summary>
public sealed class AlertState
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    private readonly ConcurrentDictionary<string, List<(DateTimeOffset At, int Restarts)>> _restarts = new();

    /// <summary>systemd is about to restart it, or it restarted three times within ten minutes.</summary>
    public bool IsCrashLooping(ServiceUnit unit, DateTimeOffset now)
    {
        var history = _restarts.GetOrAdd(unit.Name, _ => []);

        lock (history)
        {
            history.RemoveAll(h => now - h.At > TimeSpan.FromMinutes(10));
            history.Add((now, unit.Restarts));

            return unit.SubState == "auto-restart" || unit.Restarts - history[0].Restarts >= 3;
        }
    }
}
