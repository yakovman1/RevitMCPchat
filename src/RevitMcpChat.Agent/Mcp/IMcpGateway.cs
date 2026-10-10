using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMcpChat.Agent.Mcp;

public interface IMcpGateway : IAsyncDisposable
{
    Task ConnectAsync(int revitPid, string serverPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken);
    Task<McpCallOutcome> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken);
}
