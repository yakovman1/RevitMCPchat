using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Mcp;

namespace RevitMcpChat.Agent.Providers;

public sealed class GigaChatProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _completionUri;
    private readonly GigaChatTokenClient _tokenClient;

    public GigaChatProvider(
        HttpClient httpClient,
        Uri baseUri,
        string authorizationKey,
        string scope = "GIGACHAT_API_PERS",
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (baseUri is null || !baseUri.IsAbsoluteUri
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The provider base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUri));
        }

        _completionUri = BuildCompletionUri(baseUri);
        _tokenClient = new GigaChatTokenClient(
            httpClient,
            new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/api/v2/oauth"),
            authorizationKey,
            scope,
            timeProvider);
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildRequestBody(request);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var accessToken = await _tokenClient
                .GetAccessTokenAsync(cancellationToken, forceRefresh: attempt > 0)
                .ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, _completionUri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            message.Content = new ByteArrayContent(body);
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
                throw new ProviderRequestException("The GigaChat completion request could not be completed.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                if (attempt == 0)
                {
                    continue;
                }

                throw new ProviderAuthenticationException(
                    $"GigaChat authentication failed (HTTP {(int)statusCode}).");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new ProviderRequestException(
                        $"GigaChat request failed (HTTP {(int)response.StatusCode}).");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
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
                        throw new ProviderRequestException("GigaChat returned an error in its event stream.");
                    }

                    if (!root.TryGetProperty("choices", out var choices)
                        || choices.ValueKind != JsonValueKind.Array
                        || choices.GetArrayLength() == 0
                        || !choices[0].TryGetProperty("delta", out var delta))
                    {
                        continue;
                    }

                    if (delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(content.GetString()))
                    {
                        yield return new ProviderTextDeltaEvent(content.GetString()!);
                    }

                    if (delta.TryGetProperty("function_call", out var functionCall))
                    {
                        var name = GetString(functionCall, "name");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            throw new ProviderRequestException("GigaChat returned a function call without a name.");
                        }

                        var arguments = GetArguments(functionCall);
                        var callId = GetString(delta, "functions_state_id")
                            ?? "giga-" + Guid.NewGuid().ToString("N");
                        yield return new ProviderToolCallEvent(callId, name, arguments);
                    }
                }
            }

            yield break;
        }

        throw new ProviderAuthenticationException("GigaChat authentication failed after one token refresh.");
    }

    private static byte[] BuildRequestBody(ProviderRequest request)
    {
        var messages = new List<object>();
        foreach (var item in request.Messages)
        {
            if (item.Role == "tool")
            {
                messages.Add(new
                {
                    role = "function",
                    content = FunctionResultContent(item.Content),
                    name = item.ToolName
                });
                continue;
            }

            if (item.Role == "assistant" && item.ToolCalls.Count > 0)
            {
                if (item.ToolCalls.Count > 1)
                {
                    throw new ProviderCapabilityException(
                        "This GigaChat protocol profile supports one function call per assistant message.");
                }

                var call = item.ToolCalls[0];
                messages.Add(new
                {
                    role = "assistant",
                    content = item.Content,
                    function_call = new
                    {
                        name = call.Name,
                        arguments = ParseArguments(call.ArgumentsJson)
                    }
                });
                continue;
            }

            messages.Add(new { role = item.Role, content = item.Content });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = true
        };
        if (request.Tools.Count > 0)
        {
            body["functions"] = request.Tools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = ParseSchema(tool)
            }).ToArray();
            body["function_call"] = "auto";
        }

        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static Uri BuildCompletionUri(Uri baseUri)
    {
        var value = baseUri.AbsoluteUri.TrimEnd('/');
        if (value.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(value);
        }

        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(value + "/chat/completions");
        }

        return new Uri(value + "/v1/chat/completions");
    }

    private static string FunctionResultContent(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return content;
            }
        }
        catch (JsonException)
        {
            // GigaChat function results must be JSON text, so wrap plain MCP output below.
        }

        return JsonSerializer.Serialize(new Dictionary<string, string> { ["result"] = content });
    }

    private static JsonElement ParseArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderRequestException("GigaChat function arguments must be a JSON object.");
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

    private static string GetArguments(JsonElement functionCall)
    {
        if (!functionCall.TryGetProperty("arguments", out var arguments))
        {
            return "{}";
        }

        var value = arguments.ValueKind == JsonValueKind.String
            ? arguments.GetString() ?? "{}"
            : arguments.GetRawText();
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderRequestException("GigaChat returned non-object function arguments.");
            }

            return value;
        }
        catch (JsonException)
        {
            throw new ProviderRequestException("GigaChat returned invalid JSON function arguments.");
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
