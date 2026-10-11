using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent;
using TurboManager.Agent.Security;
using TurboManager.Agent.Services;
using TurboManager.Agent.HostInfo;
using TurboManager.Core.Unix;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Host.UseSystemd();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton<PeerAuthorizer>();
builder.Services.AddSingleton<SystemInfoReader>();
builder.Services.AddGrpc();

var agentOptions = builder.Configuration.GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();
var socketPath = Path.GetFullPath(agentOptions.SocketPath);

Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

if (File.Exists(socketPath))
{
    File.Delete(socketPath);
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenUnixSocket(socketPath, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;

        // Reject unauthorized peers at connection level, before any request is parsed.
        listen.Use(next => async connection =>
        {
            var socket = connection.Features.Get<IConnectionSocketFeature>()?.Socket;
            var peer = socket is null ? null : PeerCredentials.FromSocket(socket);
            var authorizer = kestrel.ApplicationServices.GetRequiredService<PeerAuthorizer>();

            if (!authorizer.IsAllowed(peer))
            {
                connection.Abort();

                return;
            }

            await next(connection);
        });
    });
});

var app = builder.Build();

app.MapGrpcService<SystemAgentService>();

app.Lifetime.ApplicationStarted.Register(() => SecureSocket(socketPath, app.Services.GetRequiredService<IOptions<AgentOptions>>().Value, app.Logger));

app.Run();

static void SecureSocket(string socketPath, AgentOptions options, ILogger logger)
{
    logger.LogInformation("Agent listening on {Socket}", socketPath);

    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

    var gid = PasswdFile.FindGroupId(options.SocketGroup, Path.Combine(options.HostRoot, "etc/group"));

    if (gid is null)
    {
        logger.LogWarning("Group {Group} not found; agent socket is root-only", options.SocketGroup);

        return;
    }

    UnixFileOwnership.SetGroup(socketPath, gid.Value);
}
