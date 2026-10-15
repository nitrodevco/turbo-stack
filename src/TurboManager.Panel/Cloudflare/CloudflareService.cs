using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Core;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Data;
using TurboManager.Panel.Firewall;

namespace TurboManager.Panel.Cloudflare;

public sealed record CloudflareZone(string Id, string Name, string Status, string? Plan);

public sealed record PermissionCheck(string Key, string Label, string UsedFor, bool Required, string Status, string? Error);

public sealed record ZoneSettingResult(string Setting, string Label, string? Current, string Recommended, bool Ok, string? Error);

public sealed class CloudflareNotConnectedException() : InvalidOperationException("Cloudflare is not connected. Add an API token on the Cloudflare page first.");

/// <summary>Everything the panel does with Cloudflare: token, DNS, zone settings, cache, edge IP ranges.</summary>
public sealed class CloudflareService(
    PanelDbContext db,
    CloudflareClient cloudflare,
    AgentChannelFactory agent,
    FirewallService firewall,
    AuditLog audit,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    ILogger<CloudflareService> logger)
{
    public const string IpListName = "Cloudflare";

    public const string RealIpConfigName = "cloudflare-realip";

    /// <summary>Zone settings recommended for a hotel: (setting, label, recommended value).</summary>
    public static readonly (string Setting, string Label, object Value)[] Preset =
    [
        ("ssl", "SSL/TLS mode", "strict"),
        ("always_use_https", "Always use HTTPS", "on"),
        ("min_tls_version", "Minimum TLS version", "1.2"),
        ("websockets", "WebSockets (needed by the game client)", "on"),
        ("http3", "HTTP/3", "on"),
        ("rocket_loader", "Rocket Loader (breaks single-page apps)", "off"),
        ("email_obfuscation", "Email obfuscation (rewrites page HTML)", "off"),
    ];

    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.Cloudflare.v1");

    public Task<CloudflareAccount?> GetAccountAsync() => db.CloudflareAccounts.FirstOrDefaultAsync();

    public static List<CloudflareZone> Zones(CloudflareAccount account) => JsonSerializer.Deserialize<List<CloudflareZone>>(account.ZonesJson) ?? [];

    public static List<PermissionCheck> Checks(CloudflareAccount account) => JsonSerializer.Deserialize<List<PermissionCheck>>(account.ChecksJson) ?? [];

    public async Task<(CloudflareAccount Account, string Token)> RequireAsync()
    {
        var account = await GetAccountAsync() ?? throw new CloudflareNotConnectedException();

        return (account, _protector.Unprotect(account.TokenProtected));
    }

    /// <summary>Verifies a token, lists its zones, probes what it may do, and stores it.</summary>
    public async Task<CloudflareAccount> ConnectAsync(string token, PanelUser user, CancellationToken cancellationToken)
    {
        token = token.Trim();
        var status = await cloudflare.VerifyTokenAsync(token, cancellationToken);

        if (status.Status != "active")
        {
            throw new InvalidOperationException($"This token is {status.Status}. Create a new one or re-activate it in Cloudflare.");
        }

        var zones = (await cloudflare.ListZonesAsync(token, cancellationToken))
            .Select(z => new CloudflareZone(z.Id, z.Name, z.Status, z.Plan?.Name))
            .ToList();
        var checks = await ProbeAsync(token, zones, cancellationToken);

        var account = await GetAccountAsync();

        if (account is null)
        {
            account = new CloudflareAccount { TokenProtected = "" };
            db.CloudflareAccounts.Add(account);
        }

        account.TokenProtected = _protector.Protect(token);
        account.TokenId = status.Id;
        account.TokenStatus = status.Status;
        account.TokenExpiresOn = status.ExpiresOn;
        account.VerifiedAt = time.GetUtcNow();
        account.ZonesJson = JsonSerializer.Serialize(zones);
        account.ChecksJson = JsonSerializer.Serialize(checks);
        audit.Record("cloudflare.connected", user, details: $"{zones.Count} zones: {string.Join(", ", zones.Select(z => z.Name))}");
        await db.SaveChangesAsync(cancellationToken);

        return account;
    }

    public async Task DisconnectAsync(PanelUser user, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync();

        if (account is not null)
        {
            db.CloudflareAccounts.Remove(account);
            audit.Record("cloudflare.disconnected", user);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Tests each permission with a harmless call (writes are undone or no-ops), because a token
    /// can't list its own permissions without an extra "API Tokens: Read" grant.
    /// </summary>
    public async Task<List<PermissionCheck>> ProbeAsync(string token, IReadOnlyList<CloudflareZone> zones, CancellationToken cancellationToken)
    {
        var checks = new List<PermissionCheck>
        {
            zones.Count > 0
                ? new("zone.read", "Zone: Read", "Listing your domains", true, "ok", null)
                : new("zone.read", "Zone: Read", "Listing your domains", true, "missing", "The token can't see any zones."),
        };

        var zone = zones.FirstOrDefault(z => z.Status == "active") ?? zones.FirstOrDefault();

        if (zone is null)
        {
            checks.Add(new("dns.edit", "DNS: Edit", "Creating records for your sites", true, "unknown", "No zone to test with."));

            return checks;
        }

        checks.Add(await CheckAsync("dns.edit", "DNS: Edit", "Creating records for your sites", true, async () =>
        {
            var probe = await cloudflare.CreateDnsRecordAsync(token, zone.Id,
                new CloudflareDnsRecord(null, "TXT", $"_turbo-stack-check.{zone.Name}", "turbo-stack permission check", false, 60), cancellationToken);
            await cloudflare.DeleteDnsRecordAsync(token, zone.Id, probe.Id!, cancellationToken);
        }));

        checks.Add(await CheckAsync("settings.edit", "Zone Settings: Edit", "SSL mode, WebSockets and the other recommended settings", true, async () =>
        {
            var ssl = await cloudflare.GetZoneSettingAsync(token, zone.Id, "always_use_https", cancellationToken);
            await cloudflare.UpdateZoneSettingAsync(token, zone.Id, "always_use_https", ssl.Value, cancellationToken);
        }));

        checks.Add(await CheckAsync("cache.purge", "Cache Purge: Purge", "Clearing cached files after deploys", false, () =>
            cloudflare.PurgeCacheAsync(token, zone.Id, null, [$"https://{zone.Name}/__turbo-stack-permission-check"], cancellationToken)));

        checks.Add(await CheckAsync("ssl.edit", "SSL and Certificates: Edit", "Issuing Origin certificates for this server", false, () =>
            cloudflare.ListOriginCertificatesAsync(token, zone.Id, cancellationToken)));

        return checks;
    }

    private async Task<PermissionCheck> CheckAsync(string key, string label, string usedFor, bool required, Func<Task> probe)
    {
        try
        {
            await probe();

            return new PermissionCheck(key, label, usedFor, required, "ok", null);
        }
        catch (CloudflareApiException e)
        {
            logger.LogInformation("Cloudflare permission probe {Key} failed: {Message}", key, e.Message);

            return new PermissionCheck(key, label, usedFor, required, e.IsPermissionError ? "missing" : "error", e.Message);
        }
    }

    public async Task<CloudflareZone?> ZoneForAsync(string domain)
    {
        var account = await GetAccountAsync();

        return account is null ? null : CloudflareDnsPlanner.ZoneFor(Zones(account), z => z.Name, domain);
    }

    /// <summary>Points every domain of the site at this server (A/AAAA), proxied or not as configured.</summary>
    public async Task<List<DnsAction>> SyncDnsAsync(Site site, bool replaceConflicts, bool dryRun, CancellationToken cancellationToken)
    {
        var (account, token) = await RequireAsync();
        var server = await db.ServerSettings.FirstOrDefaultAsync(cancellationToken);
        var desired = new Dictionary<string, string>();

        if (server?.PublicIPv4 is { } v4)
        {
            desired["A"] = v4;
        }

        if (server?.PublicIPv6 is { } v6)
        {
            desired["AAAA"] = v6;
        }

        if (desired.Count == 0)
        {
            throw new InvalidOperationException("Set this server's public IP address in Settings first.");
        }

        var actions = new List<DnsAction>();

        foreach (var domain in site.Domains)
        {
            var zone = CloudflareDnsPlanner.ZoneFor(Zones(account), z => z.Name, domain)
                ?? throw new InvalidOperationException($"{domain} is not in a Cloudflare zone this token can access.");
            var existing = await cloudflare.ListDnsRecordsAsync(token, zone.Id, domain, cancellationToken);
            var plan = CloudflareDnsPlanner.Plan(domain, desired, site.CloudflareProxied, existing, replaceConflicts);

            if (!dryRun)
            {
                foreach (var action in plan)
                {
                    await ExecuteAsync(token, zone.Id, action, cancellationToken);
                }
            }

            actions.AddRange(plan);
        }

        return actions;
    }

    private async Task ExecuteAsync(string token, string zoneId, DnsAction action, CancellationToken cancellationToken)
    {
        var record = new CloudflareDnsRecord(action.Existing?.Id, action.Type, action.Name, action.Content!, action.Proxied, 1, CloudflareDnsPlanner.Comment);

        switch (action.Kind)
        {
            case DnsActionKind.Create:
                await cloudflare.CreateDnsRecordAsync(token, zoneId, record, cancellationToken);
                break;
            case DnsActionKind.Update:
                await cloudflare.UpdateDnsRecordAsync(token, zoneId, record, cancellationToken);
                break;
            case DnsActionKind.Delete:
                await cloudflare.DeleteDnsRecordAsync(token, zoneId, action.Existing!.Id!, cancellationToken);
                break;
        }
    }

    public async Task<List<ZoneSettingResult>> GetZoneSettingsAsync(string zoneId, CancellationToken cancellationToken)
    {
        var (_, token) = await RequireAsync();
        var current = (await cloudflare.GetZoneSettingsAsync(token, zoneId, cancellationToken)).ToDictionary(s => s.Id);

        return Preset.Select(p =>
        {
            var value = current.TryGetValue(p.Setting, out var setting) ? ValueText(setting.Value) : null;

            return new ZoneSettingResult(p.Setting, p.Label, value, p.Value.ToString()!, value == p.Value.ToString(), null);
        }).ToList();
    }

    /// <summary>Applies the recommended settings one by one (some are plan-dependent; failures are reported, not fatal).</summary>
    public async Task<List<ZoneSettingResult>> ApplyPresetAsync(string zoneId, PanelUser user, CancellationToken cancellationToken)
    {
        var (_, token) = await RequireAsync();
        var results = new List<ZoneSettingResult>();

        foreach (var (setting, label, value) in Preset)
        {
            try
            {
                var updated = await cloudflare.UpdateZoneSettingAsync(token, zoneId, setting, value, cancellationToken);
                results.Add(new ZoneSettingResult(setting, label, ValueText(updated.Value), value.ToString()!, true, null));
            }
            catch (CloudflareApiException e)
            {
                results.Add(new ZoneSettingResult(setting, label, null, value.ToString()!, false, e.Message));
            }
        }

        audit.Record("cloudflare.preset_applied", user, target: zoneId, details: $"{results.Count(r => r.Ok)}/{results.Count} settings applied");
        await db.SaveChangesAsync(cancellationToken);

        return results;
    }

    public async Task PurgeAsync(string zoneId, IReadOnlyList<string>? hosts, PanelUser? user, CancellationToken cancellationToken)
    {
        var (_, token) = await RequireAsync();
        await cloudflare.PurgeCacheAsync(token, zoneId, hosts, null, cancellationToken);
        audit.Record("cloudflare.cache_purged", user, target: zoneId, details: hosts is { Count: > 0 } ? string.Join(", ", hosts) : "everything");
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Refreshes Cloudflare's edge ranges: the nginx real-IP config (so apps see visitor IPs) and
    /// the system "Cloudflare" IP list used by the Cloudflare-only firewall mode.
    /// </summary>
    public async Task RefreshIpRangesAsync(CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync();

        if (account is null)
        {
            return;
        }

        var ranges = await cloudflare.GetIpRangesAsync(cancellationToken);
        var all = ranges.Ipv4Cidrs.Concat(ranges.Ipv6Cidrs).Where(FirewallRuleValidator.IsAddress).ToList();

        if (all.Count == 0)
        {
            return;
        }

        if (account.RealIpEnabled)
        {
            var result = await agent.Nginx.ApplyNginxConfigAsync(new NginxConfig
            {
                Name = RealIpConfigName,
                Kind = NginxConfigKind.Global,
                Content = RenderRealIpConfig(all),
            }, cancellationToken: cancellationToken);

            if (!result.Applied)
            {
                logger.LogWarning("Cloudflare real-IP config was rejected: {Error}", result.Error);
            }
        }
        else
        {
            await agent.Nginx.RemoveNginxConfigAsync(new NginxConfigRef { Name = RealIpConfigName, Kind = NginxConfigKind.Global },
                cancellationToken: cancellationToken);
        }

        var list = await db.IpLists.Include(l => l.Entries).FirstOrDefaultAsync(l => l.Name == IpListName && l.System, cancellationToken);
        var changed = list is null || !list.Entries.Select(e => e.Source).ToHashSet().SetEquals(all.Select(Core.Firewall.FirewallSource.Normalize));

        if (changed)
        {
            var snapshot = await firewall.SnapshotAsync();

            if (list is null)
            {
                list = new IpList { Name = IpListName, System = true, Description = "Cloudflare's edge networks, refreshed daily from api.cloudflare.com/client/v4/ips." };
                db.IpLists.Add(list);
            }

            list.Entries.Clear();
            list.Entries.AddRange(all.Select(cidr => new IpListEntry { Source = Core.Firewall.FirewallSource.Normalize(cidr) }));

            // Range updates are routine maintenance: applied without a confirmation window.
            await firewall.CommitAsync(snapshot, "Updated Cloudflare IP ranges", null, null, acknowledgeLockout: true, cancellationToken, systemChange: true);
        }

        account.IpRangesUpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string RenderRealIpConfig(IEnumerable<string> ranges)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Managed by {Branding.ProductName}: restore visitor IPs for requests that come through Cloudflare.");
        builder.AppendLine("# Only Cloudflare's own ranges are trusted to set CF-Connecting-IP.");

        foreach (var range in ranges)
        {
            builder.AppendLine($"set_real_ip_from {range};");
        }

        builder.AppendLine("real_ip_header CF-Connecting-IP;");

        return builder.ToString();
    }

    private static string ValueText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
}

/// <summary>Daily refresh of Cloudflare's edge IP ranges.</summary>
public sealed class CloudflareMaintenanceService(IServiceScopeFactory scopes, TimeProvider time, ILogger<CloudflareMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);

        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CloudflareService>().RefreshIpRangesAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Refreshing Cloudflare IP ranges failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
