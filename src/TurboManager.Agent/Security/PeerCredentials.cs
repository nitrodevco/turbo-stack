using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TurboManager.Agent.Security;

public readonly record struct PeerCredentials(int Pid, int Uid, int Gid)
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>Reads the connecting process' credentials from a unix socket (Linux SO_PEERCRED).</summary>
    public static PeerCredentials? FromSocket(Socket socket)
    {
        if (!OperatingSystem.IsLinux() || socket.AddressFamily != AddressFamily.Unix)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[12];
        var length = socket.GetRawSocketOption(SolSocket, SoPeerCred, buffer);

        if (length < 12)
        {
            return null;
        }

        var values = MemoryMarshal.Cast<byte, int>(buffer);

        return new PeerCredentials(values[0], values[1], values[2]);
    }
}
