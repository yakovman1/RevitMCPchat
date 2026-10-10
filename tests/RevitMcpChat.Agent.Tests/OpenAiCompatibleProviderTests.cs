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
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.Providers;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task StreamsTextAndToolCallFromSse()
    {
        const string apiKey = "test-secret";
        const string responseBody =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello \"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-7\",\"type\":\"function\",\"function\":{\"name\":\"revit_get_levels\",\"arguments\":\"{\\\"includeLinked\\\":false}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(httpClient, new Uri("https://api.example/v1"), apiKey);
        var request = new ProviderRequest(
            "test-model",
            new[] { ProviderMessage.User("List levels.") },
            new[] { new McpToolDescriptor("revit_get_levels", "List model levels", "{\"type\":\"object\"}", true) });

        var events = new List<ProviderEvent>();
        await foreach (var item in provider.StreamAsync(request, CancellationToken.None))
        {
            events.Add(item);
        }

        Assert.Equal(2, events.Count);
        var text = Assert.IsType<ProviderTextDeltaEvent>(events[0]);
        Assert.Equal("Hello ", text.Text);
        var toolCall = Assert.IsType<ProviderToolCallEvent>(events[1]);
        Assert.Equal("call-7", toolCall.CallId);
        Assert.Equal("revit_get_levels", toolCall.Name);
        Assert.Equal("{\"includeLinked\":false}", toolCall.ArgumentsJson);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri("https://api.example/v1/chat/completions"), handler.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", apiKey), handler.Authorization);

        using var requestJson = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("test-model", requestJson.RootElement.GetProperty("model").GetString());
        Assert.True(requestJson.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("revit_get_levels",
            requestJson.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Http401ReportsProviderAuthErrorWithoutEchoingKey()
    {
        const string apiKey = "secret-that-must-not-appear";
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Authorization failed for " + apiKey)
        });
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(httpClient, new Uri("https://api.example/v1"), apiKey);

        var exception = await Assert.ThrowsAsync<ProviderAuthenticationException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(Request(), CancellationToken.None))
            {
            }
        });

        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
    }

    private static ProviderRequest Request() =>
        new("test-model", new[] { ProviderMessage.User("Hello") }, Array.Empty<McpToolDescriptor>());

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
            _responseFactory = responseFactory;

        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory(request);
        }
    }
}
