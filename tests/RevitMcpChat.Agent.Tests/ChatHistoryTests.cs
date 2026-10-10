using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.History;
using Microsoft.Data.Sqlite;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class ChatHistoryTests
{
    [Fact]
    public async Task HistorySurvivesProcessRestart()
    {
        using var temp = new TemporaryDirectory();
        var first = new SqliteChatHistory(temp.Path);
        var chatId = await first.CreateAsync("file:C:\\Project\\A.rvt", "openai", "gpt-test", CancellationToken.None);
        await first.AppendAsync(chatId, new ChatEvent { Kind = "assistant_complete", Text = "Hello from history." }, CancellationToken.None);
        await first.DisposeAsync();

        var reopened = new SqliteChatHistory(temp.Path);
        try
        {
            var transcript = await reopened.LoadAsync(chatId, CancellationToken.None);
            Assert.Equal("Hello from history.", Assert.Single(transcript.Events).Text);
            Assert.Equal("gpt-test", transcript.ModelId);
        }
        finally
        {
            await reopened.DisposeAsync();
        }
    }

    [Fact]
    public async Task CurrentModelFilterExcludesOtherFiles()
    {
        using var temp = new TemporaryDirectory();
        await using var history = new SqliteChatHistory(temp.Path);
        await history.CreateAsync("file:C:\\Project\\A.rvt", "openai", "gpt-test", CancellationToken.None);
        await history.CreateAsync("file:C:\\Project\\B.rvt", "openai", "gpt-test", CancellationToken.None);

        var chats = await history.ListAsync("file:C:\\Project\\A.rvt", CancellationToken.None);

        Assert.Single(chats);
        Assert.Equal("file:C:\\Project\\A.rvt", chats[0].ModelIdentity);
    }

    [Fact]
    public async Task AllChatsIncludesBothModels()
    {
        using var temp = new TemporaryDirectory();
        await using var history = new SqliteChatHistory(temp.Path);
        await history.CreateAsync("file:C:\\Project\\A.rvt", "openai", "gpt-test", CancellationToken.None);
        await history.CreateAsync("file:C:\\Project\\B.rvt", "anthropic", "claude-test", CancellationToken.None);

        var chats = await history.ListAsync(null, CancellationToken.None);

        Assert.Equal(2, chats.Count);
        Assert.Contains(chats, item => item.ProviderId == "openai");
        Assert.Contains(chats, item => item.ProviderId == "anthropic");
    }

    [Fact]
    public async Task UnsavedFirstSaveRebinds()
    {
        using var temp = new TemporaryDirectory();
        await using var history = new SqliteChatHistory(temp.Path);
        var oldIdentity = ModelIdentity.ForUnsaved(Guid.NewGuid());
        var savedIdentity = ModelIdentity.ForSaved(@"C:\Project\A.rvt");
        var chatId = await history.CreateAsync(oldIdentity, "openai", "gpt-test", CancellationToken.None);

        await history.BindUnsavedAsync(chatId, savedIdentity, CancellationToken.None);

        Assert.Equal(savedIdentity, (await history.LoadAsync(chatId, CancellationToken.None)).ModelIdentity);
    }

    [Fact]
    public async Task SaveAsDoesNotReuseOriginalIdentity()
    {
        using var temp = new TemporaryDirectory();
        await using var history = new SqliteChatHistory(temp.Path);
        var originalIdentity = ModelIdentity.ForSaved(@"C:\Project\Original.rvt");
        var copiedIdentity = ModelIdentity.ForSaved(@"C:\Project\Copy.rvt");
        var chatId = await history.CreateAsync(originalIdentity, "openai", "gpt-test", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => history.BindUnsavedAsync(chatId, copiedIdentity, CancellationToken.None));

        Assert.Equal(originalIdentity, (await history.LoadAsync(chatId, CancellationToken.None)).ModelIdentity);
        Assert.NotEqual(originalIdentity, copiedIdentity);
    }

    [Fact]
    public async Task SendCodeBodyIsAbsentFromDatabase()
    {
        using var temp = new TemporaryDirectory();
        await using var history = new SqliteChatHistory(temp.Path);
        var chatId = await history.CreateAsync("file:C:\\Project\\A.rvt", "openai", "gpt-test", CancellationToken.None);
        const string source = "doc.Delete(new ElementId(123456));";

        await history.AppendAsync(chatId, new ChatEvent
        {
            Kind = "tool_call_requested",
            ToolName = "revit_send_code_to_revit",
            Text = source
        }, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        var databaseBytes = await File.ReadAllBytesAsync(history.DatabasePath);
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(source)));
        Assert.DoesNotContain(source, (await history.LoadAsync(chatId, CancellationToken.None)).Events.Single().Text);
    }

    [Fact]
    public async Task HistoryRedactsKnownCredential()
    {
        using var temp = new TemporaryDirectory();
        const string secret = "sk-test-history-secret-908172";
        var redactor = new SecretRedactor();
        redactor.RegisterSecret(secret);
        await using var history = new SqliteChatHistory(temp.Path, redactor);
        var chatId = await history.CreateAsync("file:C:\\Project\\A.rvt", "openai", "gpt-test", CancellationToken.None);

        await history.AppendAsync(chatId, new ChatEvent
        {
            Kind = "tool_result",
            Text = "Provider returned " + secret
        }, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        var databaseBytes = await File.ReadAllBytesAsync(history.DatabasePath);
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)));
        Assert.Contains("[REDACTED]", (await history.LoadAsync(chatId, CancellationToken.None)).Events.Single().Text);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevitMcpChatHistory-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
