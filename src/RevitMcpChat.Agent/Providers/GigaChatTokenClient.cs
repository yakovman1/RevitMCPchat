using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RevitMcpChat.Agent.Providers;

public sealed class GigaChatTokenClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _tokenUri;
    private readonly string _authorizationKey;
    private readonly string _scope;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public GigaChatTokenClient(
        HttpClient httpClient,
        Uri tokenUri,
        string authorizationKey,
        string scope = "GIGACHAT_API_PERS",
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (tokenUri is null || !tokenUri.IsAbsoluteUri
            || (tokenUri.Scheme != Uri.UriSchemeHttps && tokenUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The token URL must be an absolute HTTP or HTTPS URL.", nameof(tokenUri));
        }

        if (string.IsNullOrWhiteSpace(authorizationKey))
        {
            throw new ArgumentException("A GigaChat authorization key is required.", nameof(authorizationKey));
        }

        _tokenUri = tokenUri;
        _authorizationKey = authorizationKey;
        _scope = string.IsNullOrWhiteSpace(scope) ? "GIGACHAT_API_PERS" : scope;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (!forceRefresh
                && !string.IsNullOrWhiteSpace(_accessToken)
                && now < _expiresAt.AddMinutes(-1))
            {
                return _accessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorizationKey);
            request.Headers.TryAddWithoutValidation("RqUID", Guid.NewGuid().ToString("D"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("scope", _scope)
            });

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                throw new ProviderRequestException("The GigaChat token request could not be completed.");
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ProviderAuthenticationException(
                        $"GigaChat token authentication failed (HTTP {(int)response.StatusCode}).");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ProviderRequestException(
                        $"GigaChat token request failed (HTTP {(int)response.StatusCode}).");
                }

                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (!root.TryGetProperty("access_token", out var tokenElement)
                    || tokenElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(tokenElement.GetString()))
                {
                    throw new ProviderRequestException("GigaChat returned an invalid access-token response.");
                }

                var expiresAt = root.TryGetProperty("expires_at", out var expiryElement)
                    ? ParseExpiry(expiryElement, now)
                    : now.AddMinutes(30);
                if (expiresAt <= now)
                {
                    throw new ProviderAuthenticationException("GigaChat returned an expired access token.");
                }

                _accessToken = tokenElement.GetString();
                _expiresAt = expiresAt;
                return _accessToken!;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTimeOffset ParseExpiry(JsonElement value, DateTimeOffset now)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var timestamp))
        {
            return timestamp > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return now.AddMinutes(30);
    }
}
