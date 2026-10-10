using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Providers;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class GigaChatTokenTests
{
    [Fact]
    public async Task GigaChatTokenRefreshesAfterExpiry()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero));
        var handler = new TokenHandler(clock);
        using var httpClient = new HttpClient(handler);
        var client = new GigaChatTokenClient(
            httpClient,
            new Uri("https://api.giga.chat/api/v2/oauth"),
            "encoded-auth-key",
            "GIGACHAT_API_PERS",
            clock);

        Assert.Equal("access-token-1", await client.GetAccessTokenAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("access-token-2", await client.GetAccessTokenAsync(CancellationToken.None));

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("Basic encoded-auth-key", handler.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Equal("scope=GIGACHAT_API_PERS", handler.RequestBody);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public ManualTimeProvider(DateTimeOffset now) => Now = now;
        public DateTimeOffset Now { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan amount) => Now += amount;
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        private readonly ManualTimeProvider _clock;

        public TokenHandler(ManualTimeProvider clock) => _clock = clock;
        public int RequestCount { get; private set; }
        public string? Authorization { get; private set; }
        public string? ContentType { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var expiry = _clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds();
            var content = JsonSerializer.Serialize(new
            {
                access_token = "access-token-" + RequestCount,
                expires_at = expiry
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
