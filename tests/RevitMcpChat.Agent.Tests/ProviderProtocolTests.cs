using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.Providers;
using RevitMcpChat.Agent.Settings;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class ProviderProtocolTests
{
    [Fact]
    public async Task DeepSeekUsesConfiguredBaseUrl()
    {
        var handler = new FakeHttpHandler(_ => Sse(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"ds-call\",\"function\":{\"name\":\"revit_get_levels\",\"arguments\":\"{\\\"includeLinked\\\":false}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n"));
        using var client = new HttpClient(handler);
        var provider = ProviderFactory.Create(
            Profile("deepseek", "https://api.deepseek.com", "deepseek-flash"),
            new ResolvedCredential(CredentialSource.User, "deepseek-key"),
            client);

        var events = await CollectAsync(provider, Request());

        Assert.Equal(new Uri("https://api.deepseek.com/chat/completions"), handler.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "deepseek-key"), handler.Authorization);
        var call = Assert.IsType<ProviderToolCallEvent>(Assert.Single(events));
        Assert.Equal("revit_get_levels", call.Name);
        Assert.Equal("{\"includeLinked\":false}", call.ArgumentsJson);
    }

    [Fact]
    public async Task YandexUsesApiKeyHeaderAndFolderModel()
    {
        var handler = new FakeHttpHandler(_ => Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Привет\"}}]}\n\n" +
            "data: [DONE]\n\n"));
        using var client = new HttpClient(handler);
        var profile = Profile("yandex-ai-studio", "https://ai.api.cloud.yandex.net/v1", "yandexgpt");
        profile.Parameters["folder_id"] = "folder-123";
        var provider = ProviderFactory.Create(
            profile,
            new ResolvedCredential(CredentialSource.User, "yc-secret"),
            client);

        var events = await CollectAsync(provider, new ProviderRequest(
            "yandexgpt",
            new[] { ProviderMessage.User("Hello") },
            Array.Empty<McpToolDescriptor>()));

        Assert.Equal(new Uri("https://ai.api.cloud.yandex.net/v1/chat/completions"), handler.RequestUri);
        Assert.Equal("Api-Key yc-secret", handler.Authorization?.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("gpt://folder-123/yandexgpt/latest", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Привет", Assert.IsType<ProviderTextDeltaEvent>(Assert.Single(events)).Text);
    }

    [Fact]
    public async Task AnthropicToolUseRoundTrips()
    {
        const string response =
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"anthropic-call\",\"name\":\"revit_get_levels\",\"input\":{}}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"includeLinked\\\":false}\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var handler = new FakeHttpHandler(_ => Sse(response));
        using var client = new HttpClient(handler);
        var provider = ProviderFactory.Create(
            Profile("anthropic", "https://api.anthropic.com", "claude-test"),
            new ResolvedCredential(CredentialSource.User, "anthropic-key"),
            client);
        var request = new ProviderRequest(
            "claude-test",
            new ProviderMessage[]
            {
                ProviderMessage.System("Follow the tool policy."),
                ProviderMessage.User("List levels."),
                ProviderMessage.Assistant("", new[] { new ProviderToolCall("previous-call", "revit_get_levels", "{}") }),
                ProviderMessage.ToolResult("previous-call", "revit_get_levels", "{\"levels\":[]}"),
            },
            new[] { Tool("revit_get_levels") });

        var events = await CollectAsync(provider, request);

        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), handler.RequestUri);
        Assert.Equal("anthropic-key", handler.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", handler.Headers["anthropic-version"]);
        using var body = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("Follow the tool policy.", body.RootElement.GetProperty("system").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("revit_get_levels", body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Contains(body.RootElement.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("role").GetString() == "user"
                && message.GetProperty("content").ToString().Contains("tool_result", StringComparison.Ordinal));
        var call = Assert.IsType<ProviderToolCallEvent>(Assert.Single(events));
        Assert.Equal("anthropic-call", call.CallId);
        Assert.Equal("{\"includeLinked\":false}", call.ArgumentsJson);
    }

    [Fact]
    public async Task GeminiFunctionCallRoundTrips()
    {
        const string response =
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"id\":\"gemini-call\",\"name\":\"revit_get_levels\",\"args\":{\"includeLinked\":false}}}]}}]}\n\n";
        var handler = new FakeHttpHandler(_ => Sse(response));
        using var client = new HttpClient(handler);
        var provider = ProviderFactory.Create(
            Profile("gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-test"),
            new ResolvedCredential(CredentialSource.User, "gemini-key"),
            client);
        var request = new ProviderRequest(
            "gemini-test",
            new ProviderMessage[]
            {
                ProviderMessage.System("Follow the tool policy."),
                ProviderMessage.User("List levels."),
                ProviderMessage.Assistant("", new[] { new ProviderToolCall("previous-call", "revit_get_levels", "{}") }),
                ProviderMessage.ToolResult("previous-call", "revit_get_levels", "{\"levels\":[]}"),
            },
            new[] { Tool("revit_get_levels") });

        var events = await CollectAsync(provider, request);

        Assert.Contains(":streamGenerateContent", handler.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("gemini-key", handler.Headers["x-goog-api-key"]);
        Assert.Equal("alt=sse", handler.RequestUri.Query.TrimStart('?'));
        using var body = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("Follow the tool policy.", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("revit_get_levels", body.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.Contains(body.RootElement.GetProperty("contents").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "model"
                && item.ToString().Contains("functionCall", StringComparison.Ordinal));
        Assert.Contains(body.RootElement.GetProperty("contents").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "user"
                && item.ToString().Contains("functionResponse", StringComparison.Ordinal));
        var call = Assert.IsType<ProviderToolCallEvent>(Assert.Single(events));
        Assert.Equal("gemini-call", call.CallId);
        Assert.Equal("{\"includeLinked\":false}", call.ArgumentsJson);
    }

    [Fact]
    public async Task GigaChatRefreshesRejectedAccessTokenOnce()
    {
        var tokenRequests = 0;
        var completionRequests = 0;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth", StringComparison.Ordinal))
            {
                tokenRequests++;
                var tokenBody = JsonSerializer.Serialize(new
                {
                    access_token = "giga-access-" + tokenRequests,
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tokenBody, Encoding.UTF8, "application/json")
                };
            }

            completionRequests++;
            if (completionRequests == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("expired bearer token")
                };
            }

            return Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ready\"}}]}\n\ndata: [DONE]\n\n");
        });
        using var client = new HttpClient(handler);
        var provider = ProviderFactory.Create(
            Profile("sbergigachat", "https://api.giga.chat", "GigaChat"),
            new ResolvedCredential(CredentialSource.User, "giga-authorization-key"),
            client);

        var events = await CollectAsync(provider, Request());

        Assert.Equal(2, tokenRequests);
        Assert.Equal(2, completionRequests);
        Assert.Equal("Bearer giga-access-2", handler.Authorization?.ToString());
        Assert.Equal("ready", Assert.IsType<ProviderTextDeltaEvent>(Assert.Single(events)).Text);
    }

    [Fact]
    public async Task UnsupportedToolModelReportsCapability()
    {
        var handler = new FakeHttpHandler(_ => Sse("data: [DONE]\n\n"));
        using var client = new HttpClient(handler);
        var profile = Profile("openai-compatible", "https://api.example/v1", "text-only-model");
        profile.Parameters["supports_tools"] = "false";
        var provider = ProviderFactory.Create(
            profile,
            new ResolvedCredential(CredentialSource.User, "some-key"),
            client);

        var exception = await Assert.ThrowsAsync<ProviderCapabilityException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new ProviderRequest(
                "text-only-model",
                new[] { ProviderMessage.User("Hello") },
                new[] { Tool("revit_get_levels") }), CancellationToken.None))
            {
            }
        });

        Assert.Contains("tool", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task ProviderErrorDoesNotEchoSecret()
    {
        const string secret = "secret-never-echo";
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Provider rejected " + secret)
        });
        using var client = new HttpClient(handler);
        var provider = ProviderFactory.Create(
            Profile("openai-compatible", "https://api.example/v1", "test-model"),
            new ResolvedCredential(CredentialSource.User, secret),
            client);

        var exception = await Assert.ThrowsAsync<ProviderRequestException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new ProviderRequest(
                "text-only-model",
                new[] { ProviderMessage.User("Hello") },
                new[] { Tool("revit_get_levels") }), CancellationToken.None))
            {
            }
        });

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    private static ProviderProfile Profile(string id, string baseUrl, string model) => new()
    {
        ProviderId = id,
        BaseUrl = baseUrl,
        ModelId = model
    };

    private static ProviderRequest Request() =>
        new("test-model", new[] { ProviderMessage.User("Hello") }, Array.Empty<McpToolDescriptor>());

    private static McpToolDescriptor Tool(string name) =>
        new(name, "Lists Revit levels.", "{\"type\":\"object\",\"properties\":{\"includeLinked\":{\"type\":\"boolean\"}}}", true);

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private static async Task<List<ProviderEvent>> CollectAsync(IChatProvider provider, ProviderRequest request)
    {
        var events = new List<ProviderEvent>();
        await foreach (var item in provider.StreamAsync(request, CancellationToken.None))
        {
            events.Add(item);
        }

        return events;
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
            _responseFactory = responseFactory;

        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }

            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory(request);
        }
    }
}
