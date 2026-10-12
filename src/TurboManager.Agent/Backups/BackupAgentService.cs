using Grpc.Core;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Backups;

public sealed class BackupAgentService(BackupManager backups) : BackupAgent.BackupAgentBase
{
    public override Task<TargetStatus> PrepareTarget(BackupTarget request, ServerCallContext context) => backups.PrepareAsync(request, context.CancellationToken);

    public override async Task RunBackup(BackupRunRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        var snapshot = await backups.RunAsync(request, line => responseStream.WriteAsync(new OutputLine { Line = line }), context.CancellationToken);
        await responseStream.WriteAsync(new OutputLine { Line = $"snapshot:{snapshot}", Finished = true });
    }

    public override Task<SnapshotList> ListSnapshots(SnapshotsRequest request, ServerCallContext context) =>
        backups.ListSnapshotsAsync(request, context.CancellationToken);

    public override Task<BackupManifest> ReadManifest(SnapshotRef request, ServerCallContext context) =>
        backups.ReadManifestAsync(request, context.CancellationToken);

    public override async Task RestoreSnapshot(RestoreSnapshotRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        await backups.RestoreAsync(request, line => responseStream.WriteAsync(new OutputLine { Line = line }), context.CancellationToken);
        await responseStream.WriteAsync(new OutputLine { Finished = true });
    }

    public override Task<TargetKey> CreateTargetKey(TargetKeyRequest request, ServerCallContext context) =>
        backups.CreateTargetKeyAsync(request.TargetId, context.CancellationToken);

    public override Task<HostKeyReply> ScanHostKey(HostKeyRequest request, ServerCallContext context) =>
        backups.ScanHostKeyAsync(request, context.CancellationToken);
}
