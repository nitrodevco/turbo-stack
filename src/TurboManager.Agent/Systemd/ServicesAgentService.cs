using Grpc.Core;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;

namespace TurboManager.Agent.Systemd;

public sealed class ServicesAgentService(ICommandRunner commands) : ServicesAgent.ServicesAgentBase
{
    /// <summary>Stopping or disabling these would cut off the panel or remote access.</summary>
    private static readonly HashSet<string> Protected =
        [Branding.AgentUnit, Branding.PanelUnit, "ssh.service", "sshd.service", "systemd-journald.service", "dbus.service"];

    public override async Task<ListServicesReply> ListServices(ListServicesRequest request, ServerCallContext context)
    {
        var loaded = await commands.RunCheckedAsync("systemctl",
            ["list-units", "--type=service", "--all", "--plain", "--no-legend", "--no-pager"], cancellationToken: context.CancellationToken);
        var installed = await commands.RunCheckedAsync("systemctl",
            ["list-unit-files", "--type=service", "--plain", "--no-legend", "--no-pager"], cancellationToken: context.CancellationToken);

        var names = SystemctlParser.ParseUnitNames(loaded.StdOut)
            .Concat(SystemctlParser.ParseUnitNames(installed.StdOut))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var reply = new ListServicesReply();
        reply.Services.AddRange(await ShowAsync(names, context.CancellationToken));

        return reply;
    }

    public override async Task<ServiceUnit> ControlService(ControlServiceRequest request, ServerCallContext context)
    {
        var name = Validate.ServiceName(request.Name);
        var verb = request.Action switch
        {
            ServiceAction.Start => "start",
            ServiceAction.Stop => "stop",
            ServiceAction.Restart => "restart",
            ServiceAction.Reload => "reload",
            ServiceAction.Enable => "enable",
            ServiceAction.Disable => "disable",
            _ => throw Validate.Invalid("Unknown service action."),
        };

        if (verb is "stop" or "disable" && Protected.Contains(name))
        {
            throw Validate.FailedPrecondition($"{name} is required for managing this server and can't be stopped or disabled here.");
        }

        var result = await commands.RunAsync("systemctl", [verb, name], timeout: TimeSpan.FromMinutes(2),
            cancellationToken: context.CancellationToken);

        if (!result.Success)
        {
            throw new RpcException(new Status(StatusCode.Aborted, $"systemctl {verb} {name} failed: {result.ErrorText}"));
        }

        var units = await ShowAsync([name], context.CancellationToken);

        return units.FirstOrDefault() ?? throw new RpcException(new Status(StatusCode.NotFound, $"{name} not found"));
    }

    private async Task<List<ServiceUnit>> ShowAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return [];
        }

        List<string> arguments = ["show", "--timestamp=unix", "-p", string.Join(',', SystemctlParser.ShowProperties), "--"];
        arguments.AddRange(names);

        var result = await commands.RunCheckedAsync("systemctl", arguments, cancellationToken: cancellationToken);

        // An alias (sshd.service, procps.service, dbus-org.freedesktop.*) shows as the unit it points to, so
        // the same unit can come back twice.
        return SystemctlParser.ParseShowBlocks(result.StdOut)
            .Select(SystemctlParser.ToServiceUnit)
            .Where(unit => unit.LoadState != "not-found")
            .DistinctBy(unit => unit.Name, StringComparer.Ordinal)
            .ToList();
    }
}
