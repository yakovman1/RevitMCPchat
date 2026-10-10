using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.ViewModels;

public sealed class SettingsViewModel : ObservableViewModel
{
    private readonly IAgentCommandClient _client;
    private string _providerId = "openai";
    private string _displayName = "OpenAI";
    private string _baseUrl = "https://api.openai.com/v1";
    private string _modelId = "gpt-5-mini";
    private string _apiKeyInput = string.Empty;
    private string _folderId = string.Empty;
    private string _scope = "GIGACHAT_API_PERS";
    private string _maxTokens = "4096";
    private bool _supportsTools = true;
    private string _statusMessage = string.Empty;
    private CredentialAvailability _credential = new();
    private bool _isBusy;

    public SettingsViewModel(IAgentCommandClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Providers = new ObservableCollection<ProviderSettingsDto>(CreateDefaultProviders());
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(), () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(() => LoadAsync(), () => !IsBusy);
        DeleteUserKeyCommand = new AsyncRelayCommand(() => DeleteUserKeyAsync(), () => !IsBusy && HasUserKey);
    }

    public event Action<string, string>? ProfileSaved;
    public ObservableCollection<ProviderSettingsDto> Providers { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand DeleteUserKeyCommand { get; }

    public string ProviderId
    {
        get => _providerId;
        set
        {
            var id = value ?? string.Empty;
            if (SetProperty(ref _providerId, id))
            {
                ApiKeyInput = string.Empty;
                LoadProviderDefaults(id);
            }
        }
    }

    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value ?? string.Empty); }
    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value ?? string.Empty); }
    public string ModelId { get => _modelId; set => SetProperty(ref _modelId, value ?? string.Empty); }
    public string ApiKeyInput { get => _apiKeyInput; set => SetProperty(ref _apiKeyInput, value ?? string.Empty); }
    public string FolderId { get => _folderId; set => SetProperty(ref _folderId, value ?? string.Empty); }
    public string Scope { get => _scope; set => SetProperty(ref _scope, value ?? string.Empty); }
    public string MaxTokens { get => _maxTokens; set => SetProperty(ref _maxTokens, value ?? string.Empty); }
    public bool SupportsTools { get => _supportsTools; set => SetProperty(ref _supportsTools, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasUserKey => _credential.HasUserKey;
    public bool HasAdminKey => _credential.HasAdminKey;
    public string EffectiveCredentialSource => _credential.EffectiveSource?.ToLowerInvariant() switch
    {
        "user" => "Ключ пользователя Windows",
        "admin" => "Ключ администратора",
        _ => "Ключ не настроен"
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (LoadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DeleteUserKeyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var reply = await _client.SendAsync(new AgentCommand { Kind = AgentCommandKinds.SettingsGet }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = "Не удалось загрузить настройки.";
                return;
            }

            var snapshot = AgentPayloadJson.Deserialize<SettingsSnapshot>(reply.PayloadJson);
            MergeProviders(snapshot.Providers);
            ProviderId = string.IsNullOrWhiteSpace(snapshot.SelectedProviderId)
                ? Providers[0].ProviderId
                : snapshot.SelectedProviderId;
            var selected = Providers.FirstOrDefault(item => string.Equals(item.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                ApplyProvider(selected);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.SelectedModelId))
            {
                ModelId = snapshot.SelectedModelId;
            }

            ApiKeyInput = string.Empty;
            StatusMessage = "Настройки загружены. Ключи отображаются только как наличие.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(ModelId))
        {
            StatusMessage = "Укажите провайдера и идентификатор модели.";
            return;
        }

        IsBusy = true;
        try
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["folder_id"] = FolderId,
                ["scope"] = Scope,
                ["max_tokens"] = MaxTokens,
                ["supports_tools"] = SupportsTools ? "true" : "false"
            };
            var update = new ProviderSettingsUpdate
            {
                ProviderId = ProviderId.Trim(),
                DisplayName = DisplayName.Trim(),
                BaseUrl = BaseUrl.Trim(),
                ModelId = ModelId.Trim(),
                Parameters = parameters,
                ApiKey = string.IsNullOrWhiteSpace(ApiKeyInput) ? null : ApiKeyInput
            };
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.SettingsSave,
                PayloadJson = AgentPayloadJson.Serialize(update)
            }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = "Не удалось сохранить настройки.";
                return;
            }

            var snapshot = AgentPayloadJson.Deserialize<SettingsSnapshot>(reply.PayloadJson);
            MergeProviders(snapshot.Providers);
            var saved = Providers.FirstOrDefault(item => string.Equals(item.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                ApplyProvider(saved);
            }

            ApiKeyInput = string.Empty;
            StatusMessage = "Настройки сохранены. Введённый ключ очищен.";
            ProfileSaved?.Invoke(ProviderId, ModelId);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteUserKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ProviderId))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var reply = await _client.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.SettingsDeleteKey,
                PayloadJson = AgentPayloadJson.Serialize(new ProviderIdRequest { ProviderId = ProviderId })
            }, cancellationToken);
            if (reply.Kind != AgentReplyKinds.Response)
            {
                StatusMessage = "Не удалось удалить ключ пользователя.";
                return;
            }

            SetCredential(AgentPayloadJson.Deserialize<CredentialAvailability>(reply.PayloadJson));
            ApiKeyInput = string.Empty;
            StatusMessage = HasAdminKey
                ? "Ключ пользователя удалён. Используется подготовленный администратором доступ."
                : "Ключ пользователя удалён.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void MergeProviders(IEnumerable<ProviderSettingsDto> providers)
    {
        foreach (var provider in providers ?? Array.Empty<ProviderSettingsDto>())
        {
            var existing = Providers.FirstOrDefault(item => string.Equals(item.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Providers.Add(provider);
            }
            else
            {
                existing.DisplayName = provider.DisplayName;
                existing.BaseUrl = provider.BaseUrl;
                existing.ModelId = provider.ModelId;
                existing.Parameters = provider.Parameters;
                existing.Credential = provider.Credential;
            }
        }
    }

    private void LoadProviderDefaults(string providerId)
    {
        var provider = Providers.FirstOrDefault(item => string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            SetCredential(new CredentialAvailability { ProviderId = providerId, EffectiveSource = "missing" });
            return;
        }

        ApplyProvider(provider);
    }

    private void ApplyProvider(ProviderSettingsDto provider)
    {
        DisplayName = provider.DisplayName;
        BaseUrl = provider.BaseUrl;
        if (!string.IsNullOrWhiteSpace(provider.ModelId))
        {
            ModelId = provider.ModelId;
        }

        var parameters = provider.Parameters ?? new Dictionary<string, string>();
        FolderId = GetParameter(parameters, "folder_id");
        var scope = GetParameter(parameters, "scope");
        Scope = string.IsNullOrWhiteSpace(scope) ? "GIGACHAT_API_PERS" : scope;
        var maxTokens = GetParameter(parameters, "max_tokens");
        MaxTokens = string.IsNullOrWhiteSpace(maxTokens) ? "4096" : maxTokens;
        SupportsTools = !string.Equals(GetParameter(parameters, "supports_tools"), "false", StringComparison.OrdinalIgnoreCase);
        SetCredential(provider.Credential ?? new CredentialAvailability { ProviderId = provider.ProviderId, EffectiveSource = "missing" });
    }

    private void SetCredential(CredentialAvailability credential)
    {
        _credential = credential;
        OnPropertyChanged(nameof(HasUserKey));
        OnPropertyChanged(nameof(HasAdminKey));
        OnPropertyChanged(nameof(EffectiveCredentialSource));
        (DeleteUserKeyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string GetParameter(IDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) ? value : string.Empty;

    private static IEnumerable<ProviderSettingsDto> CreateDefaultProviders()
    {
        yield return Default("openai", "OpenAI", "https://api.openai.com/v1", "gpt-5-mini");
        yield return Default("anthropic", "Anthropic", "https://api.anthropic.com", "claude-sonnet-5");
        yield return Default("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-3.8-flash");
        yield return Default("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-flash");
        yield return Default("yandex-ai-studio", "Alice AI / Yandex AI Studio", "https://ai.api.cloud.yandex.net/v1", "yandexgpt");
        yield return Default("gigachat", "Sber GigaChat", "https://api.giga.chat", "GigaChat-2-Lite");
        yield return Default("openai-compatible", "OpenAI-compatible API", string.Empty, string.Empty);
    }

    private static ProviderSettingsDto Default(string id, string name, string url, string model) => new()
    {
        ProviderId = id,
        DisplayName = name,
        BaseUrl = url,
        ModelId = model,
        Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Credential = new CredentialAvailability { ProviderId = id, EffectiveSource = "missing" }
    };
}
