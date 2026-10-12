using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Alerts;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Endpoints;

public sealed record AlertChannelRequest(string? Name, AlertChannelKind Kind, string? Target, bool Enabled = true, bool CriticalOnly = false);

public sealed record AlertSettingsRequest(int DiskPercent, int MemoryPercent, int CertificateDays, bool SecurityUpdates,
    string? SmtpHost, int SmtpPort, string? SmtpUser, string? SmtpPassword, string? SmtpFrom);

public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/alerts").RequirePermission(Permissions.SystemView);
        var manage = app.MapGroup("/api/alerts").RequirePermission(Permissions.AlertsManage);

        view.MapGet("", async (PanelDbContext db) => Results.Ok(new
        {
            open = await db.AlertEvents.Where(e => e.ResolvedAt == null).OrderByDescending(e => e.Severity).ThenByDescending(e => e.FiredAt).ToListAsync(),
            recent = await db.AlertEvents.Where(e => e.ResolvedAt != null).OrderByDescending(e => e.FiredAt).Take(50).ToListAsync(),
        }));

        manage.MapGet("/settings", async (PanelDbContext db, AlertService alerts) =>
        {
            var settings = await alerts.GetSettingsAsync();
            var channels = await db.AlertChannels.OrderBy(c => c.Name).ToListAsync();

            return Results.Ok(new
            {
                settings.DiskPercent,
                settings.MemoryPercent,
                settings.CertificateDays,
                settings.SecurityUpdates,
                settings.SmtpHost,
                settings.SmtpPort,
                settings.SmtpUser,
                smtpPasswordSet = settings.SmtpPasswordProtected is not null,
                settings.SmtpFrom,
                channels = channels.Select(c => new { c.Id, c.Name, kind = c.Kind, target = Mask(c.Kind, alerts.Unprotect(c.TargetProtected)), c.Enabled, c.CriticalOnly, c.LastError }),
            });
        });

        manage.MapPut("/settings", async (AlertSettingsRequest request, HttpContext http, PanelDbContext db, AlertService alerts, AuditLog audit) =>
        {
            if (request.DiskPercent is < 50 or > 99 || request.MemoryPercent is < 50 or > 100 || request.CertificateDays is < 1 or > 60
                || request.SmtpPort is < 1 or > 65535)
            {
                return Invalid("Disk 50-99%, memory 50-100%, certificates 1-60 days, a valid mail port.");
            }

            var settings = await alerts.GetSettingsAsync();
            settings.DiskPercent = request.DiskPercent;
            settings.MemoryPercent = request.MemoryPercent;
            settings.CertificateDays = request.CertificateDays;
            settings.SecurityUpdates = request.SecurityUpdates;
            settings.SmtpHost = Clean(request.SmtpHost);
            settings.SmtpPort = request.SmtpPort;
            settings.SmtpUser = Clean(request.SmtpUser);
            settings.SmtpFrom = Clean(request.SmtpFrom);

            // Write-only: empty keeps the stored password.
            if (!string.IsNullOrEmpty(request.SmtpPassword))
            {
                settings.SmtpPasswordProtected = alerts.Protect(request.SmtpPassword);
            }

            audit.Record("alerts.settings_updated", await CurrentUser.GetAsync(http, db));
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        manage.MapPost("/channels", async (AlertChannelRequest request, HttpContext http, PanelDbContext db, AlertService alerts, AuditLog audit) =>
        {
            if (Validate(request) is { } error)
            {
                return Invalid(error);
            }

            var channel = new AlertChannel
            {
                Name = request.Name!.Trim(),
                Kind = request.Kind,
                TargetProtected = alerts.Protect(request.Target!.Trim()),
                Enabled = request.Enabled,
                CriticalOnly = request.CriticalOnly,
            };
            db.AlertChannels.Add(channel);
            audit.Record("alerts.channel_added", await CurrentUser.GetAsync(http, db), target: channel.Name, details: channel.Kind.ToString());
            await db.SaveChangesAsync();

            return Results.Ok(new { channel.Id });
        });

        manage.MapPut("/channels/{id:int}", async (int id, AlertChannelRequest request, HttpContext http, PanelDbContext db, AlertService alerts, AuditLog audit) =>
        {
            var channel = await db.AlertChannels.FindAsync(id);

            if (channel is null)
            {
                return Results.NotFound();
            }

            // The target may be left out (it's shown masked): then the stored one stays.
            var keepTarget = string.IsNullOrWhiteSpace(request.Target);

            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 60)
            {
                return Invalid("Give the channel a name.");
            }

            if (!keepTarget && Validate(request) is { } error)
            {
                return Invalid(error);
            }

            channel.Name = request.Name!.Trim();
            channel.Enabled = request.Enabled;
            channel.CriticalOnly = request.CriticalOnly;

            if (!keepTarget)
            {
                channel.Kind = request.Kind;
                channel.TargetProtected = alerts.Protect(request.Target!.Trim());
            }

            audit.Record("alerts.channel_updated", await CurrentUser.GetAsync(http, db), target: channel.Name);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        manage.MapDelete("/channels/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var channel = await db.AlertChannels.FindAsync(id);

            if (channel is null)
            {
                return Results.NotFound();
            }

            db.AlertChannels.Remove(channel);
            audit.Record("alerts.channel_removed", await CurrentUser.GetAsync(http, db), target: channel.Name);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        manage.MapPost("/channels/{id:int}/test", async (int id, PanelDbContext db, AlertService alerts, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var channel = await db.AlertChannels.FindAsync([id], cancellationToken);

            if (channel is null)
            {
                return Results.NotFound();
            }

            var error = await alerts.SendAsync(channel, new AlertEvent
            {
                Key = "test",
                Severity = AlertSeverity.Warning,
                Title = "Test alert",
                Detail = "If you can read this, alerts reach you here.",
                FiredAt = time.GetUtcNow(),
            }, firing: true, cancellationToken);
            channel.LastError = error;
            await db.SaveChangesAsync(cancellationToken);

            return error is null ? Results.NoContent() : Results.Problem($"Sending failed: {error}", statusCode: StatusCodes.Status502BadGateway);
        });

        // Checks right away instead of waiting for the next minute.
        manage.MapPost("/check", async (AlertService alerts, CancellationToken cancellationToken) =>
        {
            await alerts.EvaluateAsync(cancellationToken);

            return Results.NoContent();
        });
    }

    private static string? Validate(AlertChannelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 60)
        {
            return "Give the channel a name.";
        }

        var target = (request.Target ?? "").Trim();

        return request.Kind switch
        {
            AlertChannelKind.Discord when !target.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal)
                && !target.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.Ordinal)
                => "Paste the Discord webhook URL (Server settings → Integrations → Webhooks).",
            AlertChannelKind.Webhook when !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                => "Enter the webhook's address (https://…).",
            AlertChannelKind.Email when !System.Net.Mail.MailAddress.TryCreate(target, out _) => "Enter an email address.",
            _ => null,
        };
    }

    /// <summary>Webhook URLs carry their secret in the path: show only the host.</summary>
    private static string Mask(AlertChannelKind kind, string target) =>
        kind == AlertChannelKind.Email ? target : Uri.TryCreate(target, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/…" : "…";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]> { ["alerts"] = [message] });
}
