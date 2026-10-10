using System.Runtime.CompilerServices;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Settings;

namespace RevitMcpChat.Agent.Providers;

public static class ProviderFactory
{
    public static IChatProvider Create(
        ProviderProfile profile,
        ResolvedCredential credential,
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (credential.Source == CredentialSource.Missing || string.IsNullOrWhiteSpace(credential.Value))
        {
            throw new ProviderAuthenticationException("No API key is configured for the selected provider.");
        }

        var providerId = profile.ProviderId?.Trim() ?? string.Empty;
        var modelId = profile.ModelId?.Trim() ?? string.Empty;
        IChatProvider provider = providerId.ToLowerInvariant() switch
        {
            "deepseek" => new DeepSeekProvider(
                httpClient,
                UriFor(profile.BaseUrl, "https://api.deepseek.com"),
                credential.Value),
            "yandex-ai-studio" or "yandex" or "aliceai" or "alice-ai" => new YandexAiStudioProvider(
                httpClient,
                UriFor(profile.BaseUrl, "https://ai.api.cloud.yandex.net/v1"),
                credential.Value,
                GetParameter(profile, "folder_id")),
            "anthropic" => new AnthropicProvider(
                httpClient,
                UriFor(profile.BaseUrl, "https://api.anthropic.com"),
                credential.Value,
                ReadIntParameter(profile, "max_tokens", 4096)),
            "gemini" or "google-gemini" => new GeminiProvider(
                httpClient,
                UriFor(profile.BaseUrl, "https://generativelanguage.googleapis.com/v1beta"),
                credential.Value),
            "gigachat" or "sber-gigachat" or "sbergigachat" => new GigaChatProvider(
                httpClient,
                UriFor(profile.BaseUrl, "https://api.giga.chat"),
                credential.Value,
                GetParameter(profile, "scope") ?? "GIGACHAT_API_PERS",
                timeProvider),
            "openai" or "openai-compatible" => new OpenAiCompatibleProvider(
                httpClient,
                UriFor(profile.BaseUrl, "https://api.openai.com/v1"),
                credential.Value,
                GetParameter(profile, "auth_scheme") ?? "Bearer",
                GetParameter(profile, "auth_header") ?? "Authorization"),
            _ => throw new ProviderCapabilityException(
                $"Provider '{providerId}' is not supported by the configured adapter.")
        };

        if (string.Equals(GetParameter(profile, "supports_tools"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolCapabilityProvider(provider, modelId);
        }

        return provider;
    }

    private static Uri UriFor(string value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ProviderRequestException("The provider base URL must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    private static string? GetParameter(ProviderProfile profile, string name)
    {
        if (profile.Parameters is null)
        {
            return null;
        }

        var pair = profile.Parameters.FirstOrDefault(
            item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(pair.Value) ? null : pair.Value.Trim();
    }

    private static int ReadIntParameter(ProviderProfile profile, string name, int defaultValue)
    {
        var value = GetParameter(profile, name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private sealed class ToolCapabilityProvider : IChatProvider
    {
        private readonly IChatProvider _inner;
        private readonly string _modelId;

        public ToolCapabilityProvider(IChatProvider inner, string modelId)
        {
            _inner = inner;
            _modelId = modelId;
        }

        public async IAsyncEnumerable<ProviderEvent> StreamAsync(
            ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.Tools.Count > 0)
            {
                throw new ProviderCapabilityException(
                    $"Model '{_modelId}' is configured without tool-calling capability; no Revit tools were sent.");
            }

            await foreach (var item in _inner.StreamAsync(request, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }
}
