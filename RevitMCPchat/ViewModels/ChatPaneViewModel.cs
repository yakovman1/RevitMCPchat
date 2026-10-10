using System;
using System.Threading.Tasks;
using System.Windows.Input;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.ViewModels;

public sealed class ChatPaneViewModel : ObservableViewModel
{
    private int _selectedTabIndex;

    public ChatPaneViewModel(IAgentCommandClient client)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }
        Chat = new ChatViewModel(client);
        History = new HistoryViewModel(client);
        Settings = new SettingsViewModel(client);
        NewChatCommand = new AsyncRelayCommand(CreateNewChatAsync, () => Chat.ActiveModel.IsOpen && !Chat.IsSending);
        History.ChatOpened += transcript =>
        {
            Chat.OpenTranscript(transcript);
            SelectedTabIndex = 0;
        };
        History.ChatDeleted += Chat.ClearConversation;
        Chat.ConversationChanged += () => _ = History.LoadAsync();
        Settings.ProfileSaved += (providerId, modelId) =>
        {
            if (Chat.ConversationId == Guid.Empty)
            {
                Chat.SetProvider(providerId, modelId);
            }
        };
        Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.IsSending))
            {
                History.SetChatRunning(Chat.IsSending);
                (NewChatCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
            else if (args.PropertyName == nameof(ChatViewModel.ActiveModel))
            {
                (NewChatCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        };
    }

    public ChatViewModel Chat { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public ICommand NewChatCommand { get; }
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public async Task InitializeAsync()
    {
        await Settings.LoadAsync();
        Chat.SetProvider(Settings.ProviderId, Settings.ModelId);
        History.CurrentModelIdentity = Chat.ActiveModel.IsOpen ? Chat.ActiveModel.Identity : string.Empty;
        await History.LoadAsync(allChats: !Chat.ActiveModel.IsOpen);
        await Chat.ReconnectAsync();
    }

    public void SetActiveModel(ActiveModelInfo model)
    {
        Chat.SetActiveModel(model);
        History.CurrentModelIdentity = model.IsOpen ? model.Identity : string.Empty;
        _ = History.LoadAsync(allChats: !model.IsOpen);
    }

    public void SetStartupGuidance(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)
            && message!.IndexOf("Connected", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Chat.SetLocalAgentGuidance(message!);
        }
    }

    private async Task CreateNewChatAsync()
    {
        Chat.SetProvider(Settings.ProviderId, Settings.ModelId);
        await Chat.NewChatAsync();
    }
}
