using System;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.Agent;

public interface IAgentCommandClient
{
    Task<AgentReply> SendAsync(AgentCommand command, CancellationToken cancellationToken);

    Task<AgentReply> SendAsync(
        AgentCommand command,
        Func<AgentReply, Task>? onEvent,
        CancellationToken cancellationToken);
}
