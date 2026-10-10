using System;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.ViewModels;

public sealed class ChatMessageItem : ObservableViewModel
{
    private string _kind;
    private string _text;

    public ChatMessageItem(string kind, string text, string toolName = "", string callId = "")
    {
        _kind = kind;
        _text = text;
        ToolName = toolName;
        CallId = callId;
    }

    public string Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => Kind switch
    {
        "user" => "Вы",
        "assistant_delta" or "assistant_complete" => "ИИ",
        "tool_call" => "Вызов инструмента",
        "tool_running" => "Выполнение",
        "tool_result" => "Результат инструмента",
        "tool_error" => "Ошибка инструмента",
        "tool_rejected" => "Вызов отклонён",
        "tool_arguments_rejected" => "Параметры инструмента отклонены",
        "tool_outcome_unknown" => "Исход вызова неизвестен",
        "tool_approval" => "Решение по вызову",
        "turn_limit_reached" => "Лимит действий",
        _ => "Система"
    };

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string ToolName { get; }
    public string CallId { get; }

    public void AppendText(string text) => Text += text;

    public void Complete(string text)
    {
        Kind = "assistant_complete";
        Text = text;
    }
}

public sealed class ChatViewModel : ObservableViewModel
{
    private readonly IAgentCommandClient _client;
    private readonly Func<CancellationToken, Task>? _ensureAgentRunning;
    private readonly SynchronizationContext? _uiContext;
    private ActiveModelInfo _activeModel = new();
    private ToolApprovalPrompt? _pendingApproval;
    private string _conversationModelIdentity = string.Empty;
    private string _providerId = string.Empty;
    private string _modelId = string.Empty;
    private string _draftText = string.Empty;
    private string _statusMessage = "Выберите провайдера в настройках и создайте чат.";
    private string _mcpGuidance = "rvt-mcp не подключён. Установите rvt-mcp и нажмите «Подключить».";
    private bool _isMcpConnected;
    private bool _isSending;

    public ChatViewModel(
        IAgentCommandClient client,
        Func<CancellationToken, Task>? ensureAgentRunning = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ensureAgentRunning = ensureAgentRunning;
        _uiContext = SynchronizationContext.Current;
        Messages = new ObservableCollection<ChatMessageItem>();
        NewChatCommand = new AsyncRelayCommand(() => NewChatAsync(), () => ActiveModel.IsOpen && !IsSending);
        SendCommand = new AsyncRelayCommand(() => SendAsync(DraftText), () => CanSend);
        ApproveCommand = new AsyncRelayCommand(() => DecidePendingApprovalAsync(true), () => CanApprovePending);
        RejectCommand = new AsyncRelayCommand(() => DecidePendingApprovalAsync(false), () => PendingApproval is not null);
        ReconnectCommand = new AsyncRelayCommand(() => ReconnectAsync(), () => !IsSending);
    }

    public event Action? ConversationChanged;
    public ObservableCollection<ChatMessageItem> Messages { get; }
    public ICommand NewChatCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand ReconnectCommand { get; }

    public ActiveModelInfo ActiveModel
    {
        get => _activeModel;
        private set
        {
            if (SetProperty(ref _activeModel, value))
            {
                OnPropertyChanged(nameof(ModelStatus));
                RefreshCommandStates();
            }
        }
    }

    public Guid ConversationId { get; private set; }
    public string ConversationModelIdentity => _conversationModelIdentity;
    public bool IsMcpConnected => _isMcpConnected;
    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                RefreshCommandStates();
            }
        }
    }
    public string ProviderId => _providerId;
    public string ModelId => _modelId;
    public string ModelStatus => ActiveModel.IsOpen ? ActiveModel.DisplayName : "Нет открытой модели Revit";
    public bool CanUseMcp => ActiveModel.IsOpen && IsMcpConnected;
    public bool IsReadOnlyChat => ConversationId != Guid.Empty
        && !string.Equals(_conversationModelIdentity, ActiveModel.Identity, StringComparison.Ordinal);
    public bool CanSend => CanUseMcp
        && ConversationId != Guid.Empty
        && !IsSending
        && !IsReadOnlyChat
        && !string.IsNullOrWhiteSpace(ProviderId)
        && !string.IsNullOrWhiteSpace(ModelId);
    public bool CanApprovePending => PendingApproval is not null
        && ActiveModel.IsOpen
        && string.Equals(PendingApproval.ModelIdentity, ActiveModel.Identity, StringComparison.Ordinal);

    public string DraftText
    {
        get => _draftText;
        set
        {
            if (SetProperty(ref _draftText, value ?? string.Empty))
            {
                RefreshCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string McpGuidance
    {
        get => _mcpGuidance;
        private set => SetProperty(ref _mcpGuidance, value);
    }

    public ToolApprovalPrompt? PendingApproval
    {
        get => _pendingApproval;
        private set
        {
            if (SetProperty(ref _pendingApproval, value))
            {
                OnPropertyChanged(nameof(CanApprovePending));
                RefreshCommandStates();
            }
        }
    }

    public void SetActiveModel(ActiveModelInfo model)
    {
        ActiveModel = model ?? throw new ArgumentNullException(nameof(model));
        OnPropertyChanged(nameof(IsReadOnlyChat));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanUseMcp));
        OnPropertyChanged(nameof(CanApprovePending));
        if (!ActiveModel.IsOpen)
        {
            StatusMessage = "Откройте модель Revit, чтобы использовать MCP. История чатов остаётся доступной.";
        }
        else if (IsReadOnlyChat)
        {
            StatusMessage = "Этот чат относится к другой модели. История доступна для чтения; MCP отключён.";
        }

        RefreshCommandStates();
    }

    public void SetAgentStatus(AgentStatus status)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        SetMcpConnected(status.IsConnected);
        var providerMessage = status.Message ?? string.Empty;
        McpGuidance = status.IsConnected
            ? providerMessage
            : providerMessage.IndexOf("rvt-mcp was not found", StringComparison.OrdinalIgnoreCase) >= 0
                ? "rvt-mcp не найден. Установите его: https://github.com/yakovman1/rvt-mcp, затем нажмите «Подключить»."
                : string.IsNullOrWhiteSpace(providerMessage)
                    ? "rvt-mcp не подключён. Проверьте установку и нажмите «Подключить»."
                    : providerMessage;
        if (!status.IsConnected)
        {
            StatusMessage = McpGuidance;
        }
    }

    public void SetLocalAgentGuidance(string message) => StatusMessage = message;

    public void SetProvider(string providerId, string modelId)
    {
        SetProperty(ref _providerId, providerId?.Trim() ?? string.Empty, nameof(ProviderId));
        SetProperty(ref _modelId, modelId?.Trim() ?? string.Empty, nameof(ModelId));
        RefreshCommandStates();
    }

    public void OpenTranscript(ChatTranscriptDto transcript)
    {
        if (transcript is null)
        {
            throw new ArgumentNullException(nameof(transcript));
        }

        ConversationId = transcript.ChatId;
        OnPropertyChanged(nameof(ConversationId));
        _conversationModelIdentity = transcript.ModelIdentity;
        OnPropertyChanged(nameof(ConversationModelIdentity));
        SetProvider(transcript.ProviderId, transcript.ModelId);
        Messages.Clear();
        foreach (var item in transcript.Events ?? new System.Collections.Generic.List<ChatEvent>())
        {
            Messages.Add(new ChatMessageItem(item.Kind, item.Text, item.ToolName, item.CallId));
        }

        PendingApproval = null;
        OnPropertyChanged(nameof(IsReadOnlyChat));
        OnPropertyChanged(nameof(CanSend));
        StatusMessage = IsReadOnlyChat
            ? "Этот чат относится к другой модели. История доступна для чтения; MCP отключён."
            : "Чат открыт.";
        RefreshCommandStates();
        ConversationChanged?.Invoke();
    }

    public void ClearConversation(Guid conversationId)
    {
        if (ConversationId != conversationId)
        {
            return;
        }

        ConversationId = Guid.Empty;
        _conversationModelIdentity = string.Empty;
        OnPropertyChanged(nameof(ConversationId));
        OnPropertyChanged(nameof(ConversationModelIdentity));
        OnPropertyChanged(nameof(IsReadOnlyChat));
        Messages.Clear();
        PendingApproval = null;
        StatusMessage = "Чат удалён. Создайте новый чат, чтобы продолжить.";
        RefreshCommandStates();
        ConversationChanged?.Invoke();
    }

    public async Task NewChatAsync(CancellationToken cancellationToken = default)
    {
        if (!ActiveModel.IsOpen || string.IsNullOrWhiteSpace(ActiveModel.Identity))
        {
            StatusMessage = "Откройте модель Revit перед созданием чата.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(ModelId))
        {
            StatusMessage = "Сначала выберите и сохраните провайдера и модель во вкладке «Настройки».";
            return;
        }

        try
        {
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.HistoryCreate,
                PayloadJson = AgentPayloadJson.Serialize(new HistoryCreateRequest { ModelIdentity = ActiveModel.Identity })
            }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = GetReplyMessage(reply, "Не удалось создать чат. Проверьте настройки провайдера.");
                return;
            }

            var result = AgentPayloadJson.Deserialize<HistoryChatRequest>(reply.PayloadJson);
            ConversationId = result.ChatId;
            _conversationModelIdentity = ActiveModel.Identity;
            OnPropertyChanged(nameof(ConversationId));
            OnPropertyChanged(nameof(ConversationModelIdentity));
            Messages.Clear();
            PendingApproval = null;
            StatusMessage = "Новый чат готов.";
            OnPropertyChanged(nameof(IsReadOnlyChat));
            RefreshCommandStates();
            ConversationChanged?.Invoke();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!CanSend)
        {
            StatusMessage = IsReadOnlyChat
                ? "Этот чат относится к другой модели. Откройте исходную модель или создайте новый чат."
                : !ActiveModel.IsOpen
                    ? "Откройте модель Revit перед отправкой сообщения."
                    : !IsMcpConnected
                        ? McpGuidance
                        : "Создайте чат и выберите провайдера перед отправкой сообщения.";
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        DraftText = string.Empty;
        IsSending = true;
        Messages.Add(new ChatMessageItem("user", text));
        try
        {
            var request = new ChatTurnRequest
            {
                ConversationId = ConversationId,
                ModelIdentity = ActiveModel.Identity,
                ProviderId = ProviderId,
                ModelId = ModelId,
                UserText = text
            };
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.Send,
                PayloadJson = AgentPayloadJson.Serialize(request)
            }, HandleAgentReplyAsync, cancellationToken);

            if (reply.Kind == AgentReplyKinds.Error)
            {
                StatusMessage = GetReplyMessage(reply, "Не удалось выполнить запрос.");
                if (reply.ErrorCode == "mcp_missing")
                {
                    McpGuidance = StatusMessage;
                    SetMcpConnected(false);
                }
            }
            else
            {
                StatusMessage = "Ответ завершён.";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    public void ApplyAgentReply(AgentReply reply)
    {
        if (reply is null)
        {
            throw new ArgumentNullException(nameof(reply));
        }

        if (reply.Kind == AgentReplyKinds.Approval)
        {
            var prompt = AgentPayloadJson.Deserialize<ToolApprovalPrompt>(reply.PayloadJson);
            if (ConversationId != Guid.Empty && prompt.ConversationId != ConversationId)
            {
                return;
            }

            PendingApproval = prompt;
            StatusMessage = "Требуется подтверждение вызова инструмента.";
            return;
        }

        if (reply.Kind != AgentReplyKinds.Event)
        {
            return;
        }

        var chatEvent = AgentPayloadJson.Deserialize<ChatEvent>(reply.PayloadJson);
        if (ConversationId != Guid.Empty
            && chatEvent.ConversationId != Guid.Empty
            && chatEvent.ConversationId != ConversationId)
        {
            return;
        }

        if (chatEvent.Kind == "assistant_delta"
            && Messages.Count > 0
            && Messages[Messages.Count - 1].Kind == "assistant_delta")
        {
            Messages[Messages.Count - 1].AppendText(chatEvent.Text);
            return;
        }

        if (chatEvent.Kind == "assistant_complete"
            && Messages.Count > 0
            && Messages[Messages.Count - 1].Kind == "assistant_delta")
        {
            Messages[Messages.Count - 1].Complete(chatEvent.Text);
            return;
        }

        if (chatEvent.Kind == "user"
            && Messages.Count > 0
            && Messages[Messages.Count - 1].Kind == "user"
            && string.Equals(Messages[Messages.Count - 1].Text, chatEvent.Text, StringComparison.Ordinal))
        {
            return;
        }

        Messages.Add(new ChatMessageItem(chatEvent.Kind, chatEvent.Text, chatEvent.ToolName, chatEvent.CallId));
        if (chatEvent.Kind is "tool_result" or "tool_error" or "tool_rejected" or "tool_outcome_unknown")
        {
            PendingApproval = null;
        }
    }

    public async Task ReconnectAsync()
    {
        try
        {
            if (_ensureAgentRunning is not null)
            {
                await _ensureAgentRunning(CancellationToken.None);
            }

            await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.ModelActive,
                PayloadJson = AgentPayloadJson.Serialize(ActiveModel)
            }, CancellationToken.None);
            var reply = await _client.SendAsync(new AgentCommand { Kind = AgentCommandKinds.Reconnect }, CancellationToken.None);
            if (reply.Kind == AgentReplyKinds.Response)
            {
                SetAgentStatus(AgentPayloadJson.Deserialize<AgentStatus>(reply.PayloadJson));
            }
            else
            {
                StatusMessage = GetReplyMessage(reply, "Не удалось подключиться к rvt-mcp.");
            }
        }
        catch (Exception exception)
        {
            SetAgentStatus(new AgentStatus { IsConnected = false, Message = exception.Message });
        }
    }

    public void ApplyLocalError(string message)
    {
        StatusMessage = message ?? string.Empty;
    }

    private async Task DecidePendingApprovalAsync(bool approve)
    {
        var prompt = PendingApproval;
        if (prompt is null)
        {
            return;
        }

        var sameModel = ActiveModel.IsOpen
            && string.Equals(prompt.ModelIdentity, ActiveModel.Identity, StringComparison.Ordinal);
        var shouldApprove = approve && sameModel;
        if (approve && !sameModel)
        {
            StatusMessage = "Активная модель изменилась. Вызов отклонён.";
        }

        try
        {
            var decision = new ToolApprovalDecision
            {
                ConversationId = prompt.ConversationId,
                CallId = prompt.Approval.CallId,
                ActiveModelIdentity = ActiveModel.Identity,
                Approved = shouldApprove
            };
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = shouldApprove ? AgentCommandKinds.Approve : AgentCommandKinds.Reject,
                PayloadJson = AgentPayloadJson.Serialize(decision)
            }, CancellationToken.None);
            if (reply.Kind == AgentReplyKinds.Error)
            {
                StatusMessage = GetReplyMessage(reply, "Не удалось отправить решение.");
            }

            PendingApproval = null;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private Task HandleAgentReplyAsync(AgentReply reply)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            ApplyAgentReply(reply);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                ApplyAgentReply(reply);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task;
    }

    private void SetMcpConnected(bool value)
    {
        if (SetProperty(ref _isMcpConnected, value, nameof(IsMcpConnected)))
        {
            OnPropertyChanged(nameof(CanUseMcp));
            OnPropertyChanged(nameof(CanSend));
            RefreshCommandStates();
        }
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanUseMcp));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanApprovePending));
        (NewChatCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SendCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApproveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RejectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string GetReplyMessage(AgentReply reply, string fallback)
    {
        if (reply.ErrorCode == "mcp_missing")
        {
            return "rvt-mcp не найден. Установите rvt-mcp и нажмите «Подключить».";
        }

        if (reply.ErrorCode == "active_model_changed" || reply.ErrorCode == "model_mismatch")
        {
            return "Активная модель изменилась. Обновите состояние и попробуйте снова.";
        }

        return string.IsNullOrWhiteSpace(reply.ErrorCode) ? fallback : fallback + " (" + reply.ErrorCode + ")";
    }
}
