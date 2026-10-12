using System.Text;
using Google.Protobuf;
using Grpc.Core;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Endpoints;

public sealed record FileWrite(string? User, string? Path, string? Content);

public sealed record FileMoveRequest(string? User, string? From, string? To);

/// <summary>The file manager: app users' home folders, as those users (the agent runs everything with runuser).</summary>
public static class FilesEndpoints
{
    public static void MapFilesEndpoints(this IEndpointRouteBuilder app)
    {
        var files = app.MapGroup("/api/files").WithAgentErrors().RequirePermission(Permissions.FilesManage)
            // Only the users of apps this person can access; ?user= (or the body's user, checked in the handler).
            .AddEndpointFilter(async (context, next) =>
            {
                var user = context.HttpContext.Request.Query["user"].ToString();

                if (user.Length == 0)
                {
                    return await next(context);
                }

                var allowed = await context.HttpContext.RequestServices.GetRequiredService<AccessScope>().SystemUsersAsync(Permissions.FilesManage);

                return allowed.Contains(user) ? await next(context) : AccessScope.Denied();
            });

        files.MapGet("/users", async (AccessScope scope) => Results.Ok(await scope.SystemUsersAsync(Permissions.FilesManage)));

        files.MapGet("", async (string user, string? path, AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            var listing = await agent.Files.ListDirectoryAsync(new FilePath { User = user, Path = path ?? "" }, cancellationToken: cancellationToken);

            return Results.Ok(new
            {
                listing.Path,
                entries = listing.Entries.OrderByDescending(e => e.Type == "d").ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new { e.Name, e.Type, e.Size, e.Mode, modified = e.ModifiedUnixMs }),
            });
        });

        files.MapGet("/content", async (string user, string path, AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            var file = await agent.Files.ReadFileAsync(new FilePath { User = user, Path = path }, cancellationToken: cancellationToken);
            var bytes = file.Content.ToByteArray();

            // Shown in the editor only if it's text.
            try
            {
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                {
                    throw new DecoderFallbackException();
                }

                return Results.Ok(new { file.Path, content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), binary = false, size = bytes.Length });
            }
            catch (DecoderFallbackException)
            {
                return Results.Ok(new { file.Path, content = (string?)null, binary = true, size = bytes.Length });
            }
        });

        files.MapPut("/content", async (FileWrite request, HttpContext http, PanelDbContext db, AccessScope scope, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (!await AllowedAsync(scope, request.User))
            {
                return AccessScope.Denied();
            }

            await agent.Files.WriteFileAsync(new FileContent { User = request.User, Path = request.Path ?? "", Content = ByteString.CopyFromUtf8(request.Content ?? "") },
                cancellationToken: cancellationToken);
            audit.Record("files.written", await CurrentUser.GetAsync(http, db), target: $"{request.User}:{request.Path}");
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        // The raw request body is the file (streamed through to the agent).
        files.MapPost("/upload", async (string user, string path, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            using var call = agent.Files.UploadFile(cancellationToken: cancellationToken);
            await call.RequestStream.WriteAsync(new FileChunk { Name = $"{user}:{path}" }, cancellationToken);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;

            while ((read = await http.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                await call.RequestStream.WriteAsync(new FileChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, cancellationToken);
            }

            await call.RequestStream.CompleteAsync();
            await call;
            audit.Record("files.uploaded", await CurrentUser.GetAsync(http, db), target: $"{user}:{path}", details: $"{total} bytes");
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(512L * 1024 * 1024));

        files.MapGet("/download", async (string user, string path, HttpContext http, AgentChannelFactory agent) =>
        {
            using var call = agent.Files.DownloadFile(new FilePath { User = user, Path = path }, cancellationToken: http.RequestAborted);
            var started = false;

            await foreach (var chunk in call.ResponseStream.ReadAllAsync(http.RequestAborted))
            {
                if (!started)
                {
                    http.Response.ContentType = "application/octet-stream";
                    http.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(path).Replace("\"", "", StringComparison.Ordinal)}\"";
                    started = true;
                }

                await http.Response.Body.WriteAsync(chunk.Data.Memory, http.RequestAborted);
            }

            if (!started)
            {
                http.Response.ContentType = "application/octet-stream";
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(path).Replace("\"", "", StringComparison.Ordinal)}\"";
            }

            return Results.Empty;
        });

        files.MapDelete("", async (string user, string path, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            await agent.Files.DeletePathAsync(new FilePath { User = user, Path = path }, cancellationToken: cancellationToken);
            audit.Record("files.deleted", await CurrentUser.GetAsync(http, db), target: $"{user}:{path}");
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        files.MapPost("/folder", async (FileWrite request, AccessScope scope, AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            if (!await AllowedAsync(scope, request.User))
            {
                return AccessScope.Denied();
            }

            await agent.Files.MakeDirectoryAsync(new FilePath { User = request.User, Path = request.Path ?? "" }, cancellationToken: cancellationToken);

            return Results.NoContent();
        });

        files.MapPost("/move", async (FileMoveRequest request, HttpContext http, PanelDbContext db, AccessScope scope, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (!await AllowedAsync(scope, request.User))
            {
                return AccessScope.Denied();
            }

            await agent.Files.MovePathAsync(new FileMove { User = request.User, From = request.From ?? "", To = request.To ?? "" }, cancellationToken: cancellationToken);
            audit.Record("files.moved", await CurrentUser.GetAsync(http, db), target: $"{request.User}:{request.From}", details: $"to {request.To}");
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });
    }

    private static async Task<bool> AllowedAsync(AccessScope scope, string? user) =>
        !string.IsNullOrEmpty(user) && (await scope.SystemUsersAsync(Permissions.FilesManage)).Contains(user);
}
