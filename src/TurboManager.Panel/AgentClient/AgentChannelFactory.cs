using System.Net.Sockets;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using TurboManager.Contracts.Agent;

namespace TurboManager.Panel.AgentClient;

/// <summary>gRPC clients for the root agent over its unix socket (one shared HTTP/2 channel).</summary>
public sealed class AgentChannelFactory(IOptions<PanelOptions> options) : IDisposable
{
    private readonly Lazy<GrpcChannel> _channel = new(() =>
    {
        var socketPath = Path.GetFullPath(options.Value.AgentSocket);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            // Live log tails hold long-running streams; allow several at once.
            EnableMultipleHttp2Connections = true,
        };

        return GrpcChannel.ForAddress("http://agent", new GrpcChannelOptions { HttpHandler = handler, MaxReceiveMessageSize = 32 * 1024 * 1024 });
    });

    public SystemAgent.SystemAgentClient System => new(_channel.Value);

    public ServicesAgent.ServicesAgentClient Services => new(_channel.Value);

    public LogsAgent.LogsAgentClient Logs => new(_channel.Value);

    public TerminalAgent.TerminalAgentClient Terminal => new(_channel.Value);

    public FilesAgent.FilesAgentClient Files => new(_channel.Value);

    public SftpAgent.SftpAgentClient Sftp => new(_channel.Value);

    public MetricsAgent.MetricsAgentClient Metrics => new(_channel.Value);

    public FirewallAgent.FirewallAgentClient Firewall => new(_channel.Value);

    public SshAgent.SshAgentClient Ssh => new(_channel.Value);

    public PackagesAgent.PackagesAgentClient Packages => new(_channel.Value);

    public NginxAgent.NginxAgentClient Nginx => new(_channel.Value);

    public DatabaseAgent.DatabaseAgentClient Databases => new(_channel.Value);

    public DeployAgent.DeployAgentClient Deploy => new(_channel.Value);

    public void Dispose()
    {
        if (_channel.IsValueCreated)
        {
            _channel.Value.Dispose();
        }
    }
}
