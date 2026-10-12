using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Deploy;

public sealed class DeployAgentService(DeployManager deploy) : DeployAgent.DeployAgentBase
{
    public override Task<AppUserInfo> EnsureAppUser(AppUserRequest request, ServerCallContext context) =>
        deploy.EnsureUserAsync(request.User, context.CancellationToken);

    public override Task<DeployKey> EnsureDeployKey(DeployKeyRequest request, ServerCallContext context) =>
        deploy.EnsureDeployKeyAsync(request, context.CancellationToken);

    public override Task<GitAccessResult> TestRepositoryAccess(GitSyncRequest request, ServerCallContext context) =>
        deploy.TestAccessAsync(request, context.CancellationToken);

    public override Task SyncRepository(GitSyncRequest request, IServerStreamWriter<DeployOutput> responseStream, ServerCallContext context) =>
        WithStream(responseStream, context, write => deploy.SyncAsync(request, write, context.CancellationToken));

    public override Task CreateRelease(ReleaseRequest request, IServerStreamWriter<DeployOutput> responseStream, ServerCallContext context) =>
        WithStream(responseStream, context, write => deploy.CreateReleaseAsync(request, write, context.CancellationToken));

    public override Task RunBuild(BuildRequest request, IServerStreamWriter<DeployOutput> responseStream, ServerCallContext context) =>
        WithStream(responseStream, context, async write =>
        {
            var exit = await deploy.BuildAsync(request, line => write(new DeployOutput { Line = line }), context.CancellationToken);
            await write(new DeployOutput { Finished = true, Success = exit == 0, Line = exit == 0 ? "" : $"Build failed (exit {exit})." });
        });

    public override Task<ReleaseState> ActivateRelease(ReleaseRequest request, ServerCallContext context) =>
        deploy.ActivateAsync(request, context.CancellationToken);

    public override Task<ReleaseState> GetReleaseState(AppRef request, ServerCallContext context) =>
        Task.FromResult(deploy.GetState(request.User, request.App));

    public override Task<ReleaseState> PruneReleases(PruneRequest request, ServerCallContext context) =>
        deploy.PruneAsync(request, context.CancellationToken);

    public override async Task<Empty> RemoveApp(AppRef request, ServerCallContext context)
    {
        await deploy.RemoveAppAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override async Task<Empty> WriteAppService(AppServiceRequest request, ServerCallContext context)
    {
        await deploy.WriteServiceAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override async Task<Empty> RemoveAppService(AppRef request, ServerCallContext context)
    {
        await deploy.RemoveServiceAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override async Task<Empty> SendConsoleInput(ConsoleInput request, ServerCallContext context)
    {
        await deploy.SendConsoleInputAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override Task<SiteDirectory> EnsureSiteDirectory(AppRef request, ServerCallContext context) =>
        deploy.EnsureSiteDirectoryAsync(request, context.CancellationToken);

    public override Task<Runtimes> ListRuntimes(Empty request, ServerCallContext context) =>
        deploy.ListRuntimesAsync(context.CancellationToken);

    public override async Task InstallRuntime(RuntimeRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        var gate = new SemaphoreSlim(1, 1);

        await deploy.InstallRuntimeAsync(request, async line =>
        {
            await gate.WaitAsync();

            try
            {
                if (!context.CancellationToken.IsCancellationRequested)
                {
                    await responseStream.WriteAsync(new OutputLine { Line = line });
                }
            }
            finally
            {
                gate.Release();
            }
        }, CancellationToken.None);

        await responseStream.WriteAsync(new OutputLine { Finished = true, ExitCode = 0 });
    }

    /// <summary>Serialises writes (stdout and stderr arrive concurrently) onto the response stream.</summary>
    private static async Task WithStream(IServerStreamWriter<DeployOutput> stream, ServerCallContext context, Func<Func<DeployOutput, Task>, Task> work)
    {
        var gate = new SemaphoreSlim(1, 1);

        await work(async output =>
        {
            await gate.WaitAsync();

            try
            {
                await stream.WriteAsync(output, context.CancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });
    }
}
