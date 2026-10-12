using Grpc.Core;
using Grpc.Core.Interceptors;

namespace TurboManager.Agent.Commands;

/// <summary>
/// A system tool that failed reaches the panel as a readable error (which program, its exit code and what it said)
/// rather than an opaque "exception was thrown by handler". Arguments are left out: they may name things the
/// caller shouldn't see in an error message.
/// </summary>
public sealed class CommandErrorInterceptor(ILogger<CommandErrorInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (CommandFailedException e)
        {
            throw Translate(e, context);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw NotStarted(e, context);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(requestStream, context);
        }
        catch (CommandFailedException e)
        {
            throw Translate(e, context);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw NotStarted(e, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (CommandFailedException e)
        {
            throw Translate(e, context);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw NotStarted(e, context);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(requestStream, responseStream, context);
        }
        catch (CommandFailedException e)
        {
            throw Translate(e, context);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw NotStarted(e, context);
        }
    }

    private RpcException Translate(CommandFailedException e, ServerCallContext context)
    {
        logger.LogWarning("{Method}: {Error}", context.Method, e.Message);

        return new RpcException(new Status(StatusCode.Aborted, Describe(e)));
    }

    private RpcException NotStarted(System.ComponentModel.Win32Exception e, ServerCallContext context)
    {
        logger.LogWarning("{Method}: {Error}", context.Method, e.Message);

        // "An error occurred trying to start process 'x' …": the program isn't installed.
        return new RpcException(new Status(StatusCode.FailedPrecondition, $"A program this needs could not be started: {e.Message}"));
    }

    public static string Describe(CommandFailedException e)
    {
        var program = e.Command.Split(' ', 2)[0];
        var said = string.Join('\n', e.Result.ErrorText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(5));

        return said.Length > 0 ? $"{program} failed (exit code {e.Result.ExitCode}): {said}" : $"{program} failed (exit code {e.Result.ExitCode}).";
    }
}
