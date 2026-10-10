using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.Ipc;

public interface IAgentCommandDispatcher
{
    Task<AgentReply> DispatchAsync(
        AgentCommand command,
        Func<AgentReply, CancellationToken, Task> publishAsync,
        CancellationToken cancellationToken);
}

public sealed class AgentPipeServer : IAsyncDisposable
{
    private const int MaximumPipeInstances = 16;
    private readonly string _pipeName;
    private readonly IAgentCommandDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private int _runStarted;

    public AgentPipeServer(string pipeName, IAgentCommandDispatcher dispatcher)
    {
        if (string.IsNullOrWhiteSpace(pipeName)
            || pipeName.Length > 100
            || pipeName.Any(character => !(char.IsLetterOrDigit(character) || character == '-' || character == '_')))
        {
            throw new ArgumentException("The pipe name contains unsupported characters.", nameof(pipeName));
        }

        _pipeName = pipeName;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("The pipe server can only be started once.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var clients = new List<Task>();
        try
        {
            while (!linkedCancellation.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = CreateServerStream();
                }
                catch (IOException)
                {
                    await Task.Delay(50, linkedCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await server.WaitForConnectionAsync(linkedCancellation.Token).ConfigureAwait(false);
                }
                catch
                {
                    server.Dispose();
                    throw;
                }

                clients.Add(HandleClientAsync(server, linkedCancellation.Token));
                clients.RemoveAll(task => task.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            // The Revit process or its private shutdown command ended this agent session.
        }
        finally
        {
            _shutdown.Cancel();
            if (clients.Count > 0)
            {
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        return ValueTask.CompletedTask;
    }

    private NamedPipeServerStream CreateServerStream() => new(
        _pipeName,
        PipeDirection.InOut,
        MaximumPipeInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        64 * 1024,
        64 * 1024);

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using (pipe)
        {
            AgentCommand? command;
            try
            {
                command = await AgentPipeFrameCodec.ReadCommandAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (command is null)
                {
                    return;
                }

                var reply = await _dispatcher.DispatchAsync(
                    command,
                    async (item, token) =>
                    {
                        item.RequestId = command.RequestId;
                        await AgentPipeFrameCodec.WriteReplyAsync(pipe, item, token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                reply.RequestId = command.RequestId;
                await AgentPipeFrameCodec.WriteReplyAsync(pipe, reply, cancellationToken).ConfigureAwait(false);
                if (reply.Kind == AgentReplyKinds.Shutdown)
                {
                    _shutdown.Cancel();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Connection was cancelled because its Revit host is shutting down.
            }
            catch (Exception)
            {
                // Invalid frames and disconnected clients are isolated to this pipe instance.
            }
        }
    }
}
