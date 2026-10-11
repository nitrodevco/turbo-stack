using System.Globalization;
using System.Text;
using System.Text.Json;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Journal;

public static class JournalParser
{
    /// <summary>Parses one line of `journalctl -o json`. Returns null for lines that aren't journal objects.</summary>
    public static JournalEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            return new JournalEntry
            {
                TimestampUsec = ReadLong(root, "__REALTIME_TIMESTAMP"),
                Unit = ReadString(root, "_SYSTEMD_UNIT") ?? ReadString(root, "UNIT") ?? "",
                Identifier = ReadString(root, "SYSLOG_IDENTIFIER") ?? ReadString(root, "_COMM") ?? "",
                Priority = (int)(root.TryGetProperty("PRIORITY", out _) ? ReadLong(root, "PRIORITY") : 6),
                Message = ReadString(root, "MESSAGE") ?? "",
                Pid = (int)ReadLong(root, "_PID"),
                Cursor = ReadString(root, "__CURSOR") ?? "",
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Builds journalctl arguments for a query. Inputs are validated by the caller.</summary>
    public static List<string> BuildArguments(JournalQuery query, bool follow)
    {
        var limit = query.Limit is > 0 and <= 5000 ? query.Limit : 200;
        List<string> arguments = ["--no-pager", "--output=json", $"--lines={limit}"];

        if (follow)
        {
            arguments.Add("--follow");
        }

        if (!string.IsNullOrEmpty(query.Unit))
        {
            arguments.Add($"--unit={query.Unit}");
        }

        if (query.HasMaxPriority)
        {
            arguments.Add($"--priority=0..{Math.Clamp(query.MaxPriority, 0, 7)}");
        }

        if (query.SinceUnixMs > 0)
        {
            arguments.Add($"--since=@{query.SinceUnixMs / 1000}");
        }

        if (query.UntilUnixMs > 0)
        {
            arguments.Add($"--until=@{query.UntilUnixMs / 1000}");
        }

        if (!string.IsNullOrEmpty(query.Grep))
        {
            arguments.Add("--case-sensitive=false");
            arguments.Add($"--grep={query.Grep}");
        }

        return arguments;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            // journald emits non-UTF-8 or binary fields as byte arrays.
            JsonValueKind.Array => Encoding.UTF8.GetString(value.EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray()),
            _ => null,
        };
    }

    private static long ReadLong(JsonElement root, string name) =>
        long.TryParse(ReadString(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
