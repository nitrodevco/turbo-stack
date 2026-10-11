using Grpc.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.HostInfo;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;

namespace TurboManager.Agent.Packages;

public sealed class PackagesAgentService(ICommandRunner commands, SystemInfoReader systemInfo, IOptions<AgentOptions> options)
    : PackagesAgent.PackagesAgentBase
{
    /// <summary>apt holds a global lock; never run two package operations at once.</summary>
    private static readonly SemaphoreSlim AptGate = new(1, 1);

    private static readonly string[] DpkgKeepConfig =
        ["-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold"];

    private string HostPath(string relativePath) => Path.Combine(options.Value.HostRoot, relativePath);

    public override async Task<UpgradablePackages> ListUpgradable(ListUpgradableRequest request, ServerCallContext context)
    {
        if (request.Refresh)
        {
            if (!await AptGate.WaitAsync(TimeSpan.Zero, context.CancellationToken))
            {
                throw Validate.FailedPrecondition("A package operation is already running.");
            }

            try
            {
                await commands.RunCheckedAsync("apt-get", ["update", "-qq"], timeout: TimeSpan.FromMinutes(5),
                    cancellationToken: context.CancellationToken);
            }
            finally
            {
                AptGate.Release();
            }
        }

        var list = await commands.RunCheckedAsync("apt", ["list", "--upgradable"], timeout: TimeSpan.FromMinutes(2),
            cancellationToken: context.CancellationToken);
        var reply = new UpgradablePackages
        {
            RebootRequired = File.Exists(HostPath("run/reboot-required")),
            ListsUpdatedUnixMs = ListsUpdatedAt(),
        };

        reply.Packages.AddRange(AptParser.ParseUpgradable(list.StdOut).OrderByDescending(p => p.Security).ThenBy(p => p.Name));

        if (File.Exists(HostPath("run/reboot-required.pkgs")))
        {
            reply.RebootRequiredPackages.AddRange((await File.ReadAllLinesAsync(HostPath("run/reboot-required.pkgs"), context.CancellationToken))
                .Where(l => l.Length > 0).Distinct());
        }

        return reply;
    }

    public override async Task UpgradePackages(UpgradeRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        List<string> arguments;
        string command;

        switch (request.Mode)
        {
            case UpgradeMode.All:
                command = "apt-get";
                arguments = ["-y", .. DpkgKeepConfig, "upgrade"];
                break;
            case UpgradeMode.Security:
                command = "unattended-upgrade";
                arguments = ["-v"];
                break;
            case UpgradeMode.Selected when request.Names.Count > 0:
                if (request.Names.Any(n => !AptParser.IsValidPackageName(n)))
                {
                    throw Validate.Invalid("Invalid package name.");
                }

                command = "apt-get";
                arguments = ["-y", .. DpkgKeepConfig, "install", "--only-upgrade", .. request.Names];
                break;
            default:
                throw Validate.Invalid("Choose what to upgrade.");
        }

        if (!await AptGate.WaitAsync(TimeSpan.Zero, context.CancellationToken))
        {
            throw Validate.FailedPrecondition("A package operation is already running.");
        }

        try
        {
            await responseStream.WriteAsync(new OutputLine { Line = $"$ {command} {string.Join(' ', arguments)}" });

            // Not cancelled with the call: interrupting dpkg mid-way can leave packages half-configured.
            var exitCode = await commands.StreamAsync(command, arguments,
                line => context.CancellationToken.IsCancellationRequested
                    ? ValueTask.CompletedTask
                    : new ValueTask(responseStream.WriteAsync(new OutputLine { Line = line })),
                mergeStdErr: true, CancellationToken.None);

            if (!context.CancellationToken.IsCancellationRequested)
            {
                await responseStream.WriteAsync(new OutputLine { Finished = true, ExitCode = exitCode });
            }
        }
        finally
        {
            AptGate.Release();
        }
    }

    public override async Task<RebootStatus> ScheduleReboot(RebootRequest request, ServerCallContext context)
    {
        if (!string.IsNullOrEmpty(systemInfo.DetectContainer()))
        {
            return Unsupported();
        }

        if (request.Cancel)
        {
            await commands.RunAsync("shutdown", ["-c"], cancellationToken: context.CancellationToken);

            return ReadStatus("Scheduled reboot cancelled.");
        }

        if (request.DelayMinutes is < 0 or > 10_080)
        {
            throw Validate.Invalid("Delay must be between 0 minutes and 7 days.");
        }

        var when = request.DelayMinutes == 0 ? "now" : $"+{request.DelayMinutes}";
        await commands.RunCheckedAsync("shutdown", ["-r", when, $"Reboot requested from {Branding.ProductName}"],
            cancellationToken: context.CancellationToken);

        return ReadStatus(request.DelayMinutes == 0 ? "Rebooting now." : $"Reboot scheduled in {request.DelayMinutes} minutes.");
    }

    public override Task<RebootStatus> GetRebootStatus(RebootRequest request, ServerCallContext context) =>
        Task.FromResult(string.IsNullOrEmpty(systemInfo.DetectContainer()) ? ReadStatus("") : Unsupported());

    private RebootStatus ReadStatus(string message)
    {
        var path = HostPath("run/systemd/shutdown/scheduled");
        var scheduledMs = File.Exists(path) ? AptParser.ParseScheduledShutdownMs(File.ReadAllText(path)) : null;

        return new RebootStatus
        {
            Supported = true,
            Scheduled = scheduledMs is not null,
            ScheduledAtUnixMs = scheduledMs ?? 0,
            Message = message,
        };
    }

    private static RebootStatus Unsupported() =>
        new() { Supported = false, Message = "Reboots are not available inside a container." };

    private long ListsUpdatedAt()
    {
        var stamp = HostPath("var/lib/apt/periodic/update-success-stamp");

        if (File.Exists(stamp))
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(stamp)).ToUnixTimeMilliseconds();
        }

        var lists = HostPath("var/lib/apt/lists");

        return Directory.Exists(lists)
            ? new DateTimeOffset(Directory.GetFiles(lists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.UnixEpoch).Max()).ToUnixTimeMilliseconds()
            : 0;
    }
}
