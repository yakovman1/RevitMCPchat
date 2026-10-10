using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Mcp;

namespace RevitMcpChat.Agent.Providers;

public sealed class GeminiProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly string _apiKey;

    public GeminiProvider(HttpClient httpClient, Uri baseUri, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (baseUri is null
            || !baseUri.IsAbsoluteUri
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The provider base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUri));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        _baseUri = baseUri;
        _apiKey = apiKey;
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildCompletionUri(request.ModelId));
        message.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
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
            throw new ProviderRequestException("The Gemini request could not be completed.");
        }

        using (response)
        {
            ThrowForStatus(response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var toolCalls = new Dictionary<(int Candidate, int Part), ToolCallBuilder>();

            await foreach (var data in HttpStreamingParser.ReadDataAsync(stream, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(data);
                var roots = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().ToArray()
                    : new[] { document.RootElement };

                foreach (var root in roots)
                {
                    if (!root.TryGetProperty("candidates", out var candidates)
                        || candidates.ValueKind != JsonValueKind.Array)
                    {
                        if (root.TryGetProperty("error", out _))
                        {
                            throw new ProviderRequestException("Gemini returned an error in its event stream.");
                        }

                        continue;
                    }

                    var candidateIndex = 0;
                    foreach (var candidate in candidates.EnumerateArray())
                    {
                        if (!candidate.TryGetProperty("content", out var content)
                            || !content.TryGetProperty("parts", out var parts)
                            || parts.ValueKind != JsonValueKind.Array)
                        {
                            candidateIndex++;
                            continue;
                        }

                        var partIndex = 0;
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text)
                                && text.ValueKind == JsonValueKind.String
                                && !string.IsNullOrEmpty(text.GetString()))
                            {
                                yield return new ProviderTextDeltaEvent(text.GetString()!);
                            }

                            if (part.TryGetProperty("functionCall", out var functionCall))
                            {
                                var name = GetString(functionCall, "name");
                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    throw new ProviderRequestException("Gemini returned a function call without a name.");
                                }

                                var key = (candidateIndex, partIndex);
                                if (!toolCalls.TryGetValue(key, out var builder))
                                {
                                    var callId = GetString(functionCall, "id")
                                        ?? $"gemini-{candidateIndex}-{partIndex}";
                                    builder = new ToolCallBuilder(callId, name);
                                    toolCalls.Add(key, builder);
                                }

                                if (functionCall.TryGetProperty("args", out var args))
                                {
                                    builder.Merge(args);
                                }
                            }

                            partIndex++;
                        }

                        candidateIndex++;
                    }
                }
            }

            foreach (var call in toolCalls.OrderBy(pair => pair.Key.Candidate).ThenBy(pair => pair.Key.Part))
            {
                yield return call.Value.Build();
            }
        }
    }

    private byte[] BuildRequestBody(ProviderRequest request)
    {
        var systemText = string.Join(
            "\n",
            request.Messages.Where(item => item.Role == "system").Select(item => item.Content));
        var contents = new List<object>();

        foreach (var item in request.Messages)
        {
            if (item.Role == "system")
            {
                continue;
            }

            if (item.Role == "tool")
            {
                contents.Add(new
                {
                    role = "user",
                    parts = new[]
                    {
                        new
                        {
                            functionResponse = new
                            {
                                id = item.ToolCallId,
                                name = item.ToolName,
                                response = ToolResponse(item.Content)
                            }
                        }
                    }
                });
                continue;
            }

            var parts = new List<object>();
            if (!string.IsNullOrEmpty(item.Content))
            {
                parts.Add(new { text = item.Content });
            }

            if (item.Role == "assistant")
            {
                foreach (var call in item.ToolCalls)
                {
                    parts.Add(new
                    {
                        functionCall = new
                        {
                            id = call.CallId,
                            name = call.Name,
                            args = ParseArguments(call.ArgumentsJson)
                        }
                    });
                }
            }

            contents.Add(new
            {
                role = item.Role == "assistant" ? "model" : "user",
                parts
            });
        }

        var body = new Dictionary<string, object?>
        {
            ["contents"] = contents
        };
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            body["systemInstruction"] = new { parts = new[] { new { text = systemText } } };
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = new[]
            {
                new
                {
                    functionDeclarations = request.Tools.Select(tool => new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = ParseSchema(tool)
                    }).ToArray()
                }
            };
        }

        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private Uri BuildCompletionUri(string modelId)
    {
        var normalizedModel = modelId.StartsWith("models/", StringComparison.Ordinal)
            ? modelId.Substring("models/".Length)
            : modelId;
        var baseText = _baseUri.AbsoluteUri.TrimEnd('/');
        if (!baseText.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
            && !baseText.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseText += "/v1beta";
        }

        return new Uri(
            $"{baseText}/models/{Uri.EscapeDataString(normalizedModel)}:streamGenerateContent?alt=sse",
            UriKind.Absolute);
    }

    private static object ToolResponse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // Plain MCP text is returned as a structured result field.
        }

        return new Dictionary<string, string> { ["result"] = content };
    }

    private static JsonElement ParseArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderRequestException("Gemini tool arguments must be a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProviderRequestException("A stored tool call had invalid JSON arguments.");
        }
    }

    private static object ParseSchema(McpToolDescriptor tool)
    {
        try
        {
            using var document = JsonDocument.Parse(tool.InputSchemaJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProviderCapabilityException($"Tool '{tool.Name}' has an invalid JSON schema.");
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ThrowForStatus(HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderAuthenticationException($"Gemini authentication failed (HTTP {(int)statusCode}).");
        }

        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            throw new ProviderRequestException($"Gemini request failed (HTTP {(int)statusCode}).");
        }
    }

    private sealed class ToolCallBuilder
    {
        private readonly Dictionary<string, JsonElement> _arguments = new(StringComparer.Ordinal);

        public ToolCallBuilder(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }

        public void Merge(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderRequestException("Gemini returned non-object function arguments.");
            }

            foreach (var property in arguments.EnumerateObject())
            {
                _arguments[property.Name] = property.Value.Clone();
            }
        }

        public ProviderToolCallEvent Build() =>
            new(Id, Name, JsonSerializer.Serialize(_arguments));
    }
}
