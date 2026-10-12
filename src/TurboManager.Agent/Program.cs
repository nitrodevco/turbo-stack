using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Databases;
using TurboManager.Agent.Deploy;
using TurboManager.Agent.Firewall;
using TurboManager.Agent.HostInfo;
using TurboManager.Agent.Journal;
using TurboManager.Agent.Metrics;
using TurboManager.Agent.Nginx;
using TurboManager.Agent.Packages;
using TurboManager.Agent.Security;
using TurboManager.Agent.Ssh;
using TurboManager.Agent.Systemd;
using TurboManager.Core.Unix;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Host.UseSystemd();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton<PeerAuthorizer>();
builder.Services.AddSingleton<SystemInfoReader>();
builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<UfwFiles>();
builder.Services.AddSingleton<FirewallManager>();
builder.Services.AddSingleton<Fail2banManager>();
builder.Services.AddSingleton<NginxManager>();
builder.Services.AddSingleton<DatabaseManager>();
builder.Services.AddSingleton<DeployManager>();
builder.Services.AddSingleton<TurboManager.Agent.Files.FilesManager>();
builder.Services.AddSingleton<TurboManager.Agent.Ssh.SftpManager>();
builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddHostedService(services => services.GetRequiredService<MetricsSampler>());
builder.Services.AddGrpc(grpc => grpc.EnableDetailedErrors = false);

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
app.MapGrpcService<ServicesAgentService>();
app.MapGrpcService<LogsAgentService>();
app.MapGrpcService<MetricsAgentService>();
app.MapGrpcService<FirewallAgentService>();
app.MapGrpcService<SshAgentService>();
app.MapGrpcService<PackagesAgentService>();
app.MapGrpcService<NginxAgentService>();
app.MapGrpcService<DatabaseAgentService>();
app.MapGrpcService<DeployAgentService>();
app.MapGrpcService<TurboManager.Agent.Terminal.TerminalAgentService>();
app.MapGrpcService<TurboManager.Agent.Files.FilesAgentService>();
app.MapGrpcService<TurboManager.Agent.Ssh.SftpAgentService>();

try
{
    await app.Services.GetRequiredService<FirewallManager>().RecoverAsync(CancellationToken.None);
}
catch (Exception e)
{
    app.Logger.LogError(e, "Could not revert a pending firewall change from before the restart");
}

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
