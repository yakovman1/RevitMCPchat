using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.History;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.Providers;
using RevitMcpChat.Agent.Settings;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.Ipc;

public sealed class AgentCommandDispatcher : IAgentCommandDispatcher
{
    private static readonly HashSet<string> SafeParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "folder_id", "scope", "auth_scheme", "auth_header", "supports_tools", "max_tokens"
    };

    private readonly AgentWorkspace _workspace;
    private readonly int _revitPid;
    private readonly string? _serverPath;
    private readonly ModelBoundApprovalGate _approvalGate = new();
    private string _activeModelIdentity = string.Empty;

    public AgentCommandDispatcher(AgentWorkspace workspace, int revitPid, string? serverPath = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        if (revitPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revitPid));
        }

        _revitPid = revitPid;
        _serverPath = serverPath;
    }

    public async Task<AgentReply> DispatchAsync(
        AgentCommand command,
        Func<AgentReply, CancellationToken, Task> publishAsync,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (!AgentCommandKinds.IsSupported(command.Kind))
        {
            return Error(command.RequestId, "invalid_command", "This agent command is not supported.");
        }

        try
        {
            return command.Kind switch
            {
                AgentCommandKinds.Send => await SendAsync(command, publishAsync, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.Approve or AgentCommandKinds.Reject => await DecideAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.HistoryList => await ListHistoryAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.HistoryCreate => await CreateHistoryAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.HistoryOpen => await OpenHistoryAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.HistoryRename => await RenameHistoryAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.HistoryDelete => await DeleteHistoryAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.SettingsGet => await GetSettingsAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.SettingsSave => await SaveSettingsAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.SettingsDeleteKey => await DeleteKeyAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.Reconnect => await ReconnectAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.ModelSaved => await ModelSavedAsync(command, cancellationToken).ConfigureAwait(false),
                AgentCommandKinds.ModelActive => SetActiveModel(command),
                AgentCommandKinds.Shutdown => new AgentReply { RequestId = command.RequestId, Kind = AgentReplyKinds.Shutdown },
                _ => Error(command.RequestId, "invalid_command", "This agent command is not supported.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KeyNotFoundException exception)
        {
            return Error(command.RequestId, "not_found", exception.Message);
        }
        catch (SecretStoreException exception)
        {
            return Error(command.RequestId, "secret_store_error", exception.Message);
        }
        catch (ProviderAuthenticationException exception)
        {
            return Error(command.RequestId, "provider_authentication", exception.Message);
        }
        catch (ProviderRequestException exception)
        {
            return Error(command.RequestId, "provider_request", exception.Message);
        }
        catch (ProviderCapabilityException exception)
        {
            return Error(command.RequestId, "provider_capability", exception.Message);
        }
        catch (McpConnectionException exception)
        {
            return Error(command.RequestId, "mcp_unavailable", exception.Message);
        }
        catch (JsonException)
        {
            return Error(command.RequestId, "invalid_payload", "The agent command payload is not valid JSON.");
        }
        catch (Exception)
        {
            return Error(command.RequestId, "agent_error", "The local agent could not complete this request.");
        }
    }

    private async Task<AgentReply> SendAsync(
        AgentCommand command,
        Func<AgentReply, CancellationToken, Task> publishAsync,
        CancellationToken cancellationToken)
    {
        var request = AgentPayloadJson.Deserialize<ChatTurnRequest>(command.PayloadJson);
        if (string.IsNullOrWhiteSpace(request.ModelIdentity)
            || !string.Equals(Volatile.Read(ref _activeModelIdentity), request.ModelIdentity, StringComparison.Ordinal))
        {
            return Error(command.RequestId, "active_model_changed", "The active Revit model changed. Refresh the model status and retry.");
        }

        var transcript = await _workspace.OpenChatAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(transcript.ModelIdentity, request.ModelIdentity, StringComparison.Ordinal))
        {
            return Error(command.RequestId, "model_mismatch", "This chat belongs to a different Revit model.");
        }

        if (!string.Equals(transcript.ProviderId, request.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(transcript.ModelId, request.ModelId, StringComparison.Ordinal))
        {
            return Error(command.RequestId, "provider_mismatch", "This chat is saved for a different provider or model.");
        }

        var profile = await _workspace.GetProfileForChatAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        var credential = await _workspace.ResolveCredentialAsync(profile.ProviderId, cancellationToken).ConfigureAwait(false);
        if (credential.Source == CredentialSource.Missing)
        {
            return Error(command.RequestId, "missing_key", $"No key is configured for '{profile.ProviderId}'. Open Settings to add one.");
        }

        var serverPath = ResolveServerPath();
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            return Error(command.RequestId, "mcp_missing", "rvt-mcp was not found. Install rvt-mcp, then reconnect.");
        }

        using var httpClient = new HttpClient();
        var provider = ProviderFactory.Create(profile, credential, httpClient);
        await using var gateway = new McpGateway();
        await gateway.ConnectAsync(_revitPid, serverPath, cancellationToken).ConfigureAwait(false);

        var guardedGateway = new ModelBoundMcpGateway(gateway, request.ModelIdentity, () => Volatile.Read(ref _activeModelIdentity));
        var sink = new PipeChatEventSink(command.RequestId, request, publishAsync, _approvalGate);
        await _workspace.CreateChatEngine(provider, guardedGateway).RunAsync(request, sink, cancellationToken).ConfigureAwait(false);
        return Response(command.RequestId);
    }

    private async Task<AgentReply> DecideAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var decision = AgentPayloadJson.Deserialize<ToolApprovalDecision>(command.PayloadJson);
        if (command.Kind == AgentCommandKinds.Reject)
        {
            decision.Approved = false;
        }

        var reply = await _approvalGate.DecideAsync(decision, cancellationToken).ConfigureAwait(false);
        reply.RequestId = command.RequestId;
        return reply;
    }

    private async Task<AgentReply> ListHistoryAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var request = AgentPayloadJson.Deserialize<HistoryListRequest>(command.PayloadJson);
        var summaries = await _workspace.ListHistoryAsync(request.ModelIdentity, request.AllChats, cancellationToken)
            .ConfigureAwait(false);
        var result = summaries.Select(summary => new ChatSummaryDto
        {
            ChatId = summary.ChatId,
            Title = summary.Title,
            ModelIdentity = summary.ModelIdentity,
            ProviderId = summary.ProviderId,
            ModelId = summary.ModelId,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt
        }).ToList();
        return Response(command.RequestId, AgentPayloadJson.Serialize(result));
    }

    private async Task<AgentReply> CreateHistoryAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var request = AgentPayloadJson.Deserialize<HistoryCreateRequest>(command.PayloadJson);
        if (string.IsNullOrWhiteSpace(request.ModelIdentity))
        {
            return Error(command.RequestId, "invalid_model", "Open a Revit model before starting a chat.");
        }

        var chatId = await _workspace.CreateChatAsync(request.ModelIdentity, cancellationToken).ConfigureAwait(false);
        return Response(command.RequestId, AgentPayloadJson.Serialize(new HistoryChatRequest { ChatId = chatId }));
    }

    private async Task<AgentReply> OpenHistoryAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var request = AgentPayloadJson.Deserialize<HistoryChatRequest>(command.PayloadJson);
        var transcript = await _workspace.OpenChatAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        var result = new ChatTranscriptDto
        {
            ChatId = transcript.ChatId,
            Title = transcript.Title,
            ModelIdentity = transcript.ModelIdentity,
            ProviderId = transcript.ProviderId,
            ModelId = transcript.ModelId,
            Events = transcript.Events.ToList()
        };
        return Response(command.RequestId, AgentPayloadJson.Serialize(result));
    }

    private async Task<AgentReply> RenameHistoryAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var request = AgentPayloadJson.Deserialize<HistoryRenameRequest>(command.PayloadJson);
        await _workspace.History.RenameAsync(request.ChatId, request.Title, cancellationToken).ConfigureAwait(false);
        return Response(command.RequestId);
    }

    private async Task<AgentReply> DeleteHistoryAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var request = AgentPayloadJson.Deserialize<HistoryChatRequest>(command.PayloadJson);
        await _workspace.History.DeleteAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        return Response(command.RequestId);
    }

    private async Task<AgentReply> GetSettingsAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var settings = await _workspace.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return Response(command.RequestId, AgentPayloadJson.Serialize(await CreateSettingsSnapshotAsync(settings, cancellationToken)
            .ConfigureAwait(false)));
    }

    private async Task<AgentReply> SaveSettingsAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var update = AgentPayloadJson.Deserialize<ProviderSettingsUpdate>(command.PayloadJson);
        if (string.IsNullOrWhiteSpace(update.ProviderId) || string.IsNullOrWhiteSpace(update.ModelId))
        {
            return Error(command.RequestId, "invalid_settings", "A provider ID and model ID are required.");
        }

        var settings = await _workspace.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.Providers ??= new List<ProviderProfile>();
        var profile = new ProviderProfile
        {
            ProviderId = update.ProviderId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(update.DisplayName) ? update.ProviderId.Trim() : update.DisplayName.Trim(),
            BaseUrl = update.BaseUrl?.Trim() ?? string.Empty,
            ModelId = update.ModelId.Trim(),
            Parameters = (update.Parameters ?? new Dictionary<string, string>())
                .Where(pair => SafeParameterNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        };
        var index = settings.Providers.FindIndex(item =>
            string.Equals(item.ProviderId, profile.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            settings.Providers.Add(profile);
        }
        else
        {
            settings.Providers[index] = profile;
        }

        settings.SelectedProviderId = profile.ProviderId;
        settings.SelectedModelId = profile.ModelId;
        await _workspace.SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(update.ApiKey))
        {
            await _workspace.SetUserKeyAsync(profile.ProviderId, update.ApiKey, cancellationToken).ConfigureAwait(false);
            update.ApiKey = null;
        }

        return Response(command.RequestId, AgentPayloadJson.Serialize(await CreateSettingsSnapshotAsync(settings, cancellationToken)
            .ConfigureAwait(false)));
    }

    private async Task<AgentReply> DeleteKeyAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var request = AgentPayloadJson.Deserialize<ProviderIdRequest>(command.PayloadJson);
        if (string.IsNullOrWhiteSpace(request.ProviderId))
        {
            return Error(command.RequestId, "invalid_settings", "A provider ID is required.");
        }

        await _workspace.RemoveUserKeyAsync(request.ProviderId, cancellationToken).ConfigureAwait(false);
        return Response(command.RequestId, AgentPayloadJson.Serialize(
            await _workspace.GetCredentialAvailabilityAsync(request.ProviderId, cancellationToken).ConfigureAwait(false)));
    }

    private async Task<AgentReply> ReconnectAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var serverPath = ResolveServerPath();
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            return Response(command.RequestId, AgentPayloadJson.Serialize(new AgentStatus
            {
                RevitPid = _revitPid,
                IsConnected = false,
                Message = "rvt-mcp was not found. Install it, then reconnect."
            }));
        }

        await using var gateway = new McpGateway();
        await gateway.ConnectAsync(_revitPid, serverPath, cancellationToken).ConfigureAwait(false);
        var tools = await gateway.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        return Response(command.RequestId, AgentPayloadJson.Serialize(new AgentStatus
        {
            RevitPid = _revitPid,
            IsConnected = true,
            Message = $"Connected to rvt-mcp for Revit PID {_revitPid}; {tools.Count} tools are available."
        }));
    }

    private async Task<AgentReply> ModelSavedAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        var change = AgentPayloadJson.Deserialize<ModelIdentityChange>(command.PayloadJson);
        if (!change.ShouldBindExistingChats)
        {
            return Error(command.RequestId, "invalid_model_change", "Only a first save can bind an unsaved model identity.");
        }

        await _workspace.BindUnsavedModelAsync(change.PreviousIdentity, change.CurrentIdentity, cancellationToken)
            .ConfigureAwait(false);
        return Response(command.RequestId);
    }

    private AgentReply SetActiveModel(AgentCommand command)
    {
        var model = AgentPayloadJson.Deserialize<ActiveModelInfo>(command.PayloadJson);
        Volatile.Write(ref _activeModelIdentity, model.IsOpen ? model.Identity : string.Empty);
        return Response(command.RequestId, AgentPayloadJson.Serialize(model));
    }

    private async Task<SettingsSnapshot> CreateSettingsSnapshotAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var profiles = settings.Providers ?? new List<ProviderProfile>();
        var providerIds = profiles.Select(profile => profile.ProviderId).ToList();
        if (!string.IsNullOrWhiteSpace(settings.SelectedProviderId)
            && !providerIds.Contains(settings.SelectedProviderId, StringComparer.OrdinalIgnoreCase))
        {
            providerIds.Add(settings.SelectedProviderId);
        }

        var result = new List<ProviderSettingsDto>();
        foreach (var providerId in providerIds)
        {
            var profile = profiles.FirstOrDefault(item =>
                string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            var credential = await _workspace.GetCredentialAvailabilityAsync(providerId, cancellationToken)
                .ConfigureAwait(false);
            result.Add(new ProviderSettingsDto
            {
                ProviderId = providerId,
                DisplayName = profile?.DisplayName ?? providerId,
                BaseUrl = profile?.BaseUrl ?? string.Empty,
                ModelId = profile?.ModelId ?? settings.SelectedModelId,
                Parameters = profile?.Parameters?.Where(pair => SafeParameterNames.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Credential = credential
            });
        }

        return new SettingsSnapshot
        {
            SelectedProviderId = settings.SelectedProviderId,
            SelectedModelId = settings.SelectedModelId,
            LastModelIdentity = settings.LastModelIdentity,
            Providers = result
        };
    }

    private string? ResolveServerPath() =>
        !string.IsNullOrWhiteSpace(_serverPath) && File.Exists(_serverPath)
            ? _serverPath
            : RvtMcpLocator.FindInstalledServer();

    private static AgentReply Response(Guid requestId, string payloadJson = "{}") => new()
    {
        RequestId = requestId,
        Kind = AgentReplyKinds.Response,
        PayloadJson = payloadJson
    };

    private static AgentReply Error(Guid requestId, string errorCode, string message) => new()
    {
        RequestId = requestId,
        Kind = AgentReplyKinds.Error,
        ErrorCode = errorCode,
        PayloadJson = AgentPayloadJson.Serialize(new ErrorPayload { Message = message })
    };

    private sealed class PipeChatEventSink : IChatEventSink
    {
        private readonly Guid _requestId;
        private readonly ChatTurnRequest _turn;
        private readonly Func<AgentReply, CancellationToken, Task> _publishAsync;
        private readonly ModelBoundApprovalGate _approvalGate;

        public PipeChatEventSink(
            Guid requestId,
            ChatTurnRequest turn,
            Func<AgentReply, CancellationToken, Task> publishAsync,
            ModelBoundApprovalGate approvalGate)
        {
            _requestId = requestId;
            _turn = turn;
            _publishAsync = publishAsync;
            _approvalGate = approvalGate;
        }

        public Task PublishAsync(ChatEvent chatEvent, CancellationToken cancellationToken) =>
            _publishAsync(new AgentReply
            {
                RequestId = _requestId,
                Kind = AgentReplyKinds.Event,
                PayloadJson = AgentPayloadJson.Serialize(chatEvent)
            }, cancellationToken);

        public Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
            var prompt = new ToolApprovalPrompt
            {
                ConversationId = _turn.ConversationId,
                ModelIdentity = _turn.ModelIdentity,
                Approval = request
            };
            return _approvalGate.RequestAsync(prompt, (item, token) => _publishAsync(new AgentReply
            {
                RequestId = _requestId,
                Kind = AgentReplyKinds.Approval,
                PayloadJson = AgentPayloadJson.Serialize(item)
            }, token), cancellationToken);
        }
    }

    private sealed class ModelBoundMcpGateway : IMcpGateway
    {
        private readonly IMcpGateway _inner;
        private readonly string _boundIdentity;
        private readonly Func<string> _activeIdentity;

        public ModelBoundMcpGateway(IMcpGateway inner, string boundIdentity, Func<string> activeIdentity)
        {
            _inner = inner;
            _boundIdentity = boundIdentity;
            _activeIdentity = activeIdentity;
        }

        public Task ConnectAsync(int revitPid, string serverPath, CancellationToken cancellationToken) =>
            _inner.ConnectAsync(revitPid, serverPath, cancellationToken);

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
        {
            EnsureModelUnchanged();
            return _inner.ListToolsAsync(cancellationToken);
        }

        public Task<McpCallOutcome> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
        {
            if (!IsBoundModelActive())
            {
                return Task.FromResult(new McpCallOutcome(
                    "The active Revit model changed. The tool call was blocked before it was sent.",
                    isError: true,
                    outcomeUnknown: false));
            }

            return _inner.CallToolAsync(name, argumentsJson, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private void EnsureModelUnchanged()
        {
            if (!IsBoundModelActive())
            {
                throw new InvalidOperationException("The active Revit model changed before the tool catalog was read.");
            }
        }

        private bool IsBoundModelActive() =>
            string.Equals(_boundIdentity, _activeIdentity(), StringComparison.Ordinal);
    }

    [System.Runtime.Serialization.DataContract]
    private sealed class ErrorPayload
    {
        [System.Runtime.Serialization.DataMember(Order = 1)]
        public string Message { get; set; } = string.Empty;
    }
}
