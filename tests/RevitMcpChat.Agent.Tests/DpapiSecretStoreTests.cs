using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Settings;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public async Task SavedKeyIsNotPlaintext()
    {
        var root = Path.Combine(Path.GetTempPath(), "RevitMcpChatSecrets-" + Guid.NewGuid().ToString("N"));
        var store = new DpapiSecretStore(root);
        const string secret = "user-secret-value";

        try
        {
            await store.SaveAsync("openai", secret, CancellationToken.None);
            var path = Path.Combine(root, "RevitMcpChat", "secrets", "openai.dpapi");
            var protectedBytes = await File.ReadAllBytesAsync(path);

            Assert.Equal(-1, protectedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)));
            Assert.Equal(secret, await store.LoadAsync("openai", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CorruptProtectedKeyShowsSettingsError()
    {
        var root = Path.Combine(Path.GetTempPath(), "RevitMcpChatSecrets-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "RevitMcpChat", "secrets", "openai.dpapi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("not a DPAPI blob"));
        var store = new DpapiSecretStore(root);

        try
        {
            var exception = await Assert.ThrowsAsync<SecretStoreException>(
                () => store.LoadAsync("openai", CancellationToken.None));
            Assert.Contains("re-enter", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

