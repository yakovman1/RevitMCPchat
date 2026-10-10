namespace RevitMcpChat.Agent.Settings;

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
