using Google.Protobuf.WellKnownTypes;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Endpoints;

/// <summary>SSH keys and sshd settings.</summary>
public static class AccessEndpoints
{
    public sealed record AddKeyRequest(string? PublicKey);

    public sealed record SshdRequest(bool? PasswordAuthentication, string? PermitRootLogin);

    public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/ssh").WithAgentErrors().RequirePermission(Permissions.SshView);
        var manage = app.MapGroup("/api/ssh").WithAgentErrors().RequirePermission(Permissions.SshManage);

        view.MapGet("/users", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok((await agent.Ssh.ListLoginUsersAsync(new Empty(), cancellationToken: cancellationToken)).Users));

        view.MapGet("/users/{user}/keys", async (string user, AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok((await agent.Ssh.ListAuthorizedKeysAsync(new SshUserRef { User = user }, cancellationToken: cancellationToken)).Keys));

        manage.MapPost("/users/{user}/keys", async (string user, AddKeyRequest request, HttpContext http, PanelDbContext db,
            AgentChannelFactory agent, AuditLog audit, CancellationToken cancellationToken) =>
        {
            var reply = await agent.Ssh.AddAuthorizedKeyAsync(new AddAuthorizedKeyRequest { User = user, PublicKey = request.PublicKey ?? "" },
                cancellationToken: cancellationToken);
            var added = reply.Keys.LastOrDefault();

            audit.Record("ssh.key_added", await CurrentUser.GetAsync(http, db), target: user, details: $"{added?.Fingerprint} {added?.Comment}");
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(reply.Keys);
        });

        manage.MapDelete("/users/{user}/keys", async (string user, string fingerprint, HttpContext http, PanelDbContext db,
            AgentChannelFactory agent, AuditLog audit, CancellationToken cancellationToken) =>
        {
            var reply = await agent.Ssh.RemoveAuthorizedKeyAsync(new RemoveAuthorizedKeyRequest { User = user, Fingerprint = fingerprint },
                cancellationToken: cancellationToken);

            audit.Record("ssh.key_removed", await CurrentUser.GetAsync(http, db), target: user, details: fingerprint);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(reply.Keys);
        });

        view.MapGet("/settings", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok(await agent.Ssh.GetSshdSettingsAsync(new Empty(), cancellationToken: cancellationToken)));

        manage.MapPut("/settings", async (SshdRequest request, HttpContext http, PanelDbContext db, AgentChannelFactory agent,
            AuditLog audit, CancellationToken cancellationToken) =>
        {
            var update = new SshdSettingsUpdate { PermitRootLogin = request.PermitRootLogin ?? "" };

            if (request.PasswordAuthentication is { } password)
            {
                update.PasswordAuthentication = password;
            }

            var settings = await agent.Ssh.UpdateSshdSettingsAsync(update, cancellationToken: cancellationToken);

            audit.Record("ssh.settings_updated", await CurrentUser.GetAsync(http, db),
                details: $"password login {(settings.PasswordAuthentication ? "on" : "off")}, root login {settings.PermitRootLogin}");
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(settings);
        });
    }
}
