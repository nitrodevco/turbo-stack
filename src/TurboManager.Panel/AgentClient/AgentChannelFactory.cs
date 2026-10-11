using System.Net.Sockets;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using TurboManager.Contracts.Agent;

namespace TurboManager.Panel.AgentClient;

/// <summary>gRPC channel to the root agent over its unix socket.</summary>
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
        };

        return GrpcChannel.ForAddress("http://agent", new GrpcChannelOptions { HttpHandler = handler });
    });

    public SystemAgent.SystemAgentClient System => new(_channel.Value);

    public void Dispose()
    {
        if (_channel.IsValueCreated)
        {
            _channel.Value.Dispose();
        }
    }
}
