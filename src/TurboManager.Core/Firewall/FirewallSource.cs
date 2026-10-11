using System.Net;
using System.Net.Sockets;

namespace TurboManager.Core.Firewall;

/// <summary>Canonical form of a firewall source, shared by the panel (desired rules) and agent (ufw state).</summary>
public static class FirewallSource
{
    public const string Any = "any";

    /// <summary>"0.0.0.0/0" and "::/0" become "any"; host networks lose /32 or /128; networks are canonicalised.</summary>
    public static string Normalize(string source)
    {
        source = source.Trim();

        if (source is "" or "0.0.0.0/0" or "::/0" or Any)
        {
            return Any;
        }

        if (IPNetwork.TryParse(source, out var network))
        {
            var hostBits = network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;

            return network.PrefixLength == hostBits ? network.BaseAddress.ToString() : network.ToString();
        }

        return IPAddress.TryParse(source, out var address) ? address.ToString() : source;
    }

    public static bool IsValid(string source) =>
        Normalize(source) == Any || IPAddress.TryParse(source.Trim(), out _) || IPNetwork.TryParse(source.Trim(), out _);

    /// <summary>True when <paramref name="source"/> ("any", an address or a network) covers <paramref name="address"/>.</summary>
    public static bool Contains(string source, IPAddress address)
    {
        var normalized = Normalize(source);

        if (normalized == Any)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.TryParse(normalized, out var single))
        {
            return single.Equals(address);
        }

        return IPNetwork.TryParse(normalized, out var network) && network.Contains(address);
    }
}
