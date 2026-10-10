using System;
using System.Linq;
using System.Threading.Tasks;
using RevitMCPchat.ViewModels;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.RevitLogic.Tests;

public sealed class ChatViewModelTests
{
    [Fact]
    public async Task WrongModelChatShowsHistoryButDisablesSend()
    {
        var client = new ViewModelTestClient();
        var viewModel = CreateReadyChat(client);
        viewModel.OpenTranscript(new ChatTranscriptDto
        {
            ChatId = Guid.NewGuid(),
            Title = "Other model chat",
            ModelIdentity = ModelIdentity.ForSaved(@"C:\Projects\Other.rvt"),
            ProviderId = "openai",
            ModelId = "gpt-4o-mini",
            Events = { new ChatEvent { Kind = "user", Text = "Earlier question" } }
        });

        Assert.Single(viewModel.Messages);
        Assert.Equal("Earlier question", viewModel.Messages.Single().Text);
        Assert.False(viewModel.CanSend);

        await viewModel.SendAsync("Do not call the other model.");
        Assert.Empty(client.Commands);
    }

    [Fact]
    public async Task NoOpenDocumentDisablesMcpButKeepsHistory()
    {
        var client = new ViewModelTestClient();
        var chat = CreateReadyChat(client);
        chat.SetActiveModel(new ActiveModelInfo { IsOpen = false });
        var history = new HistoryViewModel(client);
        history.CurrentModelIdentity = string.Empty;
        history.AllChats = true;
        client.ReplyFactory = command => new AgentReply
        {
            RequestId = command.RequestId,
            Kind = AgentReplyKinds.Response,
            PayloadJson = AgentPayloadJson.Serialize(new[]
            {
                new ChatSummaryDto { ChatId = Guid.NewGuid(), Title = "Saved history" }
            })
        };

        await history.LoadAsync();

        Assert.False(chat.CanUseMcp);
        Assert.Single(history.Chats);
    }

    [Fact]
    public void MissingMcpShowsInstallGuidance()
    {
        var viewModel = new ChatViewModel(new ViewModelTestClient());

        viewModel.SetAgentStatus(new AgentStatus
        {
            IsConnected = false,
            Message = "rvt-mcp was not found. Install it, then reconnect."
        });

        Assert.Contains("rvt-mcp", viewModel.McpGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("установите", viewModel.McpGuidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovalDisplaysExactToolArguments()
    {
        const string arguments = "{\"wall_type\":\"Concrete 200\",\"height\":3.25}";
        var viewModel = new ChatViewModel(new ViewModelTestClient());
        viewModel.SetActiveModel(new ActiveModelInfo
        {
            Identity = ModelIdentity.ForSaved(@"C:\Projects\Current.rvt"),
            IsOpen = true
        });

        viewModel.ApplyAgentReply(new AgentReply
        {
            Kind = AgentReplyKinds.Approval,
            PayloadJson = AgentPayloadJson.Serialize(new ToolApprovalPrompt
            {
                ConversationId = Guid.NewGuid(),
                ModelIdentity = viewModel.ActiveModel.Identity,
                Approval = new ToolApprovalRequest
                {
                    CallId = "call-1",
                    ToolName = "revit_create_wall",
                    ArgumentsJson = arguments,
                    Summary = "Create a wall"
                }
            })
        });

        Assert.NotNull(viewModel.PendingApproval);
        Assert.Equal(arguments, viewModel.PendingApproval!.Approval.ArgumentsJson);
    }

    [Fact]
    public void StreamingReplyRemainsOneUpdatedMessage()
    {
        var viewModel = new ChatViewModel(new ViewModelTestClient());
        viewModel.OpenTranscript(new ChatTranscriptDto
        {
            ChatId = Guid.NewGuid(),
            ModelIdentity = ModelIdentity.ForSaved(@"C:\Projects\Current.rvt"),
            ProviderId = "openai",
            ModelId = "gpt-4o-mini"
        });

        viewModel.ApplyAgentReply(Event("assistant_delta", "Привет "));
        viewModel.ApplyAgentReply(Event("assistant_delta", "мир"));
        viewModel.ApplyAgentReply(Event("assistant_complete", "Привет мир"));

        var assistant = Assert.Single(viewModel.Messages);
        Assert.Equal("assistant_complete", assistant.Kind);
        Assert.Equal("ИИ", assistant.DisplayName);
        Assert.Equal("Привет мир", assistant.Text);
    }

    [Fact]
    public async Task SubmittedUserMessageIsNotDuplicatedByAgentEvent()
    {
        var client = new ViewModelTestClient();
        var viewModel = CreateReadyChat(client);
        var conversationId = Guid.NewGuid();
        viewModel.OpenTranscript(new ChatTranscriptDto
        {
            ChatId = conversationId,
            ModelIdentity = viewModel.ActiveModel.Identity,
            ProviderId = "openai",
            ModelId = "gpt-4o-mini"
        });
        client.PublishEvents = async (_, callback) =>
        {
            if (callback is not null)
            {
                await callback(Event("user", "Размер помещения 4 на 5 метров?", conversationId));
            }
        };

        await viewModel.SendAsync("Размер помещения 4 на 5 метров?");

        var message = Assert.Single(viewModel.Messages);
        Assert.Equal("user", message.Kind);
        Assert.Equal("Размер помещения 4 на 5 метров?", message.Text);
    }

    [Fact]
    public async Task CreatingChatUsesHistoryCreateCommandAndCurrentModel()
    {
        var client = new ViewModelTestClient();
        var chat = CreateReadyChat(client);
        var id = Guid.NewGuid();
        client.ReplyFactory = command => new AgentReply
        {
            RequestId = command.RequestId,
            Kind = AgentReplyKinds.Response,
            PayloadJson = AgentPayloadJson.Serialize(new HistoryChatRequest { ChatId = id })
        };

        await chat.NewChatAsync();

        var command = Assert.Single(client.Commands);
        Assert.Equal("history-create", command.Kind);
        Assert.Equal(chat.ActiveModel.Identity, AgentPayloadJson.Deserialize<HistoryCreateRequest>(command.PayloadJson).ModelIdentity);
        Assert.Equal(id, chat.ConversationId);
    }

    private static ChatViewModel CreateReadyChat(ViewModelTestClient client)
    {
        var viewModel = new ChatViewModel(client);
        viewModel.SetActiveModel(new ActiveModelInfo
        {
            Identity = ModelIdentity.ForSaved(@"C:\Projects\Current.rvt"),
            DisplayName = "Current.rvt",
            IsSaved = true,
            IsOpen = true
        });
        viewModel.SetAgentStatus(new AgentStatus { IsConnected = true, Message = "Connected." });
        viewModel.SetProvider("openai", "gpt-4o-mini");
        return viewModel;
    }

    private static AgentReply Event(string kind, string text, Guid conversationId = default) => new()
    {
        Kind = AgentReplyKinds.Event,
        PayloadJson = AgentPayloadJson.Serialize(new ChatEvent { ConversationId = conversationId, Kind = kind, Text = text })
    };
}
