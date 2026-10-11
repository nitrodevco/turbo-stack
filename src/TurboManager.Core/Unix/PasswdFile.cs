namespace TurboManager.Core.Unix;

public sealed record PasswdEntry(string Name, int Uid, int Gid, string Home, string Shell);

/// <summary>Minimal reader for /etc/passwd and /etc/group (no NSS; managed servers use local accounts).</summary>
public static class PasswdFile
{
    public static PasswdEntry? FindUser(string name, string path = "/etc/passwd")
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var entry = ParseLine(line);

            if (entry?.Name == name)
            {
                return entry;
            }
        }

        return null;
    }

    public static int? FindGroupId(string name, string path = "/etc/group")
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(':');

            if (parts.Length >= 3 && parts[0] == name && int.TryParse(parts[2], out var gid))
            {
                return gid;
            }
        }

        return null;
    }

    public static PasswdEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            return null;
        }

        var parts = line.Split(':');

        if (parts.Length < 7 || !int.TryParse(parts[2], out var uid) || !int.TryParse(parts[3], out var gid))
        {
            return null;
        }

        return new PasswdEntry(parts[0], uid, gid, parts[5], parts[6]);
    }
}
