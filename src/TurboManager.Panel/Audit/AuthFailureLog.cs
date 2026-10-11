using System.Globalization;
using Microsoft.Extensions.Options;

namespace TurboManager.Panel.Audit;

/// <summary>
/// Appends failed sign-in attempts to a log that fail2ban's panel jail watches
/// (filter: "AUTH_FAILURE ip=&lt;HOST&gt;"), so brute-force sources get banned.
/// </summary>
public sealed class AuthFailureLog(IOptions<PanelOptions> options, ILogger<AuthFailureLog> logger)
{
    private static readonly Lock Gate = new();

    public void Record(HttpContext http, string reason, string? username = null)
    {
        var ip = http.Connection.RemoteIpAddress;

        if (ip is null)
        {
            return;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        // Usernames are attacker-controlled: keep them out of the line fail2ban parses.
        var user = username is null ? "-" : new string(username.Take(32).Where(char.IsLetterOrDigit).ToArray());
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} AUTH_FAILURE ip={ip} reason={reason} user={user}\n");

        try
        {
            lock (Gate)
            {
                File.AppendAllText(options.Value.AuthLogFile, line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            logger.LogDebug(e, "Could not write the auth failure log");
        }
    }
}
