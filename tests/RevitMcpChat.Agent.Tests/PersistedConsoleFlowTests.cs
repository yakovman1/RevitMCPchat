using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RevitMcpChat.Agent;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Console;
using RevitMcpChat.Agent.History;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.Settings;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class PersistedConsoleFlowTests
{
    [Fact]
    public void SecretInputIsNotEchoed()
    {
        var keys = new Queue<ConsoleKeyInfo>("secret".Select(character =>
            new ConsoleKeyInfo(character, (ConsoleKey)char.ToUpperInvariant(character), false, false, false)));
        keys.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        var output = new StringWriter();

        var secret = ConsoleSecretReader.ReadSecret(output, keys.Dequeue, "API key: ");

        Assert.Equal("secret", secret);
        Assert.Equal("API key: " + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task UserCanResumeChatAfterAgentRestart()
    {
        var root = NewProfileDirectory();
        var secrets = new MemorySecretStore();
        Guid chatId;
        try
        {
            await using (var firstRun = new AgentWorkspace(root, secrets, new EmptyAdminKeySource()))
            {
                await firstRun.SetActiveProviderAsync(Profile("openai", "gpt-test"));
                chatId = await firstRun.CreateChatAsync("file:C:/models/house.rvt", CancellationToken.None);
                var engine = firstRun.CreateChatEngine(
                    new ScriptedProvider(new ProviderTextDeltaEvent("Saved answer.")),
                    new FakeGateway());
                await engine.RunAsync(Request(chatId, "file:C:/models/house.rvt", "openai", "gpt-test", "Hello"), new CollectingSink(), CancellationToken.None);
            }

            await using var restarted = new AgentWorkspace(root, secrets, new EmptyAdminKeySource());
            var transcript = await restarted.OpenChatAsync(chatId, CancellationToken.None);
            var resumedProvider = new ScriptedProvider(new ProviderTextDeltaEvent("Continued answer."));
            await restarted.CreateChatEngine(resumedProvider, new FakeGateway())
                .RunAsync(Request(chatId, "file:C:/models/house.rvt", "openai", "gpt-test", "Continue"), new CollectingSink(), CancellationToken.None);

            Assert.Contains(transcript.Events, item => item.Kind == "user" && item.Text == "Hello");
            Assert.Contains(transcript.Events, item => item.Kind == "assistant_complete" && item.Text == "Saved answer.");
            Assert.Contains(resumedProvider.Requests.Single().Messages, item => item.Role == "user" && item.Content == "Hello");
            Assert.Contains(resumedProvider.Requests.Single().Messages, item => item.Role == "assistant" && item.Content == "Saved answer.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SwitchingProviderRetainsOriginalChatMetadata()
    {
        var root = NewProfileDirectory();
        try
        {
            await using var workspace = new AgentWorkspace(root, new MemorySecretStore(), new EmptyAdminKeySource());
            await workspace.SetActiveProviderAsync(Profile("openai", "gpt-original"));
            var chatId = await workspace.CreateChatAsync("file:C:/models/house.rvt", CancellationToken.None);
            await workspace.SetActiveProviderAsync(Profile("anthropic", "claude-current"));

            var transcript = await workspace.OpenChatAsync(chatId, CancellationToken.None);

            Assert.Equal("openai", transcript.ProviderId);
            Assert.Equal("gpt-original", transcript.ModelId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemovingKeyRevealsBuildAccessStatus()
    {
        var root = NewProfileDirectory();
        try
        {
            var secrets = new MemorySecretStore();
            var admin = new FakeAdminKeySource(new Dictionary<string, string> { ["openai"] = "build-key" });
            await using var workspace = new AgentWorkspace(root, secrets, admin);
            await workspace.SetUserKeyAsync("openai", "user-key", CancellationToken.None);

            var userCredential = await workspace.ResolveCredentialAsync("openai", CancellationToken.None);
            var statusAfterRemoval = await workspace.RemoveUserKeyAsync("openai", CancellationToken.None);
            var resolvedAfterRemoval = await workspace.ResolveCredentialAsync("openai", CancellationToken.None);

            Assert.Equal(CredentialSource.User, userCredential.Source);
            Assert.Equal(CredentialSource.Admin, statusAfterRemoval.Source);
            Assert.Equal(CredentialSource.Admin, resolvedAfterRemoval.Source);
            Assert.Equal("build-key", resolvedAfterRemoval.Value);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineHistoryStillOpens()
    {
        var root = NewProfileDirectory();
        var secrets = new MemorySecretStore();
        Guid chatId;
        try
        {
            await using (var firstRun = new AgentWorkspace(root, secrets, new EmptyAdminKeySource()))
            {
                await firstRun.SetActiveProviderAsync(Profile("gemini", "gemini-test"));
                chatId = await firstRun.CreateChatAsync("file:C:/models/house.rvt", CancellationToken.None);
                await firstRun.CreateChatEngine(
                        new ScriptedProvider(new ProviderTextDeltaEvent("Available offline.")),
                        new FakeGateway())
                    .RunAsync(Request(chatId, "file:C:/models/house.rvt", "gemini", "gemini-test", "Show history"), new CollectingSink(), CancellationToken.None);
            }

            await using var offline = new AgentWorkspace(root, secrets, new EmptyAdminKeySource());
            var transcript = await offline.OpenChatAsync(chatId, CancellationToken.None);

            Assert.Equal("gemini", transcript.ProviderId);
            Assert.Contains(transcript.Events, item => item.Kind == "assistant_complete" && item.Text == "Available offline.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedSendCodeCallOmitsSourceAfterEnginePublication()
    {
        var root = NewProfileDirectory();
        try
        {
            await using var workspace = new AgentWorkspace(root, new MemorySecretStore(), new EmptyAdminKeySource());
            await workspace.SetActiveProviderAsync(Profile("openai", "gpt-test"));
            var modelIdentity = "file:C:/models/house.rvt";
            var chatId = await workspace.CreateChatAsync(modelIdentity, CancellationToken.None);
            const string source = "doc.Delete(elementId);";
            const string arguments = "{\"code\":\"doc.Delete(elementId);\"}";
            var tool = new McpToolDescriptor(
                "revit_send_code_to_revit",
                "Execute C# in Revit",
                "{\"type\":\"object\",\"required\":[\"code\"],\"properties\":{\"code\":{\"type\":\"string\"}}}",
                true);
            var gateway = new FakeGateway(tool);
            var provider = new TwoRoundProvider(
                new ProviderEvent[] { new ProviderToolCallEvent("call-code", tool.Name, arguments) },
                new ProviderEvent[] { new ProviderTextDeltaEvent("The request was rejected.") });

            await workspace.CreateChatEngine(provider, gateway)
                .RunAsync(Request(chatId, modelIdentity, "openai", "gpt-test", "Delete the element"), new CollectingSink(), CancellationToken.None);

            var transcript = await workspace.OpenChatAsync(chatId, CancellationToken.None);
            var storedCall = Assert.Single(transcript.Events, item => item.Kind == "tool_call");
            Assert.Equal("[C# source omitted]", storedCall.Text);
            Assert.DoesNotContain(source, storedCall.Text, StringComparison.Ordinal);
            Assert.Equal(0, gateway.CallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewProfileDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "RevitMcpChatWorkspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProviderProfile Profile(string providerId, string modelId) => new()
    {
        ProviderId = providerId,
        DisplayName = providerId,
        ModelId = modelId,
        BaseUrl = "https://example.invalid"
    };

    private static ChatTurnRequest Request(Guid id, string modelIdentity, string providerId, string modelId, string text) => new()
    {
        ConversationId = id,
        ModelIdentity = modelIdentity,
        ProviderId = providerId,
        ModelId = modelId,
        UserText = text
    };

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

        public Task SaveAsync(string providerId, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _keys[providerId] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadAsync(string providerId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _keys.TryGetValue(providerId, out var value);
            return Task.FromResult(value);
        }

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _keys.Remove(providerId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdminKeySource : IAdminKeySource
    {
        private readonly IReadOnlyDictionary<string, string> _keys;

        public FakeAdminKeySource(IReadOnlyDictionary<string, string> keys) => _keys = keys;

        public string? GetKey(string providerId) => _keys.TryGetValue(providerId, out var key) ? key : null;
    }

    private sealed class ScriptedProvider : IChatProvider
    {
        private readonly IReadOnlyList<ProviderEvent> _events;

        public ScriptedProvider(params ProviderEvent[] events) => _events = events;

        public List<ProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ProviderEvent> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var item in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class TwoRoundProvider : IChatProvider
    {
        private readonly Queue<IReadOnlyList<ProviderEvent>> _rounds;

        public TwoRoundProvider(params IReadOnlyList<ProviderEvent>[] rounds) => _rounds = new Queue<IReadOnlyList<ProviderEvent>>(rounds);

        public async IAsyncEnumerable<ProviderEvent> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in _rounds.Dequeue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeGateway : IMcpGateway
    {
        private readonly IReadOnlyList<McpToolDescriptor> _tools;

        public FakeGateway(params McpToolDescriptor[] tools) => _tools = tools;

        public int CallCount { get; private set; }

        public Task ConnectAsync(int revitPid, string serverPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_tools);
        public Task<McpCallOutcome> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The test approval rejects every write tool.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CollectingSink : IChatEventSink
    {
        public List<ChatEvent> Events { get; } = new();

        public Task PublishAsync(ChatEvent chatEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(chatEvent);
            return Task.CompletedTask;
        }

        public Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
