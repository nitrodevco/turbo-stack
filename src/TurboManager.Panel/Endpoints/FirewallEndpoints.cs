using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Core.Firewall;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Firewall;

namespace TurboManager.Panel.Endpoints;

public static class FirewallEndpoints
{
    public sealed record RuleRequest(
        string? Name, string? Action, string? Protocol, string? Ports, FirewallSourceKind SourceKind,
        string? SourceAddress, int? IpListId, bool Enabled = true, bool AcknowledgeLockout = false);

    public sealed record SettingsRequest(bool Enabled, string? DefaultIncoming, bool AcknowledgeLockout = false);

    public sealed record IpListRequest(string? Name, string? Description, List<IpListEntryRequest>? Entries, bool AcknowledgeLockout = false);

    public sealed record IpListEntryRequest(string? Source, string? Note);

    public sealed record BanRequest(string? Ip);

    public sealed record JailRequest(int BanTimeSeconds, int FindTimeSeconds, int MaxRetry, List<string>? IgnoreIps);

    public static void MapFirewallEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/firewall").WithAgentErrors().RequirePermission(Permissions.FirewallView);
        var manage = app.MapGroup("/api/firewall").WithAgentErrors().RequirePermission(Permissions.FirewallManage);

        view.MapGet("/", async (FirewallService firewall, CancellationToken cancellationToken) =>
        {
            await firewall.ReconcileAsync(cancellationToken);

            var settings = await firewall.GetSettingsAsync();
            var rules = await firewall.GetRulesAsync();
            var actual = await firewall.GetActualStateAsync(cancellationToken);
            var applied = FirewallPlanner.ActualManaged(actual);
            var pending = await firewall.GetPendingAsync();
            var plan = FirewallPlanner.Plan(rules, settings, actual);

            return Results.Ok(new
            {
                settings = new { settings.Enabled, settings.DefaultIncoming },
                actual = new { actual.Available, actual.Enabled, actual.DefaultIncoming, actual.Error },
                rules = rules.Select(r => new
                {
                    r.Id,
                    r.Key,
                    r.Name,
                    r.Action,
                    r.Protocol,
                    r.Ports,
                    sourceKind = r.SourceKind.ToString(),
                    r.SourceAddress,
                    r.IpListId,
                    ipListName = r.IpList?.Name,
                    r.Enabled,
                    r.System,
                    status = !r.Enabled
                        ? "disabled"
                        : FirewallPlanner.Desired([r]).All(applied.Contains) ? "applied" : "not-applied",
                }),
                external = actual.Rules
                    .Where(r => !r.Comment.StartsWith(FirewallPlanner.CommentPrefix, StringComparison.Ordinal))
                    .GroupBy(r => (r.Action, r.Protocol, r.Ports, Source: FirewallSource.Normalize(r.Source), r.Direction, r.Comment))
                    .Select(g => new { g.Key.Action, g.Key.Protocol, g.Key.Ports, g.Key.Source, g.Key.Direction, g.Key.Comment, ipv6Only = g.All(r => r.Ipv6) }),
                inSync = plan.IsEmpty,
                pending = pending is null ? null : new { pending.Id, revertAt = pending.RevertAt.ToUnixTimeMilliseconds(), pending.Summary },
            });
        });

        manage.MapPost("/rules", async (RuleRequest request, HttpContext http, PanelDbContext db, FirewallService firewall, CancellationToken cancellationToken) =>
        {
            if (await ValidateRuleAsync(request, db) is { } error)
            {
                return error;
            }

            var snapshot = await firewall.SnapshotAsync();
            var rule = new ManagedFirewallRule { Key = $"r{Guid.NewGuid():N}"[..13], Name = request.Name!.Trim(), Ports = "" };
            ApplyRequest(rule, request);
            db.FirewallRules.Add(rule);

            return await CommitAsync(http, db, firewall, snapshot, $"Added rule \"{rule.Name}\"", request.AcknowledgeLockout, cancellationToken);
        });

        manage.MapPut("/rules/{id:int}", async (int id, RuleRequest request, HttpContext http, PanelDbContext db, FirewallService firewall,
            CancellationToken cancellationToken) =>
        {
            var rule = await db.FirewallRules.FindAsync([id], cancellationToken);

            if (rule is null)
            {
                return Results.NotFound();
            }

            if (await ValidateRuleAsync(request, db) is { } error)
            {
                return error;
            }

            var snapshot = await firewall.SnapshotAsync();
            ApplyRequest(rule, request);

            return await CommitAsync(http, db, firewall, snapshot, $"Updated rule \"{rule.Name}\"", request.AcknowledgeLockout, cancellationToken);
        });

        manage.MapDelete("/rules/{id:int}", async (int id, bool? acknowledgeLockout, HttpContext http, PanelDbContext db, FirewallService firewall,
            CancellationToken cancellationToken) =>
        {
            var rule = await db.FirewallRules.FindAsync([id], cancellationToken);

            if (rule is null)
            {
                return Results.NotFound();
            }

            var snapshot = await firewall.SnapshotAsync();
            db.FirewallRules.Remove(rule);

            return await CommitAsync(http, db, firewall, snapshot, $"Deleted rule \"{rule.Name}\"", acknowledgeLockout ?? false, cancellationToken);
        });

        manage.MapPut("/settings", async (SettingsRequest request, HttpContext http, PanelDbContext db, FirewallService firewall,
            CancellationToken cancellationToken) =>
        {
            if (request.DefaultIncoming is not ("deny" or "reject" or "allow"))
            {
                return Invalid("defaultIncoming", "Default for incoming traffic must be deny, reject or allow.");
            }

            var snapshot = await firewall.SnapshotAsync();
            var settings = await firewall.GetSettingsAsync();
            settings.Enabled = request.Enabled;
            settings.DefaultIncoming = request.DefaultIncoming;

            var summary = $"Firewall {(request.Enabled ? "on" : "off")}, incoming default {request.DefaultIncoming}";

            return await CommitAsync(http, db, firewall, snapshot, summary, request.AcknowledgeLockout, cancellationToken);
        });

        // Re-applies the desired rules when ufw drifted (e.g. edited by hand over SSH).
        manage.MapPost("/sync", async (HttpContext http, PanelDbContext db, FirewallService firewall, CancellationToken cancellationToken) =>
            await CommitAsync(http, db, firewall, await firewall.SnapshotAsync(), "Re-applied firewall rules", false, cancellationToken));

        manage.MapPost("/changes/{id}/confirm", async (string id, HttpContext http, PanelDbContext db, FirewallService firewall,
            CancellationToken cancellationToken) =>
        {
            var record = await firewall.ConfirmAsync(id, await CurrentUser.GetAsync(http, db), cancellationToken);

            return record is null
                ? Results.NotFound()
                : Results.Ok(new { record.Id, status = record.Status.ToString().ToLowerInvariant() });
        });

        view.MapGet("/ip-lists", async (PanelDbContext db) =>
            Results.Ok(await db.IpLists.Include(l => l.Entries).OrderBy(l => l.Name)
                .Select(l => new
                {
                    l.Id,
                    l.Name,
                    l.Description,
                    entries = l.Entries.Select(e => new { e.Source, e.Note }),
                    usedBy = db.FirewallRules.Where(r => r.IpListId == l.Id).Select(r => r.Name).ToList(),
                })
                .ToListAsync()));

        manage.MapPost("/ip-lists", async (IpListRequest request, HttpContext http, PanelDbContext db, FirewallService firewall,
            CancellationToken cancellationToken) =>
        {
            if (await ValidateIpListAsync(request, db, existingId: null) is { } error)
            {
                return error;
            }

            var snapshot = await firewall.SnapshotAsync();
            var list = new IpList { Name = request.Name!.Trim() };
            ApplyIpList(list, request);
            db.IpLists.Add(list);

            return await CommitAsync(http, db, firewall, snapshot, $"Created IP list \"{list.Name}\"", request.AcknowledgeLockout, cancellationToken);
        });

        manage.MapPut("/ip-lists/{id:int}", async (int id, IpListRequest request, HttpContext http, PanelDbContext db, FirewallService firewall,
            CancellationToken cancellationToken) =>
        {
            var list = await db.IpLists.Include(l => l.Entries).FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

            if (list is null)
            {
                return Results.NotFound();
            }

            if (await ValidateIpListAsync(request, db, existingId: id) is { } error)
            {
                return error;
            }

            var snapshot = await firewall.SnapshotAsync();
            list.Name = request.Name!.Trim();
            ApplyIpList(list, request);

            // Rules using this list are re-expanded, so editing a list updates the firewall too.
            return await CommitAsync(http, db, firewall, snapshot, $"Updated IP list \"{list.Name}\"", request.AcknowledgeLockout, cancellationToken);
        });

        manage.MapDelete("/ip-lists/{id:int}", async (int id, HttpContext http, PanelDbContext db, FirewallService firewall,
            CancellationToken cancellationToken) =>
        {
            var list = await db.IpLists.FindAsync([id], cancellationToken);

            if (list is null)
            {
                return Results.NotFound();
            }

            var usedBy = await db.FirewallRules.Where(r => r.IpListId == id).Select(r => r.Name).ToListAsync(cancellationToken);

            if (usedBy.Count > 0)
            {
                return Results.Problem($"Used by: {string.Join(", ", usedBy)}. Change those rules first.", statusCode: StatusCodes.Status409Conflict);
            }

            var snapshot = await firewall.SnapshotAsync();
            db.IpLists.Remove(list);

            return await CommitAsync(http, db, firewall, snapshot, $"Deleted IP list \"{list.Name}\"", false, cancellationToken);
        });

        view.MapGet("/exposure", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            var sockets = await agent.Firewall.ListListeningSocketsAsync(new Empty(), cancellationToken: cancellationToken);
            var state = await agent.Firewall.GetFirewallStateAsync(new Empty(), cancellationToken: cancellationToken);

            return Results.Ok(ExposureAudit.Evaluate(sockets.Sockets, state).Select(i => new
            {
                i.Protocol,
                i.Address,
                i.Port,
                i.Process,
                exposure = i.Exposure.ToString().ToLowerInvariant(),
                risk = i.Risk.ToString().ToLowerInvariant(),
                i.Note,
            }));
        });

        view.MapGet("/fail2ban", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok(await agent.Firewall.GetFail2banStatusAsync(new Empty(), cancellationToken: cancellationToken)));

        manage.MapPost("/fail2ban/{jail}/{action:regex(^(ban|unban)$)}", async (string jail, string action, BanRequest request,
            HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit, CancellationToken cancellationToken) =>
        {
            var status = await agent.Firewall.Fail2banBanAsync(
                new Fail2banBanRequest { Jail = jail, Ip = request.Ip ?? "", Unban = action == "unban" }, cancellationToken: cancellationToken);

            audit.Record($"fail2ban.{action}", await CurrentUser.GetAsync(http, db), target: request.Ip, details: jail);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(status);
        });

        manage.MapPut("/fail2ban/{jail}", async (string jail, JailRequest request, HttpContext http, PanelDbContext db,
            AgentChannelFactory agent, AuditLog audit, CancellationToken cancellationToken) =>
        {
            var settings = new Fail2banJailSettings
            {
                Jail = jail,
                BanTimeSeconds = request.BanTimeSeconds,
                FindTimeSeconds = request.FindTimeSeconds,
                MaxRetry = request.MaxRetry,
            };
            settings.IgnoreIps.AddRange((request.IgnoreIps ?? []).Select(s => s.Trim()).Where(s => s.Length > 0));

            var status = await agent.Firewall.UpdateFail2banJailAsync(settings, cancellationToken: cancellationToken);

            audit.Record("fail2ban.jail_updated", await CurrentUser.GetAsync(http, db), target: jail,
                details: $"bantime={request.BanTimeSeconds}s findtime={request.FindTimeSeconds}s maxretry={request.MaxRetry}");
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(status);
        });
    }

    private static async Task<IResult> CommitAsync(HttpContext http, PanelDbContext db, FirewallService firewall, string snapshot,
        string summary, bool acknowledgeLockout, CancellationToken cancellationToken)
    {
        try
        {
            var result = await firewall.CommitAsync(snapshot, summary, await CurrentUser.GetAsync(http, db),
                http.Connection.RemoteIpAddress, acknowledgeLockout, cancellationToken);

            return Results.Ok(new
            {
                result.Changed,
                result.Reasons,
                pending = result.Pending is null
                    ? null
                    : new { result.Pending.Id, revertAt = result.Pending.RevertAt.ToUnixTimeMilliseconds(), result.Pending.Summary },
            });
        }
        catch (FirewallLockoutException e)
        {
            return Results.Json(new
            {
                title = "This change would block your own IP address.",
                detail = $"Your address ({http.Connection.RemoteIpAddress}) matches a blocking rule. Confirm to apply it anyway.",
                requiresAcknowledgement = true,
                reasons = e.Reasons,
            }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static void ApplyRequest(ManagedFirewallRule rule, RuleRequest request)
    {
        rule.Name = request.Name!.Trim();
        rule.Action = request.Action!;
        rule.Protocol = request.Protocol!;
        rule.Ports = FirewallPlanner.NormalizePorts(request.Ports!);
        rule.SourceKind = request.SourceKind;
        rule.SourceAddress = request.SourceKind == FirewallSourceKind.Address ? FirewallSource.Normalize(request.SourceAddress!) : null;
        rule.IpListId = request.SourceKind == FirewallSourceKind.IpList ? request.IpListId : null;
        rule.Enabled = request.Enabled;
    }

    private static async Task<IResult?> ValidateRuleAsync(RuleRequest request, PanelDbContext db)
    {
        var error = FirewallRuleValidator.Validate(request.Name, request.Action, request.Protocol, request.Ports, request.SourceKind, request.SourceAddress);

        if (error is null && request.SourceKind == FirewallSourceKind.IpList
            && (request.IpListId is null || !await db.IpLists.AnyAsync(l => l.Id == request.IpListId)))
        {
            error = "Choose an IP list.";
        }

        return error is null ? null : Invalid("rule", error);
    }

    private static void ApplyIpList(IpList list, IpListRequest request)
    {
        list.Description = request.Description?.Trim();
        list.Entries.Clear();
        list.Entries.AddRange((request.Entries ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Source))
            .DistinctBy(e => FirewallSource.Normalize(e.Source!))
            .Select(e => new IpListEntry { Source = FirewallSource.Normalize(e.Source!), Note = e.Note?.Trim() }));
    }

    private static async Task<IResult?> ValidateIpListAsync(IpListRequest request, PanelDbContext db, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64)
        {
            return Invalid("name", "Give the list a name (up to 64 characters).");
        }

        if (await db.IpLists.AnyAsync(l => l.Name == request.Name.Trim() && l.Id != existingId))
        {
            return Invalid("name", "A list with this name already exists.");
        }

        var invalid = (request.Entries ?? []).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Source) && !FirewallRuleValidator.IsAddress(e.Source));

        return invalid is null ? null : Invalid("entries", $"\"{invalid.Source}\" is not an IP address or network.");
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
