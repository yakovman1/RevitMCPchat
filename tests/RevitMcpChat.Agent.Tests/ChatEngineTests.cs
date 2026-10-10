using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.History;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class ChatEngineTests
{
    [Fact]
    public async Task WrongModelChatCannotIssueToolCall()
    {
        var temp = Path.Combine(Path.GetTempPath(), "RevitMcpChatModelGuard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var history = new SqliteChatHistory(temp);
        try
        {
            var conversationId = await history.CreateAsync(
                ModelIdentity.ForSaved(@"C:\Project\A.rvt"), "openai", "test-model", CancellationToken.None);
            var tool = Tool("revit_create_wall", readOnly: false);
            var gateway = new FakeGateway(tool);
            var provider = new ScriptedProvider(
                new ProviderEvent[] { new ProviderToolCallEvent("call-guard", tool.Name, "{}") });
            var sink = new FakeSink(approve: true);
            var request = Turn("Create a wall.");
            request.ConversationId = conversationId;
            request.ModelIdentity = ModelIdentity.ForSaved(@"C:\Project\B.rvt");

            await new ChatEngine(provider, gateway, history: history)
                .RunAsync(request, sink, CancellationToken.None);

            Assert.Equal(0, gateway.CallCount);
            Assert.Empty(provider.Requests);
            Assert.Contains(sink.Events, item => item.Kind == "chat_model_mismatch");
        }
        finally
        {
            await history.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadOnlyToolRunsWithoutApproval()
    {
        var tool = Tool("revit_get_levels", readOnly: true);
        var gateway = new FakeGateway(tool);
        var provider = new ScriptedProvider(
            new ProviderEvent[] { new ProviderToolCallEvent("call-1", tool.Name, "{}") },
            new ProviderEvent[] { new ProviderTextDeltaEvent("done") });
        var sink = new FakeSink(approve: false);

        await new ChatEngine(provider, gateway).RunAsync(Turn("Get the levels."), sink, CancellationToken.None);

        Assert.Equal(1, gateway.CallCount);
        Assert.Empty(sink.Approvals);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task UnknownToolRequiresApproval()
    {
        var tool = Tool("custom_tool", readOnly: null);
        var gateway = new FakeGateway(tool);
        var provider = new ScriptedProvider(
            new ProviderEvent[] { new ProviderToolCallEvent("call-2", tool.Name, "{}") },
            new ProviderEvent[] { new ProviderTextDeltaEvent("done") });
        var sink = new FakeSink(approve: true);

        await new ChatEngine(provider, gateway).RunAsync(Turn("Run the custom tool."), sink, CancellationToken.None);

        Assert.Single(sink.Approvals);
        Assert.Equal("call-2", sink.Approvals[0].CallId);
        Assert.Equal(tool.Name, sink.Approvals[0].ToolName);
        Assert.Equal("{}", sink.Approvals[0].ArgumentsJson);
        Assert.Equal(1, gateway.CallCount);
    }

    [Fact]
    public async Task RejectedSendCodeNeverCallsMcp()
    {
        const string codeArguments = "{\"code\":\"doc.Delete(elementId);\"}";
        var tool = Tool("revit_send_code_to_revit", readOnly: true);
        var gateway = new FakeGateway(tool);
        var provider = new ScriptedProvider(
            new ProviderEvent[] { new ProviderToolCallEvent("code-call", tool.Name, codeArguments) },
            new ProviderEvent[] { new ProviderTextDeltaEvent("The code was rejected.") });
        var sink = new FakeSink(approve: false);

        await new ChatEngine(provider, gateway).RunAsync(Turn("Delete the selected element."), sink, CancellationToken.None);

        Assert.Equal(0, gateway.CallCount);
        Assert.Single(sink.Approvals);
        Assert.True(sink.Approvals[0].IsCode);
        Assert.Equal(codeArguments, sink.Approvals[0].ArgumentsJson);
    }

    [Fact]
    public async Task MalformedArgumentsNeverCallMcp()
    {
        var schema = "{\"type\":\"object\",\"required\":[\"count\"],\"properties\":{\"count\":{\"type\":\"integer\"}}}";
        var tool = new McpToolDescriptor("revit_update_count", "Update count", schema, false);
        var gateway = new FakeGateway(tool);
        var provider = new ScriptedProvider(
            new ProviderEvent[] { new ProviderToolCallEvent("bad-call", tool.Name, "{\"count\":\"many\"}") },
            new ProviderEvent[] { new ProviderTextDeltaEvent("The arguments were invalid.") });
        var sink = new FakeSink(approve: true);

        await new ChatEngine(provider, gateway).RunAsync(Turn("Update the count."), sink, CancellationToken.None);

        Assert.Equal(0, gateway.CallCount);
        Assert.Empty(sink.Approvals);
    }

    [Fact]
    public async Task InterruptedWriteIsNotRetried()
    {
        var tool = Tool("revit_create_wall", readOnly: false);
        var gateway = new FakeGateway(tool)
        {
            CallOutcome = new McpCallOutcome("connection interrupted", isError: true, outcomeUnknown: true)
        };
        var provider = new ScriptedProvider(
            new ProviderEvent[] { new ProviderToolCallEvent("write-1", tool.Name, "{}") });
        var sink = new FakeSink(approve: true);

        await new ChatEngine(provider, gateway).RunAsync(Turn("Create a wall."), sink, CancellationToken.None);

        Assert.Equal(1, gateway.CallCount);
        Assert.Single(provider.Requests);
        Assert.Contains(sink.Events, item => item.Kind == "tool_outcome_unknown");
    }

    [Fact]
    public async Task CancellationDuringInterruptedWriteStillReportsUnknownOutcome()
    {
        var temp = Path.Combine(Path.GetTempPath(), "RevitMcpChatCancelledWrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var tool = Tool("revit_create_wall", readOnly: false);
            var gateway = new FakeGateway(tool)
            {
                CallOutcome = new McpCallOutcome("connection interrupted", isError: true, outcomeUnknown: true),
                BeforeCall = cancellation.Cancel
            };
            var provider = new ScriptedProvider(
                new ProviderEvent[] { new ProviderToolCallEvent("write-cancelled", tool.Name, "{}") });
            var sink = new FakeSink(approve: true, rejectCanceledPublish: true);
            var request = Turn("Create a wall.");

            await using (var history = new SqliteChatHistory(temp))
            {
                request.ConversationId = await history.CreateAsync(
                    request.ModelIdentity,
                    request.ProviderId,
                    request.ModelId,
                    CancellationToken.None);

                await new ChatEngine(provider, gateway, history: history).RunAsync(
                    request,
                    sink,
                    cancellation.Token);

                Assert.Contains(sink.Events, item => item.Kind == "tool_outcome_unknown");
                Assert.Contains(
                    (await history.LoadAsync(request.ConversationId, CancellationToken.None)).Events,
                    item => item.Kind == "tool_outcome_unknown");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(temp, recursive: true);
        }
    }
    [Fact]
    public async Task CatalogOnlyContainsAdvertisedTools()
    {
        var advertised = new[]
        {
            Tool("revit_get_walls", readOnly: true),
            Tool("revit_get_levels", readOnly: true),
            Tool("revit_delete_elements", readOnly: false)
        };
        var gateway = new FakeGateway(advertised);
        var provider = new ScriptedProvider(new ProviderEvent[] { new ProviderTextDeltaEvent("Found walls.") });
        var sink = new FakeSink(approve: false);

        await new ChatEngine(provider, gateway).RunAsync(Turn("Find walls."), sink, CancellationToken.None);

        Assert.NotEmpty(provider.Requests);
        var advertisedNames = advertised.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(provider.Requests[0].Tools, tool => Assert.Contains(tool.Name, advertisedNames));
    }

    [Fact]
    public async Task ToolListFailureIsReportedAsRecoverableMcpConnectionError()
    {
        var gateway = new FakeGateway { ListToolsException = new IOException("internal transport detail") };
        var provider = new ScriptedProvider(new ProviderEvent[] { new ProviderTextDeltaEvent("unused") });
        var sink = new FakeSink(approve: false);

        var exception = await Assert.ThrowsAsync<McpConnectionException>(() =>
            new ChatEngine(provider, gateway).RunAsync(Turn("What is in the model?"), sink, CancellationToken.None));

        Assert.Contains("rvt-mcp", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal transport detail", exception.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ApprovedWriteIsDurablyRecordedBeforeMcpSideEffectStarts()
    {
        var temp = Path.Combine(Path.GetTempPath(), "RevitMcpChatWriteAudit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        await using var history = new SqliteChatHistory(temp);
        var request = Turn("Create a wall.");
        request.ConversationId = await history.CreateAsync(request.ModelIdentity, request.ProviderId, request.ModelId, CancellationToken.None);
        var tool = Tool("revit_create_wall", readOnly: false);
        var gateway = new FakeGateway(tool);
        gateway.BeforeCall = () =>
        {
            var persisted = history.LoadAsync(request.ConversationId, CancellationToken.None).GetAwaiter().GetResult().Events;
            Assert.Contains(persisted, item => item.Kind == "tool_call" && item.ToolName == tool.Name);
            Assert.Contains(persisted, item => item.Kind == "tool_approval" && item.Text == "approved");
            Assert.Contains(persisted, item => item.Kind == "tool_running" && item.ToolName == tool.Name);
        };
        var provider = new ScriptedProvider(
            new ProviderEvent[] { new ProviderToolCallEvent("write-audit", tool.Name, "{}") },
            new ProviderEvent[] { new ProviderTextDeltaEvent("Wall created.") });

        await new ChatEngine(provider, gateway, history: history)
            .RunAsync(request, new FakeSink(approve: true), CancellationToken.None);

        var finalEvents = (await history.LoadAsync(request.ConversationId, CancellationToken.None)).Events;
        Assert.Contains(finalEvents, item => item.Kind == "tool_result" && item.Text == "ok");
    }

    private static McpToolDescriptor Tool(string name, bool? readOnly) =>
        new(name, "Test tool " + name, "{\"type\":\"object\",\"properties\":{}}", readOnly);

    private static ChatTurnRequest Turn(string text) => new()
    {
        ConversationId = Guid.Parse("1bc1b87d-f05e-4144-8849-a7c7399f9e28"),
        ModelIdentity = @"file:C:\Project\A.rvt",
        ProviderId = "openai",
        ModelId = "test-model",
        UserText = text
    };

    private sealed class ScriptedProvider : IChatProvider
    {
        private readonly Queue<IReadOnlyList<ProviderEvent>> _responses;

        public ScriptedProvider(params IReadOnlyList<ProviderEvent>[] responses) =>
            _responses = new Queue<IReadOnlyList<ProviderEvent>>(responses);

        public List<ProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ProviderEvent> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var events = _responses.Dequeue();
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeGateway : IMcpGateway
    {
        public FakeGateway() { }
        public FakeGateway(params McpToolDescriptor[] tools) => Tools = tools;

        public IReadOnlyList<McpToolDescriptor> Tools { get; } = Array.Empty<McpToolDescriptor>();
        public int CallCount { get; private set; }
        public McpCallOutcome CallOutcome { get; set; } = new("ok", isError: false, outcomeUnknown: false);
        public Action? BeforeCall { get; set; }
        public Exception? ListToolsException { get; set; }

        public Task ConnectAsync(int revitPid, string serverPath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
        {
            if (ListToolsException is not null)
            {
                throw ListToolsException;
            }

            return Task.FromResult(Tools);
        }

        public Task<McpCallOutcome> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
        {
            CallCount++;
            BeforeCall?.Invoke();
            return Task.FromResult(CallOutcome);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSink : IChatEventSink
    {
        private readonly bool _approve;
        private readonly bool _rejectCanceledPublish;

        public FakeSink(bool approve, bool rejectCanceledPublish = false)
        {
            _approve = approve;
            _rejectCanceledPublish = rejectCanceledPublish;
        }

        public List<ChatEvent> Events { get; } = new();
        public List<ToolApprovalRequest> Approvals { get; } = new();

        public Task PublishAsync(ChatEvent chatEvent, CancellationToken cancellationToken)
        {
            if (_rejectCanceledPublish && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            Events.Add(chatEvent);
            return Task.CompletedTask;
        }

        public Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
            Approvals.Add(request);
            return Task.FromResult(_approve);
        }
    }
}
