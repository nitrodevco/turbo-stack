using System.Globalization;
using System.Text.RegularExpressions;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Firewall;

/// <summary>Parses `ss -H -tulpn`: listening TCP and bound UDP sockets with their processes.</summary>
public static partial class SsParser
{
    public static List<ListeningSocket> Parse(string output)
    {
        var sockets = new List<ListeningSocket>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Netid State Recv-Q Send-Q Local:Port Peer:Port [Process]
            var fields = rawLine.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length < 6 || fields[0] is not ("tcp" or "udp"))
            {
                continue;
            }

            var (address, port) = SplitEndpoint(fields[4]);

            if (port <= 0)
            {
                continue;
            }

            var process = fields.Length > 6 ? ProcessPattern().Match(fields[6]) : Match.Empty;
            var socket = new ListeningSocket
            {
                Protocol = fields[0],
                Address = address,
                Port = port,
                Process = process.Success ? process.Groups["name"].Value : "",
                Pid = process.Success ? int.Parse(process.Groups["pid"].Value, CultureInfo.InvariantCulture) : 0,
            };

            // ss lists one line per socket; the same port can appear for v4 and v6 or several workers.
            if (!sockets.Any(s => s.Protocol == socket.Protocol && s.Address == socket.Address && s.Port == socket.Port))
            {
                sockets.Add(socket);
            }
        }

        return sockets;
    }

    /// <summary>"0.0.0.0:80", "[::]:22", "127.0.0.53%lo:53", "*:9001", "[::ffff:127.0.0.1]:7800".</summary>
    public static (string Address, int Port) SplitEndpoint(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');

        if (colon < 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return (endpoint, 0);
        }

        var address = endpoint[..colon].Trim('[', ']');
        var percent = address.IndexOf('%');

        if (percent >= 0)
        {
            address = address[..percent];
        }

        if (address.StartsWith("::ffff:", StringComparison.Ordinal))
        {
            address = address["::ffff:".Length..];
        }

        return (address == "*" ? "0.0.0.0" : address, port);
    }

    [GeneratedRegex("""\(\("(?<name>[^"]+)",pid=(?<pid>\d+)""")]
    private static partial Regex ProcessPattern();
}
