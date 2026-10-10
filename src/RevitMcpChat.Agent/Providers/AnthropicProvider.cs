using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Mcp;

namespace RevitMcpChat.Agent.Providers;

public sealed class AnthropicProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _messagesUri;
    private readonly string _apiKey;
    private readonly int _maxTokens;

    public AnthropicProvider(HttpClient httpClient, Uri baseUri, string apiKey, int maxTokens = 4096)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _messagesUri = BuildMessagesUri(baseUri);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        _apiKey = apiKey;
        _maxTokens = maxTokens > 0 ? maxTokens : 4096;
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _messagesUri);
        message.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
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
            throw new ProviderRequestException("The Anthropic request could not be completed.");
        }

        using (response)
        {
            ThrowForStatus(response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var toolCalls = new Dictionary<int, ToolCallBuilder>();

            await foreach (var data in HttpStreamingParser.ReadDataAsync(stream, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeValue)
                    || typeValue.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (typeValue.GetString())
                {
                    case "content_block_start":
                    {
                        if (!root.TryGetProperty("index", out var indexValue)
                            || !indexValue.TryGetInt32(out var index)
                            || !root.TryGetProperty("content_block", out var block)
                            || !block.TryGetProperty("type", out var blockType)
                            || blockType.GetString() != "tool_use")
                        {
                            break;
                        }

                        var id = GetString(block, "id");
                        var name = GetString(block, "name");
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                        {
                            throw new ProviderRequestException("Anthropic returned an incomplete tool call.");
                        }

                        var builder = new ToolCallBuilder(id, name);
                        if (block.TryGetProperty("input", out var input)
                            && input.ValueKind == JsonValueKind.Object
                            && input.EnumerateObject().Any())
                        {
                            builder.Append(input.GetRawText());
                        }

                        toolCalls[index] = builder;
                        break;
                    }
                    case "content_block_delta":
                    {
                        if (!root.TryGetProperty("delta", out var delta)
                            || !delta.TryGetProperty("type", out var deltaType))
                        {
                            break;
                        }

                        if (deltaType.GetString() == "text_delta"
                            && delta.TryGetProperty("text", out var text)
                            && text.ValueKind == JsonValueKind.String
                            && !string.IsNullOrEmpty(text.GetString()))
                        {
                            yield return new ProviderTextDeltaEvent(text.GetString()!);
                        }
                        else if (deltaType.GetString() == "input_json_delta"
                            && root.TryGetProperty("index", out var indexValue)
                            && indexValue.TryGetInt32(out var index)
                            && delta.TryGetProperty("partial_json", out var partial)
                            && partial.ValueKind == JsonValueKind.String
                            && toolCalls.TryGetValue(index, out var builder))
                        {
                            builder.Append(partial.GetString()!);
                        }

                        break;
                    }
                    case "content_block_stop":
                    {
                        if (root.TryGetProperty("index", out var indexValue)
                            && indexValue.TryGetInt32(out var index)
                            && toolCalls.Remove(index, out var builder))
                        {
                            yield return builder.Build();
                        }

                        break;
                    }
                    case "error":
                        throw new ProviderRequestException("Anthropic returned an error in its event stream.");
                }
            }

            if (toolCalls.Count > 0)
            {
                throw new ProviderRequestException("Anthropic ended the stream before a tool call was complete.");
            }
        }
    }

    private byte[] BuildRequestBody(ProviderRequest request)
    {
        var systemText = string.Join(
            "\n",
            request.Messages.Where(item => item.Role == "system").Select(item => item.Content));
        var messages = new List<object>();
        foreach (var item in request.Messages)
        {
            if (item.Role == "system")
            {
                continue;
            }

            if (item.Role == "tool")
            {
                messages.Add(new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "tool_result",
                            tool_use_id = item.ToolCallId,
                            content = item.Content
                        }
                    }
                });
                continue;
            }

            if (item.Role == "assistant" && item.ToolCalls.Count > 0)
            {
                var content = new List<object>();
                if (!string.IsNullOrEmpty(item.Content))
                {
                    content.Add(new { type = "text", text = item.Content });
                }

                foreach (var call in item.ToolCalls)
                {
                    content.Add(new
                    {
                        type = "tool_use",
                        id = call.CallId,
                        name = call.Name,
                        input = ParseArguments(call.ArgumentsJson)
                    });
                }

                messages.Add(new { role = "assistant", content });
                continue;
            }

            messages.Add(new { role = item.Role == "assistant" ? "assistant" : "user", content = item.Content });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = _maxTokens,
            ["stream"] = true,
            ["messages"] = messages
        };
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            body["system"] = systemText;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                input_schema = ParseSchema(tool)
            }).ToArray();
        }

        return JsonSerializer.SerializeToUtf8Bytes(body);
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

    private static JsonElement ParseArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProviderRequestException("A stored tool call had invalid JSON arguments.");
        }
    }

    private static Uri BuildMessagesUri(Uri baseUri)
    {
        ValidateBaseUri(baseUri);
        var text = baseUri.AbsoluteUri.TrimEnd('/');
        if (text.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(text);
        }

        return new Uri(
            text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? text + "/messages" : text + "/v1/messages");
    }

    private static void ValidateBaseUri(Uri baseUri)
    {
        if (baseUri is null
            || !baseUri.IsAbsoluteUri
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The provider base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUri));
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
            throw new ProviderAuthenticationException($"Anthropic authentication failed (HTTP {(int)statusCode}).");
        }

        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            throw new ProviderRequestException($"Anthropic request failed (HTTP {(int)statusCode}).");
        }
    }

    private sealed class ToolCallBuilder
    {
        private readonly StringBuilder _arguments = new();

        public ToolCallBuilder(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }

        public void Append(string json) => _arguments.Append(json);

        public ProviderToolCallEvent Build()
        {
            var arguments = _arguments.Length == 0 ? "{}" : _arguments.ToString();
            try
            {
                using var _ = JsonDocument.Parse(arguments);
            }
            catch (JsonException)
            {
                throw new ProviderRequestException("Anthropic returned invalid JSON tool arguments.");
            }

            return new ProviderToolCallEvent(Id, Name, arguments);
        }
    }
}
