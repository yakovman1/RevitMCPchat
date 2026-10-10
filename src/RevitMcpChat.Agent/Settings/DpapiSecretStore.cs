using System.Security.Cryptography;
using System.Text;

namespace RevitMcpChat.Agent.Settings;

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;

    public DpapiSecretStore(string? localApplicationDataPath = null)
    {
        var root = localApplicationDataPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new SecretStoreException("The Windows local application data directory is unavailable.");
        }

        _directory = Path.Combine(root, "RevitMcpChat", "secrets");
    }

    public async Task SaveAsync(
        string providerId,
        string value,
        CancellationToken cancellationToken)
    {
        ValidateProviderId(providerId);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty key is required.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        var path = GetPath(providerId);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var plainBytes = Encoding.UTF8.GetBytes(value);
        byte[]? protectedBytes = null;

        try
        {
            protectedBytes = ProtectedData.Protect(
                plainBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException
            or IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            TryDelete(temporaryPath);
            throw new SecretStoreException(
                $"The key for provider '{providerId}' could not be protected for this Windows account.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public async Task<string?> LoadAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        ValidateProviderId(providerId);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        var path = GetPath(providerId);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[]? protectedBytes = null;
        byte[]? plainBytes = null;
        try
        {
            protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(plainBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException
            or IOException
            or UnauthorizedAccessException
            or DecoderFallbackException
            or PlatformNotSupportedException)
        {
            throw new SecretStoreException(
                $"The saved key for provider '{providerId}' cannot be read with this Windows account; re-enter it in Settings.",
                exception);
        }
        finally
        {
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (plainBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
    }

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken)
    {
        ValidateProviderId(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        var path = GetPath(providerId);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException(
                $"The saved key for provider '{providerId}' could not be removed.",
                exception);
        }
    }

    private string GetPath(string providerId) =>
        Path.Combine(_directory, providerId.ToLowerInvariant() + ".dpapi");

    private static void ValidateProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > 64
            || providerId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider id contains unsupported characters.", nameof(providerId));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup of an incomplete ciphertext file.
        }
    }
}


