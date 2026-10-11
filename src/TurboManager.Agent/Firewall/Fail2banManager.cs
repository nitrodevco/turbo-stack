using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;

namespace TurboManager.Agent.Firewall;

public sealed class Fail2banManager(ICommandRunner commands, IOptions<AgentOptions> options)
{
    private string JailFile => Path.Combine(options.Value.HostRoot, $"etc/fail2ban/jail.d/{Branding.Slug}.conf");

    public async Task<Fail2banStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = new Fail2banStatus();
        CommandResult overview;

        try
        {
            overview = await commands.RunAsync("fail2ban-client", ["status"], cancellationToken: cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            status.Error = "fail2ban is not installed.";

            return status;
        }

        if (!overview.Success)
        {
            status.Error = overview.ErrorText;

            return status;
        }

        status.Available = true;

        foreach (var name in Fail2banParser.ParseJailList(overview.StdOut))
        {
            status.Jails.Add(await GetJailAsync(name, cancellationToken));
        }

        return status;
    }

    public async Task<Fail2banStatus> BanAsync(Fail2banBanRequest request, CancellationToken cancellationToken)
    {
        var jail = await RequireJailAsync(request.Jail, cancellationToken);
        var ip = Validate.IpAddress(request.Ip);

        await commands.RunCheckedAsync("fail2ban-client", ["set", jail, request.Unban ? "unbanip" : "banip", ip],
            cancellationToken: cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<Fail2banStatus> UpdateJailAsync(Fail2banJailSettings settings, CancellationToken cancellationToken)
    {
        var jail = await RequireJailAsync(settings.Jail, cancellationToken);

        if (settings.BanTimeSeconds is < 60 or > 31_536_000 || settings.FindTimeSeconds is < 60 or > 604_800 || settings.MaxRetry is < 1 or > 100)
        {
            throw Validate.Invalid("Ban time must be 1 minute to 1 year, find time 1 minute to 7 days, max retries 1 to 100.");
        }

        var ignore = settings.IgnoreIps.Select(Validate.Source).Where(s => s != "any").ToList();
        var document = IniDocument.Parse(File.Exists(JailFile) ? await File.ReadAllTextAsync(JailFile, cancellationToken) : "");

        document.Set(jail, "bantime", settings.BanTimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        document.Set(jail, "findtime", settings.FindTimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        document.Set(jail, "maxretry", settings.MaxRetry.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Loopback is always ignored so the panel's own health checks can never be banned.
        document.Set(jail, "ignoreip", string.Join(' ', new[] { "127.0.0.1/8", "::1" }.Concat(ignore).Distinct()));

        await File.WriteAllTextAsync(JailFile, document.Write($"Managed by {Branding.ProductName}."), cancellationToken);
        await commands.RunCheckedAsync("fail2ban-client", ["reload"], timeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    private async Task<Fail2banJail> GetJailAsync(string name, CancellationToken cancellationToken)
    {
        var status = await commands.RunAsync("fail2ban-client", ["status", name], cancellationToken: cancellationToken);
        var jail = new Fail2banJail
        {
            Name = name,
            CurrentlyFailed = Fail2banParser.ReadInt(status.StdOut, "Currently failed"),
            TotalFailed = Fail2banParser.ReadInt(status.StdOut, "Total failed"),
            TotalBanned = Fail2banParser.ReadInt(status.StdOut, "Total banned"),
            BanTimeSeconds = await GetIntAsync(name, "bantime", cancellationToken),
            FindTimeSeconds = await GetIntAsync(name, "findtime", cancellationToken),
            MaxRetry = await GetIntAsync(name, "maxretry", cancellationToken),
        };

        jail.BannedIps.AddRange(Fail2banParser.ParseBannedIps(status.StdOut));

        var ignore = await commands.RunAsync("fail2ban-client", ["get", name, "ignoreip"], cancellationToken: cancellationToken);
        jail.IgnoreIps.AddRange(Fail2banParser.ParseIgnoreIps(ignore.StdOut));

        return jail;
    }

    private async Task<int> GetIntAsync(string jail, string key, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync("fail2ban-client", ["get", jail, key], cancellationToken: cancellationToken);

        return int.TryParse(result.StdOut.Trim(), out var value) ? value : 0;
    }

    private async Task<string> RequireJailAsync(string jail, CancellationToken cancellationToken)
    {
        Validate.Fail2banJail(jail);
        var overview = await commands.RunCheckedAsync("fail2ban-client", ["status"], cancellationToken: cancellationToken);

        return Fail2banParser.ParseJailList(overview.StdOut).Contains(jail)
            ? jail
            : throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.NotFound, $"Jail {jail} is not active."));
    }
}
