using System.Text;
using TurboManager.Contracts.Agent;
using TurboManager.Core.Firewall;

namespace TurboManager.Agent.Firewall;

/// <summary>Reads ufw's own rule files instead of scraping `ufw status` text.</summary>
public static class UfwParser
{
    private const string TupleMarker = "### tuple ###";

    /// <summary>
    /// Parses "### tuple ###" lines from /etc/ufw/user.rules or user6.rules. Format (from ufw's backend):
    /// action proto dport dst sport src [dapp sapp] direction [comment=&lt;hex&gt;]
    /// </summary>
    public static List<FirewallRule> ParseRulesFile(string content, bool ipv6)
    {
        var rules = new List<FirewallRule>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            if (!line.StartsWith(TupleMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line[TupleMarker.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var comment = "";

            if (tokens.Count > 0 && tokens[^1].StartsWith("comment=", StringComparison.Ordinal))
            {
                comment = DecodeHex(tokens[^1]["comment=".Length..]);
                tokens.RemoveAt(tokens.Count - 1);
            }

            if (tokens.Count is not (7 or 9))
            {
                continue;
            }

            rules.Add(new FirewallRule
            {
                // "allow_log" / "allow_log-all" carry the logging mode after an underscore.
                Action = tokens[0].Split('_')[0],
                Protocol = tokens[1],
                Ports = tokens[2],
                Source = NormalizeSource(tokens[5]),
                Direction = tokens[^1].Split('_')[0],
                Ipv6 = ipv6,
                Comment = comment,
            });
        }

        return rules;
    }

    public static string NormalizeSource(string source) => FirewallSource.Normalize(source);

    /// <summary>Reads DEFAULT_INPUT_POLICY / DEFAULT_OUTPUT_POLICY from /etc/default/ufw.</summary>
    public static (string Incoming, string Outgoing) ParseDefaults(string defaultsFile)
    {
        string Read(string key)
        {
            var line = defaultsFile.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
            var value = line?.Split('=', 2)[1].Trim().Trim('"') ?? "";

            return value switch
            {
                "ACCEPT" => "allow",
                "DROP" => "deny",
                "REJECT" => "reject",
                _ => value.ToLowerInvariant(),
            };
        }

        return (Read("DEFAULT_INPUT_POLICY"), Read("DEFAULT_OUTPUT_POLICY"));
    }

    public static bool ParseStatusActive(string statusOutput) =>
        statusOutput.Split('\n').Any(l => l.Trim().Equals("Status: active", StringComparison.OrdinalIgnoreCase));

    private static string DecodeHex(string hex)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return hex;
        }
    }
}
