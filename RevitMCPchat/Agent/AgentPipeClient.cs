using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.Agent;

public sealed class AgentPipeClient
{
    private readonly string _pipeName;

    public AgentPipeClient(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        }

        _pipeName = pipeName;
    }

    public Task<AgentReply> SendAsync(AgentCommand command, CancellationToken cancellationToken) =>
        SendAsync(command, onEvent: null, cancellationToken);

    public async Task<AgentReply> SendAsync(
        AgentCommand command,
        Func<AgentReply, Task>? onEvent,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (command.RequestId == Guid.Empty)
        {
            command.RequestId = Guid.NewGuid();
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        await AgentPipeFrameCodec.WriteCommandAsync(pipe, command, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var reply = await AgentPipeFrameCodec.ReadReplyAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                throw new EndOfStreamException("The local agent closed the pipe before replying.");
            }

            if (reply.RequestId != command.RequestId)
            {
                throw new InvalidDataException("The local agent returned a reply for a different request.");
            }

            if (reply.Kind == AgentReplyKinds.Event || reply.Kind == AgentReplyKinds.Approval)
            {
                if (onEvent is not null)
                {
                    await onEvent(reply).ConfigureAwait(false);
                }

                continue;
            }

            return reply;
        }
    }
}
