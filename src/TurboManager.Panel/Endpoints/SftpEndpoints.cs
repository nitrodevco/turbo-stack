using Google.Protobuf.WellKnownTypes;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Databases;

namespace TurboManager.Panel.Endpoints;

public sealed record SftpCreateRequest(string? Name, string? User, string? Path, bool Password = true);

public sealed record SftpKeysRequest(List<string>? Keys);

/// <summary>SFTP-only accounts locked into one folder (e.g. for uploading game assets).</summary>
public static class SftpEndpoints
{
    public static void MapSftpEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/sftp").WithAgentErrors().RequirePermission(Permissions.SshView);
        var manage = app.MapGroup("/api/sftp").WithAgentErrors().RequirePermission(Permissions.SshManage);

        view.MapGet("", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok((await agent.Sftp.ListSftpAccountsAsync(new Empty(), cancellationToken: cancellationToken)).Accounts));

        manage.MapPost("", async (SftpCreateRequest request, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            await agent.Sftp.CreateSftpAccountAsync(new SftpAccountRequest { Name = request.Name ?? "", AppUser = request.User ?? "", Path = request.Path ?? "" },
                cancellationToken: cancellationToken);
            string? password = null;

            if (request.Password)
            {
                password = DatabaseService.GeneratePassword();
                await agent.Sftp.SetSftpPasswordAsync(new SftpCredentials { Name = request.Name, Password = password }, cancellationToken: cancellationToken);
            }

            audit.Record("sftp.created", await CurrentUser.GetAsync(http, db), target: request.Name, details: $"{request.User}:{request.Path}");
            await db.SaveChangesAsync(cancellationToken);

            // Shown once; the panel doesn't keep it.
            return Results.Ok(new { password });
        });

        manage.MapPost("/{name}/password", async (string name, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var password = DatabaseService.GeneratePassword();
            await agent.Sftp.SetSftpPasswordAsync(new SftpCredentials { Name = name, Password = password }, cancellationToken: cancellationToken);
            audit.Record("sftp.password_reset", await CurrentUser.GetAsync(http, db), target: name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new { password });
        });

        manage.MapDelete("/{name}/password", async (string name, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            await agent.Sftp.SetSftpPasswordAsync(new SftpCredentials { Name = name, Password = "" }, cancellationToken: cancellationToken);
            audit.Record("sftp.password_disabled", await CurrentUser.GetAsync(http, db), target: name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        manage.MapPut("/{name}/keys", async (string name, SftpKeysRequest request, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var credentials = new SftpCredentials { Name = name };
            credentials.PublicKeys.AddRange(request.Keys ?? []);
            await agent.Sftp.SetSftpKeysAsync(credentials, cancellationToken: cancellationToken);
            audit.Record("sftp.keys_set", await CurrentUser.GetAsync(http, db), target: name, details: $"{credentials.PublicKeys.Count} keys");
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        manage.MapDelete("/{name}", async (string name, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            await agent.Sftp.DeleteSftpAccountAsync(new SftpAccountRef { Name = name }, cancellationToken: cancellationToken);
            audit.Record("sftp.deleted", await CurrentUser.GetAsync(http, db), target: name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });
    }
}
