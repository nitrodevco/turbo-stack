using System.Net;
using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Cloudflare;
using TurboManager.Panel.Data;
using TurboManager.Panel.Firewall;

namespace TurboManager.Panel.Sites;

/// <summary>
/// Moves the panel from https://&lt;ip&gt;:8443 to its own domain: a system site proxying to the panel,
/// an end-to-end check from the browser, a one-time sign-in link for the new address, and finally
/// closing the setup address (nginx site + firewall rule).
/// </summary>
public sealed class PanelAddressService(
    PanelDbContext db,
    SiteService sites,
    CloudflareService cloudflare,
    FirewallService firewall,
    AgentChannelFactory agent,
    AuditLog audit,
    IMemoryCache cache,
    TimeProvider time)
{
    public const string SiteName = "panel";

    public const string PanelUpstream = "http://127.0.0.1:7800";

    public static readonly TimeSpan HandoverLifetime = TimeSpan.FromMinutes(2);

    public Task<PanelAddress?> GetAsync() => db.PanelAddresses.FirstOrDefaultAsync();

    public async Task<SiteApplyResult> SetAsync(string domain, PanelUser user, CancellationToken cancellationToken)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var zone = await cloudflare.ZoneForAsync(domain);
        var account = await cloudflare.GetAccountAsync();
        var canIssueOrigin = account is not null && CloudflareService.Checks(account).Any(c => c.Key == "ssl.edit" && c.Status == "ok");

        var site = await db.Sites.Include(s => s.Routes).FirstOrDefaultAsync(s => s.Name == SiteName, cancellationToken);

        if (site is null)
        {
            site = new Site { Name = SiteName, System = true };
            db.Sites.Add(site);
        }

        new SiteModel(
            SiteName,
            [domain],
            [new SiteRouteModel("/", RouteKind.WebSocket, PanelUpstream)],
            zone is not null && canIssueOrigin ? TlsMode.CloudflareOrigin : TlsMode.LetsEncrypt,
            ForceHttps: true,
            Hsts: false,
            SecurityHeaders: false, // the panel sets its own
            ClientMaxBodyMb: 100,
            CloudflareDns: zone is not null,
            CloudflareProxied: true).ApplyTo(site);
        site.System = true;

        if (SiteValidator.Validate(site, await sites.DomainsInUseAsync(site.Id == 0 ? null : site.Id), allowReservedName: true) is { } error)
        {
            throw new InvalidOperationException(error);
        }

        var address = await GetAsync();

        if (address is null)
        {
            address = new PanelAddress { Domain = domain, VerificationCode = "" };
            db.PanelAddresses.Add(address);
        }

        address.Domain = domain;
        address.VerificationCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        address.VerifiedAt = null;
        audit.Record("panel_address.set", user, target: domain);
        await db.SaveChangesAsync(cancellationToken);

        address.SiteId = site.Id;
        await db.SaveChangesAsync(cancellationToken);

        return await sites.ApplyAsync(site, user, requestCertificate: true, cancellationToken);
    }

    /// <summary>Called by the browser from the new address: proves DNS, TLS, proxy and panel all work together.</summary>
    public async Task<bool> VerifyAsync(string? code, HttpRequest request)
    {
        var address = await GetAsync();

        if (address is null || code is null || !request.IsHttps || !IsPanelHost(address, request)
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(address.VerificationCode)))
        {
            return false;
        }

        address.VerifiedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync();

        return true;
    }

    public static bool IsPanelHost(PanelAddress address, HttpRequest request) =>
        request.Host.Host.Equals(address.Domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>A one-time link that signs the same user in on the new address (cookies don't cross hosts).</summary>
    public string CreateHandover(PanelUser user)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        cache.Set(HandoverKey(token), user.Id, HandoverLifetime);

        return token;
    }

    public int? RedeemHandover(string? token)
    {
        if (string.IsNullOrEmpty(token) || !cache.TryGetValue(HandoverKey(token), out int userId))
        {
            return null;
        }

        cache.Remove(HandoverKey(token));

        return userId;
    }

    /// <summary>
    /// Removes the IP:8443 entry point. Must be requested through the new address, so the firewall
    /// change (which needs confirming) is confirmed over the path that will keep working.
    /// </summary>
    public async Task<FirewallApplyResult> CloseSetupAsync(PanelUser user, HttpRequest request, IPAddress? requester, CancellationToken cancellationToken)
    {
        var address = await GetAsync();

        if (address?.VerifiedAt is null)
        {
            throw new InvalidOperationException("Verify the new panel address before closing the setup address.");
        }

        if (!IsPanelHost(address, request))
        {
            throw new InvalidOperationException($"Open the panel at https://{address.Domain} and close the setup address from there.");
        }

        var result = await agent.Nginx.CloseSetupAddressAsync(new Empty(), cancellationToken: cancellationToken);

        if (!result.Applied)
        {
            throw new InvalidOperationException($"nginx refused the change: {result.Error}");
        }

        address.SetupClosedAt = time.GetUtcNow();
        audit.Record("panel_address.setup_closed", user, target: address.Domain);
        await db.SaveChangesAsync(cancellationToken);

        var snapshot = await firewall.SnapshotAsync();
        var rule = await db.FirewallRules.FirstOrDefaultAsync(r => r.Key == "setup", cancellationToken);

        if (rule is null || !rule.Enabled)
        {
            return new FirewallApplyResult(false, null, []);
        }

        rule.Enabled = false;

        return await firewall.CommitAsync(snapshot, "Closed the setup port (8443)", user, requester, acknowledgeLockout: true, cancellationToken);
    }

    private static string HandoverKey(string token) => $"panel-handover:{token}";
}
