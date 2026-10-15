using System.Globalization;
using System.Text;

namespace TurboManager.Agent.Firewall;

/// <summary>Parses fail2ban-client's tree-style output.</summary>
public static class Fail2banParser
{
    /// <summary>"`- Jail list:	sshd, turbo-stack-panel" from `fail2ban-client status`.</summary>
    public static List<string> ParseJailList(string output)
    {
        var value = ReadField(output, "Jail list");

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Reads "|- Name:	value" from `fail2ban-client status &lt;jail&gt;`.</summary>
    public static string ReadField(string output, string name)
    {
        foreach (var line in output.Split('\n'))
        {
            var cleaned = line.TrimStart(' ', '|', '`', '-').TrimEnd('\r');

            if (cleaned.StartsWith(name + ":", StringComparison.Ordinal))
            {
                return cleaned[(name.Length + 1)..].Trim();
            }
        }

        return "";
    }

    public static int ReadInt(string output, string name) =>
        int.TryParse(ReadField(output, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static List<string> ParseBannedIps(string jailStatus) =>
        ReadField(jailStatus, "Banned IP list").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>`fail2ban-client get &lt;jail&gt; ignoreip` lists entries as tree lines after a header.</summary>
    public static List<string> ParseIgnoreIps(string output) =>
        output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.TrimStart().StartsWith("|-", StringComparison.Ordinal) || l.TrimStart().StartsWith("`-", StringComparison.Ordinal))
            .Select(l => l.TrimStart(' ', '|', '`', '-').Trim())
            .Where(l => l.Length > 0)
            .ToList();
}

/// <summary>Minimal INI editor for the jail.d file the panel owns: keeps sections and unknown keys intact.</summary>
public sealed class IniDocument
{
    private readonly List<(string Name, List<KeyValuePair<string, string>> Values)> _sections = [];

    public static IniDocument Parse(string content)
    {
        var document = new IniDocument();
        List<KeyValuePair<string, string>>? current = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = [];
                document._sections.Add((line[1..^1].Trim(), current));
            }
            else if (current is not null && line.IndexOf('=') is > 0 and var index)
            {
                current.Add(new(line[..index].Trim(), line[(index + 1)..].Trim()));
            }
        }

        return document;
    }

    public string? Get(string section, string key) =>
        _sections.FirstOrDefault(s => s.Name == section).Values?.LastOrDefault(v => v.Key == key).Value;

    public void Set(string section, string key, string value)
    {
        var values = _sections.FirstOrDefault(s => s.Name == section).Values;

        if (values is null)
        {
            values = [];
            _sections.Add((section, values));
        }

        values.RemoveAll(v => v.Key == key);
        values.Add(new(key, value));
    }

    public string Write(string headerComment)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(headerComment);

        foreach (var (name, values) in _sections)
        {
            builder.AppendLine().Append('[').Append(name).AppendLine("]");

            foreach (var (key, value) in values)
            {
                builder.Append(key).Append(" = ").AppendLine(value);
            }
        }

        return builder.ToString();
    }
}
