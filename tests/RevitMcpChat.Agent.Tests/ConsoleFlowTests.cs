using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Console;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class ConsoleFlowTests
{
    [Fact]
    public async Task ConsoleApprovalRejectsWithoutMcpCall()
    {
        const string arguments = "{\"elementId\":42}";
        var gateway = new FakeGateway();
        var provider = new ScriptedProvider(
            new ProviderEvent[] { new ProviderToolCallEvent("write-1", "revit_delete_element", arguments) },
            new ProviderEvent[] { new ProviderTextDeltaEvent("The change was rejected.") });
        var output = new StringWriter();
        var sink = new ConsoleChatSink(new StringReader("n\n"), output);
        var request = new ChatTurnRequest
        {
            ConversationId = Guid.NewGuid(),
            ModelIdentity = "revit-pid:42",
            ProviderId = "openai-compatible",
            ModelId = "test-model",
            UserText = "Delete element 42."
        };

        await new ChatEngine(provider, gateway).RunAsync(request, sink, CancellationToken.None);

        Assert.Equal(0, gateway.CallCount);
        Assert.Contains("revit_delete_element", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"elementId\": 42", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class ScriptedProvider : IChatProvider
    {
        private readonly Queue<IReadOnlyList<ProviderEvent>> _responses;

        public ScriptedProvider(params IReadOnlyList<ProviderEvent>[] responses) =>
            _responses = new Queue<IReadOnlyList<ProviderEvent>>(responses);

        public async IAsyncEnumerable<ProviderEvent> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in _responses.Dequeue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeGateway : IMcpGateway
    {
        private readonly IReadOnlyList<McpToolDescriptor> _tools =
            new[] { new McpToolDescriptor("revit_delete_element", "Delete one model element", "{\"type\":\"object\",\"required\":[\"elementId\"],\"properties\":{\"elementId\":{\"type\":\"integer\"}}}", false) };

        public int CallCount { get; private set; }

        public Task ConnectAsync(int revitPid, string serverPath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_tools);

        public Task<McpCallOutcome> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new McpCallOutcome("deleted", isError: false, outcomeUnknown: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
