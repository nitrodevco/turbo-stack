using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Cloudflare;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;

namespace TurboManager.Panel.Sites;

public sealed record SiteApplyResult(bool Applied, string? Error, int Revision, long? CertificateJobId, string? DnsError = null, int DnsConflicts = 0);

/// <summary>Renders sites, applies them through the agent and keeps their revision history.</summary>
public sealed class SiteService(PanelDbContext db, AgentChannelFactory agent, CertificateService certificates, JobRunner jobs, CloudflareService cloudflare)
{
    public static readonly string[] AutoCreatedWebRoots = ["/var/www/", "/srv/"];

    public Task<List<Site>> GetAllAsync() => db.Sites.Include(s => s.Routes).OrderBy(s => s.Name).ToListAsync();

    public Task<Site?> GetAsync(int id) => db.Sites.Include(s => s.Routes).FirstOrDefaultAsync(s => s.Id == id);

    /// <summary>Domains used by sites other than <paramref name="exceptSiteId"/>.</summary>
    public async Task<List<string>> DomainsInUseAsync(int? exceptSiteId) =>
        (await db.Sites.Where(s => s.Id != exceptSiteId).Select(s => s.Domains).ToListAsync()).SelectMany(d => d).ToList();

    public async Task<string> RenderAsync(Site site)
    {
        var certificate = site.TlsMode == TlsMode.None ? null : await certificates.FindForAsync(site);

        return SiteRenderer.Render(site, certificate is null ? null : CertificateService.PathsFor(certificate));
    }

    /// <summary>
    /// Applies the site's current model. HTTPS sites without a certificate are applied HTTP-only
    /// first (Let's Encrypt validates over port 80), then a certificate job is started which
    /// re-applies the site once the certificate is installed.
    /// </summary>
    public async Task<SiteApplyResult> ApplyAsync(Site site, PanelUser? user, bool requestCertificate, CancellationToken cancellationToken)
    {
        var certificate = site.TlsMode == TlsMode.None ? null : await certificates.FindForAsync(site);
        var content = SiteRenderer.Render(site, certificate is null ? null : CertificateService.PathsFor(certificate));

        if (site.Enabled && site.CustomConfig is null)
        {
            foreach (var route in site.Routes.Where(r => r.Kind is RouteKind.Static or RouteKind.Spa))
            {
                if (AutoCreatedWebRoots.Any(root => route.Target.StartsWith(root, StringComparison.Ordinal)))
                {
                    await agent.Nginx.EnsureWebRootAsync(new WebRootRequest { Path = route.Target, CreatePlaceholder = true },
                        cancellationToken: cancellationToken);
                }
            }
        }

        var result = site.Enabled
            ? await agent.Nginx.ApplyNginxConfigAsync(new NginxConfig { Name = site.Name, Kind = NginxConfigKind.Site, Content = content },
                cancellationToken: cancellationToken)
            : await agent.Nginx.RemoveNginxConfigAsync(new NginxConfigRef { Name = site.Name, Kind = NginxConfigKind.Site },
                cancellationToken: cancellationToken);

        var number = (await db.SiteRevisions.Where(r => r.SiteId == site.Id).MaxAsync(r => (int?)r.Number, cancellationToken) ?? 0) + 1;
        db.SiteRevisions.Add(new SiteRevision
        {
            SiteId = site.Id,
            Number = number,
            Content = content,
            ModelJson = JsonSerializer.Serialize(SiteModel.From(site)),
            Applied = result.Applied,
            Error = result.Applied ? null : result.Error,
            CreatedBy = user?.Username,
        });

        site.LastError = result.Applied ? null : result.Error;

        if (result.Applied)
        {
            site.LastAppliedRevision = number;
        }

        await db.SaveChangesAsync(cancellationToken);

        long? jobId = null;

        if (result.Applied && site.Enabled && requestCertificate && certificate is null && CertificateService.KindFor(site.TlsMode) is { } kind)
        {
            jobId = (await StartCertificateJobAsync(await CertificateDomainsAsync(site, kind), kind, user)).Id;
        }

        string? dnsError = null;
        var dnsConflicts = 0;

        if (result.Applied && site.Enabled && site.CloudflareDns && requestCertificate)
        {
            try
            {
                dnsConflicts = (await cloudflare.SyncDnsAsync(site, replaceConflicts: false, dryRun: false, cancellationToken))
                    .Count(a => a.Kind == DnsActionKind.Conflict);
            }
            catch (Exception e) when (e is CloudflareApiException or InvalidOperationException)
            {
                dnsError = e.Message;
            }
        }

        return new SiteApplyResult(result.Applied, result.Error, number, jobId, dnsError, dnsConflicts);
    }

    /// <summary>
    /// Origin certificates cover the whole zone (zone + *.zone) so one certificate serves every hotel
    /// subdomain; anything deeper is added explicitly. Other kinds cover exactly the site's domains.
    /// </summary>
    public async Task<List<string>> CertificateDomainsAsync(Site site, CertificateKind kind)
    {
        if (kind != CertificateKind.CloudflareOrigin || await cloudflare.ZoneForAsync(site.Domains[0]) is not { } zone)
        {
            return [.. site.Domains];
        }

        List<string> domains = [zone.Name, $"*.{zone.Name}"];
        domains.AddRange(site.Domains.Where(d => !CertificateService.Covers(domains, d)));

        return domains;
    }

    public async Task RemoveAsync(Site site, CancellationToken cancellationToken)
    {
        var result = await agent.Nginx.RemoveNginxConfigAsync(new NginxConfigRef { Name = site.Name, Kind = NginxConfigKind.Site },
            cancellationToken: cancellationToken);

        if (!result.Applied)
        {
            throw new InvalidOperationException($"nginx refused to remove the site: {result.Error}");
        }

        db.Sites.Remove(site);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Issues a certificate in the background, then re-applies every site it now covers.</summary>
    public async Task<Job> StartCertificateJobAsync(IReadOnlyList<string> domains, CertificateKind kind, PanelUser? user)
    {
        var record = await certificates.GetOrCreateAsync(domains, kind);
        await db.SaveChangesAsync();
        var certificateId = record.Id;

        return await jobs.StartAsync("certificates", $"Certificate for {string.Join(", ", domains)}", user, async context =>
        {
            var scoped = context.Services.GetRequiredService<CertificateService>();
            var scopedDb = context.Services.GetRequiredService<PanelDbContext>();
            var sites = context.Services.GetRequiredService<SiteService>();
            var certificate = await scopedDb.Certificates.FirstAsync(c => c.Id == certificateId, context.CancellationToken);

            if (!await scoped.IssueAsync(certificate, context.Log, context.CancellationToken))
            {
                return false;
            }

            foreach (var site in await sites.GetAllAsync())
            {
                if (site.Enabled && site.TlsMode != TlsMode.None && site.Domains.All(d => CertificateService.Covers(certificate.Domains, d)))
                {
                    var applied = await sites.ApplyAsync(site, user: null, requestCertificate: false, context.CancellationToken);
                    context.Log(applied.Applied ? $"HTTPS enabled for {site.Name}." : $"Re-applying {site.Name} failed: {applied.Error}");
                }
            }

            return true;
        });
    }
}

/// <summary>Hourly: renews Let's Encrypt certificates due within 30 days and retries failed ones.</summary>
public sealed class CertificateRenewalService(IServiceScopeFactory scopes, TimeProvider time, ILogger<CertificateRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the agent a moment after boot, then check hourly (one certificate per check; jobs are serialised).
        await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);

        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var sites = scope.ServiceProvider.GetRequiredService<SiteService>();
                var now = time.GetUtcNow();
                var due = (await db.Certificates.Where(c => c.AutoRenew && c.Kind != CertificateKind.CloudflareOrigin).ToListAsync(stoppingToken))
                    .Where(c => (c.NotAfter ?? DateTimeOffset.MinValue) - now < CertificateService.RenewBefore)
                    .Where(c => c.LastAttemptAt is null || now - c.LastAttemptAt > TimeSpan.FromHours(6))
                    .ToList();

                foreach (var certificate in due)
                {
                    logger.LogInformation("Renewing certificate {Name}", certificate.Name);
                    await sites.StartCertificateJobAsync(certificate.Domains, certificate.Kind, user: null);

                    break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Certificate renewal check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
