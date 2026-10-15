using System.Net;
using System.Text.RegularExpressions;
using Grpc.Core;

namespace TurboManager.Agent.Validation;

/// <summary>
/// Input validation for every agent operation. The panel validates too; this is the last line of
/// defence before a value reaches a root-run command. Failures become INVALID_ARGUMENT.
/// </summary>
public static partial class Validate
{
    public static string ServiceName(string name) =>
        ServiceNamePattern().IsMatch(name) ? name : throw Invalid($"Invalid service name: {name}");

    public static string UserName(string name) =>
        UserNamePattern().IsMatch(name) ? name : throw Invalid($"Invalid user name: {name}");

    /// <summary>A single port, a range "a:b", or a comma list of those (ufw syntax).</summary>
    public static string Ports(string ports)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Length > 100)
        {
            throw Invalid("Ports are required.");
        }

        foreach (var part in ports.Split(','))
        {
            var bounds = part.Split(':');

            if (bounds.Length > 2 || bounds.Any(b => !int.TryParse(b, out var p) || p is < 1 or > 65535))
            {
                throw Invalid($"Invalid port or range: {part}");
            }

            if (bounds.Length == 2 && int.Parse(bounds[0]) >= int.Parse(bounds[1]))
            {
                throw Invalid($"Invalid port range: {part}");
            }
        }

        return ports;
    }

    /// <summary>"any", an IP address or a CIDR network.</summary>
    public static string Source(string source)
    {
        if (source == "any")
        {
            return source;
        }

        if (IPAddress.TryParse(source, out _) || IPNetwork.TryParse(source, out _))
        {
            return source;
        }

        throw Invalid($"Invalid source address: {source}");
    }

    public static string IpAddress(string ip) =>
        IPAddress.TryParse(ip, out var address) ? address.ToString() : throw Invalid($"Invalid IP address: {ip}");

    public static string OneOf(string value, string field, params string[] allowed) =>
        allowed.Contains(value) ? value : throw Invalid($"Invalid {field}: {value}. Expected one of {string.Join(", ", allowed)}.");

    /// <summary>Firewall rule comments are ours ("ts:&lt;key&gt;"), so keep them to a tight alphabet.</summary>
    public static string RuleComment(string comment) =>
        RuleCommentPattern().IsMatch(comment) ? comment : throw Invalid($"Invalid rule comment: {comment}");

    public static string Fail2banJail(string jail) =>
        JailPattern().IsMatch(jail) ? jail : throw Invalid($"Invalid jail name: {jail}");

    public static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));

    public static RpcException FailedPrecondition(string message) => new(new Status(StatusCode.FailedPrecondition, message));

    [GeneratedRegex(@"^[A-Za-z0-9@._:\\-]{1,200}\.service$")]
    private static partial Regex ServiceNamePattern();

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex UserNamePattern();

    [GeneratedRegex("^ts:[a-z0-9._-]{1,64}$")]
    private static partial Regex RuleCommentPattern();

    [GeneratedRegex("^[a-zA-Z0-9._-]{1,64}$")]
    private static partial Regex JailPattern();
}
