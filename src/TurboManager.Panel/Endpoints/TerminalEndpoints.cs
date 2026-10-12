using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Endpoints;

/// <summary>
/// The web terminal: a WebSocket bridged to the agent's pseudo-terminal. Binary frames carry keystrokes and output;
/// text frames carry {"resize":{"cols":…,"rows":…}} from the browser and {"exit":code} at the end.
/// </summary>
public static class TerminalEndpoints
{
    public static void MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        // Who may open a terminal as whom (the browser lists these).
        app.MapGet("/api/terminal/users", async (HttpContext http, PanelDbContext db, AccessScope scope) =>
        {
            var users = await scope.SystemUsersAsync(Permissions.TerminalApp);

            return Results.Ok(new { users, root = Permissions.Has(http.User, Permissions.TerminalRoot) });
        }).RequireAuthorization(SessionOnly);

        // A WebSocket can't show a "confirm it's you" answer: the browser asks this first, which can.
        app.MapPost("/api/terminal/root-check", () => Results.NoContent()).RequirePermission(Permissions.TerminalRoot).RequireSudo();

        app.Map("/api/terminal", async (string? user, int? cols, int? rows, HttpContext http, PanelDbContext db, AccessScope scope,
            AgentChannelFactory agent, AuditLog audit, TimeProvider time) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest();
            }

            // A WebSocket isn't covered by the CSRF header check: only pages of the panel itself may open one.
            if (!SameOrigin(http))
            {
                return Results.Problem("Cross-site terminal requests aren't allowed.", statusCode: StatusCodes.Status403Forbidden);
            }

            var actor = await CurrentUser.GetAsync(http, db);
            user = (user ?? "").Trim();

            if (user == "root")
            {
                var session = await SessionManager.GetSessionAsync(http, db);

                if (!Permissions.Has(http.User, Permissions.TerminalRoot))
                {
                    return AccessScope.Denied();
                }

                if (!(session?.SudoUntil > time.GetUtcNow()))
                {
                    return SudoMode.Required();
                }
            }
            else if (!(await scope.SystemUsersAsync(Permissions.TerminalApp)).Contains(user))
            {
                return AccessScope.Denied();
            }

            audit.Record("terminal.opened", actor, target: user);
            await db.SaveChangesAsync();

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await BridgeAsync(socket, agent, user, cols, rows, http.RequestAborted);

            return Results.Empty;
        }).RequireAuthorization(SessionOnly);
    }

    private static void SessionOnly(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy) =>
        policy.AddAuthenticationSchemes(AuthSchemes.Session).RequireAuthenticatedUser();


    /// <summary>
    /// The page that opened the socket is the panel itself. Host names are compared without ports: nginx forwards
    /// "Host: $host", which drops the port of the setup address (https://&lt;ip&gt;:8443).
    /// </summary>
    private static bool SameOrigin(HttpContext http)
    {
        var origin = http.Request.Headers.Origin.ToString();

        return origin.Length > 0 && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task BridgeAsync(WebSocket socket, AgentChannelFactory agent, string user, int? cols, int? rows, CancellationToken cancellationToken)
    {
        using var call = agent.Terminal.OpenTerminal(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new TerminalInput
        {
            Open = new TerminalOpen { User = user, Size = new TerminalSize { Cols = cols ?? 80, Rows = rows ?? 24 } },
        }, cancellationToken);

        var toAgent = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFrom(buffer, 0, result.Count) }, cancellationToken);
                    }
                    else if (TryReadResize(buffer.AsSpan(0, result.Count)) is { } size)
                    {
                        await call.RequestStream.WriteAsync(new TerminalInput { Resize = size }, cancellationToken);
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or RpcException)
            {
            }
            finally
            {
                await call.RequestStream.CompleteAsync();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var output in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                if (output.KindCase == TerminalOutput.KindOneofCase.Data)
                {
                    await socket.SendAsync(output.Data.Memory, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
                }
                else if (output.KindCase == TerminalOutput.KindOneofCase.ExitCode)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes($"{{\"exit\":{output.ExitCode}}}"), WebSocketMessageType.Text, true, cancellationToken);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or RpcException)
        {
        }

        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
        }

        await toAgent;
    }

    private static TerminalSize? TryReadResize(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());

            return document.RootElement.TryGetProperty("resize", out var resize)
                ? new TerminalSize { Cols = resize.GetProperty("cols").GetInt32(), Rows = resize.GetProperty("rows").GetInt32() }
                : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
