using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.ViewModels;

public sealed class HistoryViewModel : ObservableViewModel
{
    private readonly IAgentCommandClient _client;
    private ChatSummaryDto? _selectedChat;
    private string _currentModelIdentity = string.Empty;
    private string _renameTitle = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _allChats;
    private bool _isChatRunning;

    public HistoryViewModel(IAgentCommandClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Chats = new ObservableCollection<ChatSummaryDto>();
        LoadCommand = new AsyncRelayCommand(() => LoadAsync());
        OpenSelectedCommand = new AsyncRelayCommand(() => OpenSelectedAsync(), () => SelectedChat is not null && !IsChatRunning);
        RenameSelectedCommand = new AsyncRelayCommand(() => RenameSelectedAsync(RenameTitle), () => SelectedChat is not null && !IsChatRunning);
        DeleteSelectedCommand = new AsyncRelayCommand(() => DeleteSelectedAsync(), () => SelectedChat is not null && !IsChatRunning);
    }

    public event Action<ChatTranscriptDto>? ChatOpened;
    public event Action<Guid>? ChatDeleted;
    public ObservableCollection<ChatSummaryDto> Chats { get; }
    public ICommand LoadCommand { get; }
    public ICommand OpenSelectedCommand { get; }
    public ICommand RenameSelectedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }

    public string CurrentModelIdentity
    {
        get => _currentModelIdentity;
        set => SetProperty(ref _currentModelIdentity, value ?? string.Empty);
    }

    public bool AllChats
    {
        get => _allChats;
        set => SetProperty(ref _allChats, value);
    }

    public bool IsChatRunning => _isChatRunning;

    public void SetChatRunning(bool value)
    {
        if (SetProperty(ref _isChatRunning, value, nameof(IsChatRunning)))
        {
            RefreshCommands();
        }
    }

    public ChatSummaryDto? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (SetProperty(ref _selectedChat, value))
            {
                RenameTitle = value?.Title ?? string.Empty;
                RefreshCommands();
            }
        }
    }

    public string RenameTitle
    {
        get => _renameTitle;
        set
        {
            if (SetProperty(ref _renameTitle, value ?? string.Empty))
            {
                (RenameSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task LoadAsync(bool? allChats = null, CancellationToken cancellationToken = default)
    {
        if (allChats.HasValue)
        {
            AllChats = allChats.Value;
        }

        try
        {
            var request = new HistoryListRequest
            {
                ModelIdentity = CurrentModelIdentity,
                AllChats = AllChats
            };
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.HistoryList,
                PayloadJson = AgentPayloadJson.Serialize(request)
            }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = "Не удалось загрузить историю.";
                return;
            }

            var previousId = SelectedChat?.ChatId;
            var rows = AgentPayloadJson.Deserialize<System.Collections.Generic.List<ChatSummaryDto>>(reply.PayloadJson);
            Chats.Clear();
            foreach (var row in rows)
            {
                Chats.Add(row);
            }

            if (previousId.HasValue)
            {
                SelectedChat = Chats.FirstOrDefault(item => item.ChatId == previousId.Value);
            }

            StatusMessage = Chats.Count == 0 ? "Чатов нет." : "Загружено чатов: " + Chats.Count;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public async Task OpenSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedChat;
        if (selected is null)
        {
            return;
        }

        try
        {
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.HistoryOpen,
                PayloadJson = AgentPayloadJson.Serialize(new HistoryChatRequest { ChatId = selected.ChatId })
            }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = "Не удалось открыть выбранный чат.";
                return;
            }

            ChatOpened?.Invoke(AgentPayloadJson.Deserialize<ChatTranscriptDto>(reply.PayloadJson));
            StatusMessage = "Чат открыт.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public async Task RenameSelectedAsync(string title, CancellationToken cancellationToken = default)
    {
        var selected = SelectedChat;
        if (selected is null || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.HistoryRename,
                PayloadJson = AgentPayloadJson.Serialize(new HistoryRenameRequest
                {
                    ChatId = selected.ChatId,
                    Title = title.Trim()
                })
            }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = "Не удалось переименовать чат.";
                return;
            }

            var index = Chats.IndexOf(selected);
            var renamed = new ChatSummaryDto
            {
                ChatId = selected.ChatId,
                Title = title.Trim(),
                ModelIdentity = selected.ModelIdentity,
                ProviderId = selected.ProviderId,
                ModelId = selected.ModelId,
                CreatedAt = selected.CreatedAt,
                UpdatedAt = selected.UpdatedAt
            };
            if (index >= 0)
            {
                Chats[index] = renamed;
            }

            SelectedChat = renamed;
            RenameTitle = renamed.Title;
            StatusMessage = "Название чата обновлено.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedChat;
        if (selected is null)
        {
            return;
        }

        try
        {
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.HistoryDelete,
                PayloadJson = AgentPayloadJson.Serialize(new HistoryChatRequest { ChatId = selected.ChatId })
            }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = "Не удалось удалить чат.";
                return;
            }

            var target = Chats.FirstOrDefault(item => item.ChatId == selected.ChatId);
            if (target is not null)
            {
                Chats.Remove(target);
            }

            SelectedChat = null;
            StatusMessage = "Чат удалён.";
            ChatDeleted?.Invoke(selected.ChatId);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private void RefreshCommands()
    {
        (OpenSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RenameSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
