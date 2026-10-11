using Grpc.Core;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Journal;

public sealed class LogsAgentService(ICommandRunner commands) : LogsAgent.LogsAgentBase
{
    public override async Task<JournalEntries> QueryJournal(JournalQuery request, ServerCallContext context)
    {
        var reply = new JournalEntries();

        await commands.StreamAsync("journalctl", BuildArguments(request, follow: false), line =>
        {
            if (JournalParser.ParseLine(line) is { } entry)
            {
                reply.Entries.Add(entry);
            }

            return ValueTask.CompletedTask;
        }, mergeStdErr: false, context.CancellationToken);

        return reply;
    }

    public override async Task FollowJournal(JournalQuery request, IServerStreamWriter<JournalEntry> responseStream, ServerCallContext context)
    {
        try
        {
            // Runs until the panel cancels the call (browser closed the live tail); the runner then kills journalctl.
            await commands.StreamAsync("journalctl", BuildArguments(request, follow: true), async line =>
            {
                if (JournalParser.ParseLine(line) is { } entry)
                {
                    await responseStream.WriteAsync(entry, context.CancellationToken);
                }
            }, mergeStdErr: false, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Normal end of a live tail.
        }
    }

    private static List<string> BuildArguments(JournalQuery request, bool follow)
    {
        if (!string.IsNullOrEmpty(request.Unit))
        {
            Validate.ServiceName(request.Unit);
        }

        if (request.Grep.Length > 200)
        {
            throw Validate.Invalid("Search pattern is too long.");
        }

        return JournalParser.BuildArguments(request, follow);
    }
}
