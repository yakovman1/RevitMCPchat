using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.RevitLogic.Tests;

internal sealed class ViewModelTestClient : IAgentCommandClient
{
    public List<AgentCommand> Commands { get; } = new();
    public Func<AgentCommand, Func<AgentReply, Task>?, Task>? PublishEvents { get; set; }
    public Func<AgentCommand, AgentReply> ReplyFactory { get; set; } = command => new AgentReply
    {
        RequestId = command.RequestId,
        Kind = AgentReplyKinds.Response,
        PayloadJson = "{}"
    };

    public Task<AgentReply> SendAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command);
        return Task.FromResult(ReplyFactory(command));
    }

    public async Task<AgentReply> SendAsync(
        AgentCommand command,
        Func<AgentReply, Task>? onEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command);
        if (PublishEvents is not null)
        {
            await PublishEvents(command, onEvent);
        }

        return ReplyFactory(command);
    }
}
