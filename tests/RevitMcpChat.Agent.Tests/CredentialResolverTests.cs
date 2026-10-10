using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Settings;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class CredentialResolverTests
{
    [Fact]
    public async Task UserKeyOverridesAdmin()
    {
        var secrets = new MemorySecretStore();
        await secrets.SaveAsync("openai", "user-key", CancellationToken.None);
        var resolver = new CredentialResolver(secrets, new TestAdminKeySource("openai", "admin-key"));

        var credential = await resolver.ResolveAsync("openai", CancellationToken.None);

        Assert.Equal(CredentialSource.User, credential.Source);
        Assert.Equal("user-key", credential.Value);
    }

    [Fact]
    public async Task DeletingUserKeyRestoresAdmin()
    {
        var secrets = new MemorySecretStore();
        await secrets.SaveAsync("openai", "user-key", CancellationToken.None);
        var admin = new TestAdminKeySource("openai", "admin-key");
        var resolver = new CredentialResolver(secrets, admin);

        await secrets.DeleteAsync("openai", CancellationToken.None);
        var credential = await resolver.ResolveAsync("openai", CancellationToken.None);

        Assert.Equal(CredentialSource.Admin, credential.Source);
        Assert.Equal("admin-key", credential.Value);
    }

    [Fact]
    public async Task InvalidUserKeyDoesNotFallbackAfter401()
    {
        var secrets = new MemorySecretStore();
        await secrets.SaveAsync("openai", "rejected-user-key", CancellationToken.None);
        var admin = new TestAdminKeySource("openai", "admin-key");
        var resolver = new CredentialResolver(secrets, admin);

        var credential = await resolver.ResolveAsync("openai", CancellationToken.None);

        Assert.Equal(CredentialSource.User, credential.Source);
        Assert.Equal("rejected-user-key", credential.Value);
        Assert.Equal(0, admin.ReadCount);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task SaveAsync(string providerId, string value, CancellationToken cancellationToken)
        {
            _values[providerId] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadAsync(string providerId, CancellationToken cancellationToken)
        {
            _values.TryGetValue(providerId, out var value);
            return Task.FromResult(value);
        }

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken)
        {
            _values.Remove(providerId);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAdminKeySource : IAdminKeySource
    {
        private readonly string _providerId;
        private readonly string? _key;

        public TestAdminKeySource(string providerId, string? key)
        {
            _providerId = providerId;
            _key = key;
        }

        public int ReadCount { get; private set; }

        public string? GetKey(string providerId)
        {
            ReadCount++;
            return string.Equals(providerId, _providerId, StringComparison.OrdinalIgnoreCase) ? _key : null;
        }
    }
}
