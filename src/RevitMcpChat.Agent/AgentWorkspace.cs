using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.History;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.Settings;

namespace RevitMcpChat.Agent;

/// <summary>
/// Shared local services used by the console workflow and the Revit panel.
/// </summary>
public sealed class AgentWorkspace : IAsyncDisposable
{
    private readonly ISecretStore _secretStore;
    private readonly CredentialResolver _credentialResolver;
    private readonly SettingsRepository _settings;
    private readonly SqliteChatHistory _history;
    private readonly SecretRedactor _redactor;

    public AgentWorkspace(
        string? localApplicationDataPath = null,
        ISecretStore? secretStore = null,
        IAdminKeySource? adminKeySource = null)
    {
        _redactor = new SecretRedactor();
        _history = new SqliteChatHistory(localApplicationDataPath, _redactor);
        _settings = new SettingsRepository(localApplicationDataPath);
        _secretStore = secretStore ?? new DpapiSecretStore(localApplicationDataPath);
        _credentialResolver = new CredentialResolver(
            _secretStore,
            adminKeySource ?? new EmptyAdminKeySource());
    }

    public IChatHistory History => _history;

    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        _settings.LoadAsync(cancellationToken);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _settings.SaveAsync(settings, cancellationToken);

    public async Task SetActiveProviderAsync(
        ProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ProviderId) || string.IsNullOrWhiteSpace(profile.ModelId))
        {
            throw new ArgumentException("A provider id and model id are required.", nameof(profile));
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        settings.Providers ??= new List<ProviderProfile>();
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
        await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> CreateChatAsync(
        string modelIdentity,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = settings.Providers?.FirstOrDefault(item =>
            string.Equals(item.ProviderId, settings.SelectedProviderId, StringComparison.OrdinalIgnoreCase));
        var modelId = string.IsNullOrWhiteSpace(settings.SelectedModelId)
            ? profile?.ModelId
            : settings.SelectedModelId;
        if (profile is null || string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("Select a provider and model before creating a chat.");
        }

        var chatId = await _history.CreateAsync(
                modelIdentity,
                profile.ProviderId,
                modelId,
                cancellationToken)
            .ConfigureAwait(false);
        settings.LastModelIdentity = modelIdentity;
        await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return chatId;
    }

    public async Task<IReadOnlyList<ChatSummary>> ListHistoryAsync(
        bool allChats,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await ListHistoryAsync(settings.LastModelIdentity, allChats, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ChatSummary>> ListHistoryAsync(
        string? currentModelIdentity,
        bool allChats,
        CancellationToken cancellationToken = default)
    {
        if (!allChats && string.IsNullOrWhiteSpace(currentModelIdentity))
        {
            return Task.FromResult<IReadOnlyList<ChatSummary>>(Array.Empty<ChatSummary>());
        }

        return _history.ListAsync(allChats ? null : currentModelIdentity, cancellationToken);
    }

    public Task<ChatTranscript> OpenChatAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        _history.LoadAsync(chatId, cancellationToken);

    public async Task<ProviderProfile> GetProfileForChatAsync(
        Guid chatId,
        CancellationToken cancellationToken = default)
    {
        var transcript = await _history.LoadAsync(chatId, cancellationToken).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var savedProfile = settings.Providers?.FirstOrDefault(item =>
            string.Equals(item.ProviderId, transcript.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (savedProfile is null)
        {
            return new ProviderProfile
            {
                ProviderId = transcript.ProviderId,
                DisplayName = transcript.ProviderId,
                ModelId = transcript.ModelId
            };
        }

        return new ProviderProfile
        {
            ProviderId = savedProfile.ProviderId,
            DisplayName = savedProfile.DisplayName,
            BaseUrl = savedProfile.BaseUrl,
            ModelId = transcript.ModelId,
            Parameters = new Dictionary<string, string>(savedProfile.Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public async Task SetUserKeyAsync(
        string providerId,
        string key,
        CancellationToken cancellationToken = default)
    {
        await _secretStore.SaveAsync(providerId, key, cancellationToken).ConfigureAwait(false);
        _redactor.RegisterSecret(key);
    }

    public async Task<ResolvedCredential> RemoveUserKeyAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await _secretStore.DeleteAsync(providerId, cancellationToken).ConfigureAwait(false);
        return await ResolveCredentialAsync(providerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResolvedCredential> ResolveCredentialAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var credential = await _credentialResolver.ResolveAsync(providerId, cancellationToken).ConfigureAwait(false);
        _redactor.RegisterSecret(credential.Value);
        return credential;
    }

    public ChatEngine CreateChatEngine(IChatProvider provider, IMcpGateway gateway, ToolPolicy? toolPolicy = null) =>
        new(provider, gateway, toolPolicy, _history);

    public ValueTask DisposeAsync() => _history.DisposeAsync();
}
