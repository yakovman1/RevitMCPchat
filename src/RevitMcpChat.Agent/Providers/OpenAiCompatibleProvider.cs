using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Chat;

namespace RevitMcpChat.Agent.Providers;

public sealed class OpenAiCompatibleProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _completionUri;
    private readonly string _apiKey;
    private readonly string _authorizationScheme;
    private readonly string _authorizationHeaderName;

    public OpenAiCompatibleProvider(
        HttpClient httpClient,
        Uri baseUri,
        string apiKey,
        string authorizationScheme = "Bearer",
        string authorizationHeaderName = "Authorization")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (baseUri is null || !baseUri.IsAbsoluteUri ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The provider base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUri));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        var normalizedBase = baseUri.AbsoluteUri.TrimEnd('/');
        var endpoint = normalizedBase.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? normalizedBase
            : normalizedBase + "/chat/completions";

        _completionUri = new Uri(endpoint, UriKind.Absolute);
        if (string.IsNullOrWhiteSpace(authorizationHeaderName))
        {
            throw new ArgumentException("An authorization header name is required.", nameof(authorizationHeaderName));
        }

        _apiKey = apiKey;
        _authorizationScheme = authorizationScheme ?? string.Empty;
        _authorizationHeaderName = authorizationHeaderName;
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ProviderRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _completionUri);
        if (string.Equals(_authorizationHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_authorizationScheme))
            {
                message.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            }
            else
            {
                message.Headers.Authorization = new AuthenticationHeaderValue(_authorizationScheme, _apiKey);
            }
        }
        else if (!message.Headers.TryAddWithoutValidation(_authorizationHeaderName, _apiKey))
        {
            throw new ProviderRequestException("The configured provider authentication header is invalid.");
        }
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content = new ByteArrayContent(BuildRequestBody(request));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new ProviderRequestException("The provider request could not be completed.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ProviderAuthenticationException(
                    $"Provider authentication failed (HTTP {(int)response.StatusCode}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderRequestException(
                    $"Provider request failed (HTTP {(int)response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var toolCalls = new Dictionary<int, ToolCallBuilder>();

            await foreach (var data in HttpStreamingParser.ReadDataAsync(stream, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out _))
                {
                    throw new ProviderRequestException("The provider returned an error in its event stream.");
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(content.GetString()))
                {
                    yield return new ProviderTextDeltaEvent(content.GetString()!);
                }

                if (delta.TryGetProperty("tool_calls", out var deltas) &&
                    deltas.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolDelta in deltas.EnumerateArray())
                    {
                        var index = toolDelta.TryGetProperty("index", out var indexValue) &&
                                    indexValue.TryGetInt32(out var parsedIndex)
                            ? parsedIndex
                            : 0;

                        if (!toolCalls.TryGetValue(index, out var builder))
                        {
                            builder = new ToolCallBuilder();
                            toolCalls.Add(index, builder);
                        }

                        builder.Append(toolDelta);
                    }
                }
            }

            foreach (var pair in toolCalls.OrderBy(item => item.Key))
            {
                yield return pair.Value.Build();
            }
        }
    }

    private static byte[] BuildRequestBody(ProviderRequest request)
    {
        var messages = request.Messages.Select(ToWireMessage).ToArray();
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = true
        };

        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(tool =>
            {
                using var schemaDocument = JsonDocument.Parse(tool.InputSchemaJson);
                return new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = schemaDocument.RootElement.Clone()
                    }
                };
            }).ToArray();
            body["tool_choice"] = "auto";
        }

        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static Dictionary<string, object?> ToWireMessage(ProviderMessage message)
    {
        var wireMessage = new Dictionary<string, object?>
        {
            ["role"] = message.Role
        };

        if (message.Role == "tool")
        {
            wireMessage["tool_call_id"] = message.ToolCallId;
            wireMessage["name"] = message.ToolName;
            wireMessage["content"] = message.Content;
            return wireMessage;
        }

        wireMessage["content"] = message.Content;
        if (message.ToolCalls.Count > 0)
        {
            wireMessage["tool_calls"] = message.ToolCalls.Select(call => new
            {
                id = call.CallId,
                type = "function",
                function = new { name = call.Name, arguments = call.ArgumentsJson }
            }).ToArray();
        }

        return wireMessage;
    }

    private sealed class ToolCallBuilder
    {
        private readonly StringBuilder _name = new();
        private readonly StringBuilder _arguments = new();
        private string? _callId;

        public void Append(JsonElement delta)
        {
            if (delta.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                _callId ??= id.GetString();
            }

            if (!delta.TryGetProperty("function", out var function))
            {
                return;
            }

            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                _name.Append(name.GetString());
            }

            if (function.TryGetProperty("arguments", out var arguments) &&
                arguments.ValueKind == JsonValueKind.String)
            {
                _arguments.Append(arguments.GetString());
            }
        }

        public ProviderToolCallEvent Build() =>
            new(
                _callId ?? Guid.NewGuid().ToString("N"),
                _name.ToString(),
                _arguments.Length == 0 ? "{}" : _arguments.ToString());
    }
}
