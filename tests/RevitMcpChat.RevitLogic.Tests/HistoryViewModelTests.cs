using System;
using System.Linq;
using System.Threading.Tasks;
using RevitMCPchat.ViewModels;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.RevitLogic.Tests;

public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task CurrentModelAndAllChatFiltersDiffer()
    {
        var modelA = ModelIdentity.ForSaved(@"C:\Projects\A.rvt");
        var modelB = ModelIdentity.ForSaved(@"C:\Projects\B.rvt");
        var client = new ViewModelTestClient();
        client.ReplyFactory = command =>
        {
            var request = AgentPayloadJson.Deserialize<HistoryListRequest>(command.PayloadJson);
            var items = new[]
            {
                new ChatSummaryDto { ChatId = Guid.NewGuid(), ModelIdentity = modelA, Title = "A" },
                new ChatSummaryDto { ChatId = Guid.NewGuid(), ModelIdentity = modelB, Title = "B" }
            };
            return new AgentReply
            {
                RequestId = command.RequestId,
                Kind = AgentReplyKinds.Response,
                PayloadJson = AgentPayloadJson.Serialize(request.AllChats
                    ? items
                    : items.Where(item => item.ModelIdentity == request.ModelIdentity).ToArray())
            };
        };
        var viewModel = new HistoryViewModel(client) { CurrentModelIdentity = modelA };

        await viewModel.LoadAsync(allChats: false);
        Assert.Single(viewModel.Chats);
        Assert.Equal("A", viewModel.Chats.Single().Title);

        await viewModel.LoadAsync(allChats: true);
        Assert.Equal(2, viewModel.Chats.Count);
        Assert.Contains(client.Commands, command =>
        {
            var request = AgentPayloadJson.Deserialize<HistoryListRequest>(command.PayloadJson);
            return !request.AllChats && request.ModelIdentity == modelA;
        });
        Assert.Contains(client.Commands, command => AgentPayloadJson.Deserialize<HistoryListRequest>(command.PayloadJson).AllChats);
    }

    [Fact]
    public async Task DeleteChatRemovesOnlySelectedChat()
    {
        var client = new ViewModelTestClient();
        var first = new ChatSummaryDto { ChatId = Guid.NewGuid(), Title = "Keep" };
        var selected = new ChatSummaryDto { ChatId = Guid.NewGuid(), Title = "Delete" };
        var viewModel = new HistoryViewModel(client);
        viewModel.Chats.Add(first);
        viewModel.Chats.Add(selected);
        viewModel.SelectedChat = selected;

        await viewModel.DeleteSelectedAsync();

        Assert.Single(viewModel.Chats);
        Assert.Same(first, viewModel.Chats.Single());
        Assert.Equal(selected.ChatId, AgentPayloadJson.Deserialize<HistoryChatRequest>(Assert.Single(client.Commands).PayloadJson).ChatId);
    }

    [Fact]
    public void HistoryMutationsAreDisabledWhileChatResponseIsRunning()
    {
        var viewModel = new HistoryViewModel(new ViewModelTestClient());
        viewModel.Chats.Add(new ChatSummaryDto { ChatId = Guid.NewGuid(), Title = "Active chat" });
        viewModel.SelectedChat = viewModel.Chats[0];
        viewModel.SetChatRunning(true);

        Assert.False(viewModel.OpenSelectedCommand.CanExecute(null));
        Assert.False(viewModel.RenameSelectedCommand.CanExecute(null));
        Assert.False(viewModel.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeletingActiveChatClearsOpenTranscript()
    {
        var client = new ViewModelTestClient();
        var pane = new ChatPaneViewModel(client);
        var id = Guid.NewGuid();
        pane.Chat.OpenTranscript(new ChatTranscriptDto
        {
            ChatId = id,
            ModelIdentity = ModelIdentity.ForSaved(@"C:\Projects\A.rvt"),
            ProviderId = "openai",
            ModelId = "gpt-4o-mini",
            Events = { new ChatEvent { Kind = "user", Text = "This will be removed." } }
        });
        pane.History.Chats.Add(new ChatSummaryDto { ChatId = id, Title = "Active chat" });
        pane.History.SelectedChat = pane.History.Chats[0];

        await pane.History.DeleteSelectedAsync();

        Assert.Equal(Guid.Empty, pane.Chat.ConversationId);
        Assert.Empty(pane.Chat.Messages);
    }
}
