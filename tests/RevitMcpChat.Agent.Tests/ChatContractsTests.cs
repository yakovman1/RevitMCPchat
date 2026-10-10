using System;
using System.Text.Json;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public class ChatContractsTests
{
    [Fact]
    public void ChatContractsRoundTrip()
    {
        var conversationId = Guid.Parse("b1bcb3ec-8345-4d5d-897f-2d69a9c01ae5");
        var original = new ChatTurnRequest
        {
            ConversationId = conversationId,
            ModelIdentity = @"file:C:\Project\A.rvt",
            ProviderId = "openai",
            ModelId = "gpt-4.1",
            UserText = "List the levels."
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ChatTurnRequest>(json);

        Assert.NotNull(restored);
        Assert.Equal(conversationId, restored.ConversationId);
        Assert.Equal(@"file:C:\Project\A.rvt", restored.ModelIdentity);
        Assert.Equal("openai", restored.ProviderId);
        Assert.Equal("gpt-4.1", restored.ModelId);
        Assert.Equal("List the levels.", restored.UserText);
    }
}
