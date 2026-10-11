using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Databases;

public sealed class DatabaseAgentService(DatabaseManager databases) : DatabaseAgent.DatabaseAgentBase
{
    private static readonly Empty Done = new();

    public override Task<DatabaseEngines> GetEngines(Empty request, ServerCallContext context) =>
        databases.GetEnginesAsync(context.CancellationToken);

    public override Task InstallEngine(EngineRef request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context) =>
        StreamOutputAsync(responseStream, context, output => databases.InstallAsync(request.Engine, output, CancellationToken.None));

    public override Task<DatabaseList> ListDatabases(EngineRef request, ServerCallContext context) =>
        databases.ListDatabasesAsync(request.Engine, context.CancellationToken);

    public override async Task<Empty> CreateDatabase(DatabaseRequest request, ServerCallContext context)
    {
        await databases.CreateDatabaseAsync(request, context.CancellationToken);

        return Done;
    }

    public override async Task<Empty> DropDatabase(DatabaseRequest request, ServerCallContext context)
    {
        await databases.DropDatabaseAsync(request, context.CancellationToken);

        return Done;
    }

    public override Task<DatabaseUserList> ListUsers(EngineRef request, ServerCallContext context) =>
        databases.ListUsersAsync(request.Engine, context.CancellationToken);

    public override async Task<Empty> CreateUser(DatabaseUserRequest request, ServerCallContext context)
    {
        await databases.CreateUserAsync(request, context.CancellationToken);

        return Done;
    }

    public override async Task<Empty> SetUserPassword(DatabaseUserRequest request, ServerCallContext context)
    {
        await databases.SetPasswordAsync(request, context.CancellationToken);

        return Done;
    }

    public override async Task<Empty> DropUser(DatabaseUserRequest request, ServerCallContext context)
    {
        await databases.DropUserAsync(request, context.CancellationToken);

        return Done;
    }

    public override async Task<Empty> SetGrant(GrantRequest request, ServerCallContext context)
    {
        await databases.SetGrantAsync(request, context.CancellationToken);

        return Done;
    }

    public override Task<BackupFile> BackupDatabase(DatabaseRequest request, ServerCallContext context) =>
        // Not tied to the call: a dump interrupted halfway is useless, so let it finish.
        databases.BackupAsync(request, CancellationToken.None);

    public override Task<BackupList> ListBackups(EngineRef request, ServerCallContext context) =>
        Task.FromResult(databases.ListBackups(request.Engine));

    public override Task<Empty> DeleteBackup(BackupRef request, ServerCallContext context)
    {
        databases.DeleteBackup(request.Id);

        return Task.FromResult(Done);
    }

    public override Task RestoreBackup(RestoreRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context) =>
        StreamOutputAsync(responseStream, context, output => databases.RestoreAsync(request, output, CancellationToken.None));

    public override Task DownloadBackup(BackupRef request, IServerStreamWriter<FileChunk> responseStream, ServerCallContext context) =>
        databases.DownloadAsync(request.Id, responseStream, context.CancellationToken);

    public override Task<BackupFile> UploadImport(IAsyncStreamReader<FileChunk> requestStream, ServerCallContext context) =>
        databases.UploadAsync(requestStream, context.CancellationToken);

    /// <summary>Runs work that reports progress lines; ends the stream with an exit marker.</summary>
    private static async Task StreamOutputAsync(IServerStreamWriter<OutputLine> stream, ServerCallContext context, Func<Func<string, ValueTask>, Task> work)
    {
        var gate = new SemaphoreSlim(1, 1);

        async ValueTask Write(string line)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            await gate.WaitAsync();

            try
            {
                await stream.WriteAsync(new OutputLine { Line = line });
            }
            finally
            {
                gate.Release();
            }
        }

        await work(Write);

        if (!context.CancellationToken.IsCancellationRequested)
        {
            await stream.WriteAsync(new OutputLine { Finished = true, ExitCode = 0 });
        }
    }
}
