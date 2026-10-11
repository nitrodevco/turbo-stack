using System.Globalization;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Ssh;

public static class SshdConfig
{
    /// <summary>Parses `sshd -T` (effective config, lowercase keys).</summary>
    public static SshdSettings ParseEffective(string output)
    {
        var settings = new SshdSettings();

        foreach (var rawLine in output.Split('\n'))
        {
            var parts = rawLine.Trim().Split(' ', 2);

            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "passwordauthentication":
                    settings.PasswordAuthentication = parts[1] == "yes";
                    break;
                case "pubkeyauthentication":
                    settings.PubkeyAuthentication = parts[1] == "yes";
                    break;
                case "permitrootlogin":
                    // Older OpenSSH prints the deprecated alias.
                    settings.PermitRootLogin = parts[1] == "without-password" ? "prohibit-password" : parts[1];
                    break;
                case "port" when int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port):
                    settings.Ports.Add(port);
                    break;
            }
        }

        return settings;
    }

    /// <summary>
    /// Content of the panel's drop-in. It is named 00-* because sshd uses the first value it reads,
    /// and Ubuntu includes sshd_config.d/*.conf (e.g. 50-cloud-init.conf) in lexical order.
    /// </summary>
    public static string RenderDropIn(bool? passwordAuthentication, string? permitRootLogin, string productName)
    {
        var lines = new List<string> { $"# Managed by {productName}. Changes here are overwritten from the panel." };

        if (passwordAuthentication is { } password)
        {
            lines.Add($"PasswordAuthentication {(password ? "yes" : "no")}");
            lines.Add($"KbdInteractiveAuthentication {(password ? "yes" : "no")}");
        }

        if (!string.IsNullOrEmpty(permitRootLogin))
        {
            lines.Add($"PermitRootLogin {permitRootLogin}");
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Reads back the values the panel previously wrote to its drop-in (null when not set).</summary>
    public static (bool? PasswordAuthentication, string? PermitRootLogin) ParseDropIn(string content)
    {
        bool? password = null;
        string? root = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var parts = rawLine.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);

            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0].Equals("PasswordAuthentication", StringComparison.OrdinalIgnoreCase))
            {
                password = parts[1].Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
            else if (parts[0].Equals("PermitRootLogin", StringComparison.OrdinalIgnoreCase))
            {
                root = parts[1];
            }
        }

        return (password, root);
    }
}
