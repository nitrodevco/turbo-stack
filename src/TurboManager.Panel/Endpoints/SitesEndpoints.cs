using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;
using TurboManager.Panel.Sites;

namespace TurboManager.Panel.Endpoints;

public static class SitesEndpoints
{
    public sealed record CertificateRequest(List<string>? Domains, CertificateKind Kind);

    public sealed record ServerSettingsRequest(string? PublicIPv4, string? PublicIPv6);

    public static void MapSitesEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.SitesView).RequireScope();
        var manage = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.SitesManage);

        view.MapGet("/sites", async (SiteService sites, CertificateService certificates, AccessScope scope) =>
        {
            var limits = await scope.GetAsync();
            var all = (await sites.GetAllAsync()).Where(limits.Site);
            var result = new List<object>();

            foreach (var site in all)
            {
                var certificate = site.TlsMode == TlsMode.None ? null : await certificates.FindForAsync(site);
                result.Add(new
                {
                    site.Id,
                    site.Name,
                    site.Domains,
                    tlsMode = site.TlsMode,
                    https = certificate is not null,
                    certificateExpires = certificate?.NotAfter,
                    site.Enabled,
                    site.System,
                    custom = site.CustomConfig is not null,
                    site.LastError,
                    routes = site.Routes.OrderBy(r => r.SortOrder).Select(r => new { r.Path, r.Kind, r.Target }),
                });
            }

            return Results.Ok(result);
        });

        view.MapGet("/sites/{id:int}", async (int id, SiteService sites, PanelDbContext db) =>
        {
            var site = await sites.GetAsync(id);

            if (site is null)
            {
                return Results.NotFound();
            }

            var revisions = await db.SiteRevisions.Where(r => r.SiteId == id).OrderByDescending(r => r.Number).Take(30)
                .Select(r => new { r.Number, r.Applied, r.Error, r.CreatedBy, r.CreatedAt })
                .ToListAsync();

            return Results.Ok(new { site.Id, site.System, site.LastError, site.LastAppliedRevision, model = SiteModel.From(site), revisions });
        });

        view.MapGet("/sites/{id:int}/revisions/{number:int}", async (int id, int number, PanelDbContext db) =>
        {
            var revision = await db.SiteRevisions.FirstOrDefaultAsync(r => r.SiteId == id && r.Number == number);

            return revision is null ? Results.NotFound() : Results.Ok(new { revision.Number, revision.Content, revision.Applied, revision.Error, revision.CreatedAt });
        });

        // Renders a config without saving or applying it (editor preview).
        manage.MapPost("/sites/preview", async (SiteModel model, SiteService sites) =>
        {
            var site = new Site { Name = "preview" };
            model.ApplyTo(site);

            return Results.Text(await sites.RenderAsync(site), "text/plain");
        });

        manage.MapPost("/sites", async (SiteModel model, HttpContext http, PanelDbContext db, SiteService sites, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var site = new Site { Name = "" };
            model.ApplyTo(site);

            if (SiteValidator.Validate(site, await sites.DomainsInUseAsync(null)) is { } error)
            {
                return Invalid(error);
            }

            if (await db.Sites.AnyAsync(s => s.Name == site.Name, cancellationToken))
            {
                return Invalid($"A site named \"{site.Name}\" already exists.");
            }

            var user = await CurrentUser.GetAsync(http, db);
            db.Sites.Add(site);
            audit.Record("site.created", user, target: site.Name, details: string.Join(", ", site.Domains));
            await db.SaveChangesAsync(cancellationToken);

            var result = await sites.ApplyAsync(site, user, requestCertificate: true, cancellationToken);

            return Results.Ok(new { site.Id, result.Applied, result.Error, result.Revision, result.CertificateJobId, result.DnsError, result.DnsConflicts });
        });

        manage.MapPut("/sites/{id:int}", async (int id, SiteModel model, HttpContext http, PanelDbContext db, SiteService sites, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);

            if (site is null)
            {
                return Results.NotFound();
            }

            var previousName = site.Name;
            model.ApplyTo(site);

            if (site.System)
            {
                site.Name = previousName;
            }

            if (site.Name != previousName)
            {
                return Invalid("A site can't be renamed; create a new one instead.");
            }

            if (SiteValidator.Validate(site, await sites.DomainsInUseAsync(id), allowReservedName: site.System) is { } error)
            {
                return Invalid(error);
            }

            var user = await CurrentUser.GetAsync(http, db);
            audit.Record("site.updated", user, target: site.Name);
            await db.SaveChangesAsync(cancellationToken);

            var result = await sites.ApplyAsync(site, user, requestCertificate: true, cancellationToken);

            return Results.Ok(new { site.Id, result.Applied, result.Error, result.Revision, result.CertificateJobId, result.DnsError, result.DnsConflicts });
        });

        manage.MapPost("/sites/{id:int}/apply", async (int id, HttpContext http, PanelDbContext db, SiteService sites, CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);

            if (site is null)
            {
                return Results.NotFound();
            }

            var result = await sites.ApplyAsync(site, await CurrentUser.GetAsync(http, db), requestCertificate: true, cancellationToken);

            return Results.Ok(new { site.Id, result.Applied, result.Error, result.Revision, result.CertificateJobId, result.DnsError, result.DnsConflicts });
        });

        manage.MapPost("/sites/{id:int}/revisions/{number:int}/restore", async (int id, int number, HttpContext http, PanelDbContext db,
            SiteService sites, AuditLog audit, CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);
            var revision = await db.SiteRevisions.FirstOrDefaultAsync(r => r.SiteId == id && r.Number == number, cancellationToken);

            if (site is null || revision is null)
            {
                return Results.NotFound();
            }

            var model = JsonSerializer.Deserialize<SiteModel>(revision.ModelJson)!;
            model.ApplyTo(site);

            if (SiteValidator.Validate(site, await sites.DomainsInUseAsync(id), allowReservedName: site.System) is { } error)
            {
                return Invalid($"Revision {number} can't be restored: {error}");
            }

            var user = await CurrentUser.GetAsync(http, db);
            audit.Record("site.restored", user, target: site.Name, details: $"revision {number}");
            await db.SaveChangesAsync(cancellationToken);

            var result = await sites.ApplyAsync(site, user, requestCertificate: true, cancellationToken);

            return Results.Ok(new { site.Id, result.Applied, result.Error, result.Revision, result.CertificateJobId, result.DnsError, result.DnsConflicts });
        });

        manage.MapDelete("/sites/{id:int}", async (int id, HttpContext http, PanelDbContext db, SiteService sites, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);

            if (site is null)
            {
                return Results.NotFound();
            }

            if (site.System)
            {
                return Results.Problem("This site is managed by the panel itself.", statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                await sites.RemoveAsync(site, cancellationToken);
            }
            catch (InvalidOperationException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            audit.Record("site.deleted", await CurrentUser.GetAsync(http, db), target: site.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        // Where do the site's domains point right now?
        view.MapGet("/sites/{id:int}/dns", async (int id, SiteService sites, PanelDbContext db, CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);

            if (site is null)
            {
                return Results.NotFound();
            }

            var server = await db.ServerSettings.FirstOrDefaultAsync(cancellationToken);
            var ours = new[] { server?.PublicIPv4, server?.PublicIPv6 }.OfType<string>().ToHashSet();
            var results = new List<object>();

            foreach (var domain in site.Domains)
            {
                string[] addresses;

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    addresses = (await Dns.GetHostAddressesAsync(domain, timeout.Token)).Select(a => a.ToString()).ToArray();
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    addresses = [];
                }

                results.Add(new
                {
                    domain,
                    addresses,
                    pointsHere = ours.Count == 0 ? (bool?)null : addresses.Any(ours.Contains),
                });
            }

            return Results.Ok(results);
        });

        view.MapGet("/certificates", async (PanelDbContext db) =>
            Results.Ok(await db.Certificates.OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.Domains, kind = c.Kind, status = c.Status, c.NotAfter, c.IssuedAt, c.LastError, c.AutoRenew })
                .ToListAsync()));

        manage.MapPost("/certificates", async (CertificateRequest request, HttpContext http, PanelDbContext db, SiteService sites, AuditLog audit) =>
        {
            var domains = (request.Domains ?? []).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();

            if (domains.Count == 0)
            {
                return Invalid("Add at least one domain.");
            }

            var user = await CurrentUser.GetAsync(http, db);

            try
            {
                var job = await sites.StartCertificateJobAsync(domains, request.Kind, user);
                audit.Record("certificate.requested", user, target: string.Join(", ", domains), details: request.Kind.ToString());
                await db.SaveChangesAsync();

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }
            catch (JobConflictException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        manage.MapPost("/certificates/{id:int}/renew", async (int id, HttpContext http, PanelDbContext db, SiteService sites) =>
        {
            var certificate = await db.Certificates.FindAsync(id);

            if (certificate is null)
            {
                return Results.NotFound();
            }

            try
            {
                var job = await sites.StartCertificateJobAsync(certificate.Domains, certificate.Kind, await CurrentUser.GetAsync(http, db));

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }
            catch (JobConflictException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        manage.MapDelete("/certificates/{id:int}", async (int id, HttpContext http, PanelDbContext db, SiteService sites, AgentChannelFactory agent,
            AuditLog audit, CancellationToken cancellationToken) =>
        {
            var certificate = await db.Certificates.FindAsync([id], cancellationToken);

            if (certificate is null)
            {
                return Results.NotFound();
            }

            var users = (await sites.GetAllAsync()).Where(s => s.TlsMode != TlsMode.None && s.Domains.All(d => CertificateService.Covers(certificate.Domains, d))).ToList();

            if (users.Count > 0)
            {
                return Results.Problem($"Used by: {string.Join(", ", users.Select(s => s.Name))}.", statusCode: StatusCodes.Status409Conflict);
            }

            await agent.Nginx.RemoveCertificateAsync(new Contracts.Agent.CertificateRef { Name = certificate.Name }, cancellationToken: cancellationToken);
            db.Certificates.Remove(certificate);
            audit.Record("certificate.deleted", await CurrentUser.GetAsync(http, db), target: certificate.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        view.MapGet("/nginx/status", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok(await agent.Nginx.GetNginxStatusAsync(new Empty(), cancellationToken: cancellationToken)));

        view.MapGet("/settings/server", async (PanelDbContext db) =>
        {
            var settings = await db.ServerSettings.FirstOrDefaultAsync() ?? new ServerSettings();

            return Results.Ok(new { settings.PublicIPv4, settings.PublicIPv6 });
        });

        manage.MapPut("/settings/server", async (ServerSettingsRequest request, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            if (!ValidAddress(request.PublicIPv4, AddressFamily.InterNetwork) || !ValidAddress(request.PublicIPv6, AddressFamily.InterNetworkV6))
            {
                return Invalid("Enter valid public IPv4/IPv6 addresses (or leave them empty).");
            }

            var settings = await db.ServerSettings.FirstOrDefaultAsync() ?? db.ServerSettings.Add(new ServerSettings()).Entity;
            settings.PublicIPv4 = NullIfEmpty(request.PublicIPv4);
            settings.PublicIPv6 = NullIfEmpty(request.PublicIPv6);
            audit.Record("settings.server_updated", await CurrentUser.GetAsync(http, db), details: $"{settings.PublicIPv4} {settings.PublicIPv6}".Trim());
            await db.SaveChangesAsync();

            return Results.Ok(new { settings.PublicIPv4, settings.PublicIPv6 });
        });

        // Asks Cloudflare's public trace endpoint which address this server's traffic comes from.
        manage.MapPost("/settings/server/detect-ip", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
        {
            var client = clients.CreateClient("detect-ip");

            return Results.Ok(new
            {
                publicIPv4 = await TraceAsync(client, "https://1.1.1.1/cdn-cgi/trace", cancellationToken),
                publicIPv6 = await TraceAsync(client, "https://[2606:4700:4700::1111]/cdn-cgi/trace", cancellationToken),
            });
        });
    }

    private static async Task<string?> TraceAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            var body = await client.GetStringAsync(url, cancellationToken);

            return body.Split('\n').FirstOrDefault(l => l.StartsWith("ip=", StringComparison.Ordinal))?[3..].Trim();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static bool ValidAddress(string? value, AddressFamily family) =>
        string.IsNullOrWhiteSpace(value) || (IPAddress.TryParse(value.Trim(), out var address) && address.AddressFamily == family);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["site"] = [message] });
}
