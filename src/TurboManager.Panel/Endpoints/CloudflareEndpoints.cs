using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Cloudflare;
using TurboManager.Panel.Data;
using TurboManager.Panel.Firewall;
using TurboManager.Panel.Sites;

namespace TurboManager.Panel.Endpoints;

public static class CloudflareEndpoints
{
    public sealed record TokenRequest(string? Token);

    public sealed record PurgeRequest(List<string>? Hosts);

    public sealed record CloudflareSettingsRequest(bool RealIpEnabled);

    public sealed record CloudflareOnlyRequest(bool Enabled, bool AcknowledgeLockout = false);

    public sealed record DnsSyncRequest(bool ReplaceConflicts);

    public sealed record PanelAddressRequest(string? Domain);

    public sealed record CodeRequest(string? Code);

    public sealed record HandoverRequest(string? Token);

    public static void MapCloudflareEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").WithAgentErrors().AddEndpointFilter<CloudflareErrorFilter>().RequirePermission(Permissions.SitesView);
        var manage = app.MapGroup("/api").WithAgentErrors().AddEndpointFilter<CloudflareErrorFilter>().RequirePermission(Permissions.SitesManage);

        view.MapGet("/cloudflare", async (CloudflareService cloudflare, PanelDbContext db) => Results.Ok(await OverviewAsync(cloudflare, db)));

        manage.MapPut("/cloudflare/token", async (TokenRequest request, HttpContext http, PanelDbContext db, CloudflareService cloudflare,
            ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = ["Paste the API token."] });
            }

            await cloudflare.ConnectAsync(request.Token, await CurrentUser.GetAsync(http, db), cancellationToken);

            try
            {
                await cloudflare.RefreshIpRangesAsync(cancellationToken);
            }
            catch (Exception e) when (e is CloudflareApiException or HttpRequestException or InvalidOperationException)
            {
                loggers.CreateLogger("Cloudflare").LogWarning(e, "Could not refresh Cloudflare IP ranges after connecting");
            }

            return Results.Ok(await OverviewAsync(cloudflare, db));
        });

        manage.MapPost("/cloudflare/recheck", async (HttpContext http, PanelDbContext db, CloudflareService cloudflare, CancellationToken cancellationToken) =>
        {
            var (_, token) = await cloudflare.RequireAsync();
            await cloudflare.ConnectAsync(token, await CurrentUser.GetAsync(http, db), cancellationToken);

            return Results.Ok(await OverviewAsync(cloudflare, db));
        });

        manage.MapDelete("/cloudflare/token", async (HttpContext http, PanelDbContext db, CloudflareService cloudflare, CancellationToken cancellationToken) =>
        {
            await cloudflare.DisconnectAsync(await CurrentUser.GetAsync(http, db), cancellationToken);

            return Results.NoContent();
        });

        manage.MapPut("/cloudflare/settings", async (CloudflareSettingsRequest request, PanelDbContext db, CloudflareService cloudflare,
            CancellationToken cancellationToken) =>
        {
            var (account, _) = await cloudflare.RequireAsync();
            account.RealIpEnabled = request.RealIpEnabled;
            await db.SaveChangesAsync(cancellationToken);
            await cloudflare.RefreshIpRangesAsync(cancellationToken);

            return Results.Ok(await OverviewAsync(cloudflare, db));
        });

        view.MapGet("/cloudflare/zones/{zoneId}/settings", async (string zoneId, CloudflareService cloudflare, CancellationToken cancellationToken) =>
            Results.Ok(await cloudflare.GetZoneSettingsAsync(zoneId, cancellationToken)));

        manage.MapPost("/cloudflare/zones/{zoneId}/preset", async (string zoneId, HttpContext http, PanelDbContext db, CloudflareService cloudflare,
            CancellationToken cancellationToken) =>
            Results.Ok(await cloudflare.ApplyPresetAsync(zoneId, await CurrentUser.GetAsync(http, db), cancellationToken)));

        manage.MapPost("/cloudflare/zones/{zoneId}/purge", async (string zoneId, PurgeRequest request, HttpContext http, PanelDbContext db,
            CloudflareService cloudflare, CancellationToken cancellationToken) =>
        {
            await cloudflare.PurgeAsync(zoneId, request.Hosts, await CurrentUser.GetAsync(http, db), cancellationToken);

            return Results.NoContent();
        });

        // Only Cloudflare may reach ports 80/443: switches the HTTP/HTTPS rules to the Cloudflare IP list.
        app.MapPut("/api/cloudflare/cloudflare-only", async (CloudflareOnlyRequest request, HttpContext http, PanelDbContext db,
                CloudflareService cloudflare, FirewallService firewall, CancellationToken cancellationToken) =>
            {
                await cloudflare.RequireAsync();
                var list = await db.IpLists.FirstOrDefaultAsync(l => l.Name == CloudflareService.IpListName && l.System, cancellationToken);

                if (list is null)
                {
                    await cloudflare.RefreshIpRangesAsync(cancellationToken);
                    list = await db.IpLists.FirstAsync(l => l.Name == CloudflareService.IpListName && l.System, cancellationToken);
                }

                var snapshot = await firewall.SnapshotAsync();
                var rules = await db.FirewallRules.Where(r => r.Key == "http" || r.Key == "https").ToListAsync(cancellationToken);

                foreach (var rule in rules)
                {
                    rule.SourceKind = request.Enabled ? FirewallSourceKind.IpList : FirewallSourceKind.Any;
                    rule.IpListId = request.Enabled ? list.Id : null;
                    rule.SourceAddress = null;
                }

                var result = await firewall.CommitAsync(snapshot, request.Enabled ? "Web ports only reachable through Cloudflare" : "Web ports reachable from anywhere",
                    await CurrentUser.GetAsync(http, db), http.Connection.RemoteIpAddress, request.AcknowledgeLockout, cancellationToken);

                return Results.Ok(new
                {
                    result.Changed,
                    result.Reasons,
                    pending = result.Pending is null ? null : new { result.Pending.Id, revertAt = result.Pending.RevertAt.ToUnixTimeMilliseconds(), result.Pending.Summary },
                });
            })
            .AddEndpointFilter<AgentErrorFilter>()
            .AddEndpointFilter<CloudflareErrorFilter>()
            .RequirePermission(Permissions.FirewallManage);

        view.MapGet("/sites/{id:int}/cloudflare-dns", async (int id, SiteService sites, CloudflareService cloudflare, CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);

            return site is null ? Results.NotFound() : Results.Ok(DnsView(await cloudflare.SyncDnsAsync(site, replaceConflicts: false, dryRun: true, cancellationToken)));
        });

        manage.MapPost("/sites/{id:int}/cloudflare-dns", async (int id, DnsSyncRequest request, HttpContext http, PanelDbContext db, SiteService sites,
            CloudflareService cloudflare, AuditLog audit, CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);

            if (site is null)
            {
                return Results.NotFound();
            }

            var actions = await cloudflare.SyncDnsAsync(site, request.ReplaceConflicts, dryRun: false, cancellationToken);
            audit.Record("cloudflare.dns_synced", await CurrentUser.GetAsync(http, db), target: site.Name,
                details: string.Join(", ", actions.Where(a => a.Kind != DnsActionKind.Ok).Select(a => $"{a.Kind} {a.Type} {a.Name}")));
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(DnsView(actions));
        });

        manage.MapPost("/sites/{id:int}/purge", async (int id, HttpContext http, PanelDbContext db, SiteService sites, CloudflareService cloudflare,
            CancellationToken cancellationToken) =>
        {
            var site = await sites.GetAsync(id);

            if (site is null)
            {
                return Results.NotFound();
            }

            var zone = await cloudflare.ZoneForAsync(site.Domains[0]) ?? throw new InvalidOperationException($"{site.Domains[0]} is not in a connected Cloudflare zone.");
            await cloudflare.PurgeAsync(zone.Id, site.Domains, await CurrentUser.GetAsync(http, db), cancellationToken);

            return Results.NoContent();
        });

        view.MapGet("/panel-address", async (HttpContext http, PanelAddressService panelAddress) =>
        {
            var address = await panelAddress.GetAsync();

            return Results.Ok(new
            {
                domain = address?.Domain,
                address?.SiteId,
                verified = address?.VerifiedAt is not null,
                verifyUrl = address is null ? null : $"https://{address.Domain}/address-check?code={address.VerificationCode}",
                setupClosed = address?.SetupClosedAt is not null,
                onPanelDomain = address is not null && PanelAddressService.IsPanelHost(address, http.Request),
            });
        });

        manage.MapPut("/panel-address", async (PanelAddressRequest request, HttpContext http, PanelDbContext db, PanelAddressService panelAddress,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Domain))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["domain"] = ["Enter the domain for the panel, e.g. panel.myhotel.com."] });
            }

            var result = await panelAddress.SetAsync(request.Domain, await CurrentUser.GetAsync(http, db), cancellationToken);

            return Results.Ok(new { result.Applied, result.Error, result.CertificateJobId, result.DnsError, result.DnsConflicts });
        });

        app.MapPost("/api/panel-address/verify", async (CodeRequest request, HttpContext http, PanelAddressService panelAddress) =>
            await panelAddress.VerifyAsync(request.Code, http.Request) ? Results.NoContent() : Results.Problem("This address check is not valid here.", statusCode: 400))
            .RequireRateLimiting(Http.RateLimits.Auth);

        app.MapPost("/api/panel-address/handover", async (HttpContext http, PanelDbContext db, PanelAddressService panelAddress) =>
        {
            var address = await panelAddress.GetAsync();

            if (address?.VerifiedAt is null)
            {
                return Results.Problem("Verify the new address first.", statusCode: StatusCodes.Status409Conflict);
            }

            var token = panelAddress.CreateHandover(await CurrentUser.GetAsync(http, db));

            return Results.Ok(new { url = $"https://{address.Domain}/handover?token={token}" });
        }).RequirePermission(Permissions.SystemView);

        manage.MapPost("/panel-address/close-setup", async (HttpContext http, PanelDbContext db, PanelAddressService panelAddress, CancellationToken cancellationToken) =>
        {
            var result = await panelAddress.CloseSetupAsync(await CurrentUser.GetAsync(http, db), http.Request, http.Connection.RemoteIpAddress, cancellationToken);

            return Results.Ok(new
            {
                result.Changed,
                pending = result.Pending is null ? null : new { result.Pending.Id, revertAt = result.Pending.RevertAt.ToUnixTimeMilliseconds(), result.Pending.Summary },
            });
        }).RequirePermission(Permissions.FirewallManage);

        app.MapPost("/api/auth/handover", async (HandoverRequest request, HttpContext http, PanelDbContext db, PanelAddressService panelAddress, AuditLog audit) =>
        {
            var address = await panelAddress.GetAsync();
            var userId = address is not null && PanelAddressService.IsPanelHost(address, http.Request) ? panelAddress.RedeemHandover(request.Token) : null;
            var user = userId is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == userId);

            if (user is null)
            {
                return Results.Problem("This sign-in link is invalid or has expired. Sign in normally.", statusCode: StatusCodes.Status401Unauthorized);
            }

            await SessionManager.SignInAsync(http, AuthSchemes.Session, user);
            audit.Record("auth.handover", user, target: address!.Domain);
            await db.SaveChangesAsync();

            return Results.NoContent();
        }).RequireRateLimiting(Http.RateLimits.Auth);
    }

    private static async Task<object> OverviewAsync(CloudflareService cloudflare, PanelDbContext db)
    {
        var account = await cloudflare.GetAccountAsync();

        if (account is null)
        {
            return new { connected = false };
        }

        var webRules = await db.FirewallRules.Include(r => r.IpList).Where(r => r.Key == "http" || r.Key == "https").ToListAsync();
        var cloudflareOnly = webRules.Count > 0
            && webRules.All(r => r.SourceKind == FirewallSourceKind.IpList && r.IpList is { System: true, Name: CloudflareService.IpListName });

        return new
        {
            connected = true,
            token = new { status = account.TokenStatus, expiresOn = account.TokenExpiresOn, account.VerifiedAt },
            checks = CloudflareService.Checks(account),
            zones = CloudflareService.Zones(account),
            account.RealIpEnabled,
            account.IpRangesUpdatedAt,
            cloudflareOnlyWeb = cloudflareOnly,
        };
    }

    private static IEnumerable<object> DnsView(IEnumerable<DnsAction> actions) =>
        actions.Select(a => new { kind = a.Kind.ToString().ToLowerInvariant(), a.Type, a.Name, a.Content, a.Proxied, existing = a.Existing?.Content, a.Reason });
}

/// <summary>Maps Cloudflare failures and "not set up yet" conditions to problem responses.</summary>
public sealed class CloudflareErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (CloudflareApiException e)
        {
            return Results.Problem(e.IsPermissionError ? $"Cloudflare refused: {e.Message}. Check the token's permissions." : $"Cloudflare: {e.Message}",
                statusCode: e.IsPermissionError ? StatusCodes.Status403Forbidden : StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException e)
        {
            return Results.Problem($"Could not reach Cloudflare: {e.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
