namespace RevitMcpChat.Agent.Settings;

public interface IAdminKeySource
{
    string? GetKey(string providerId);
}

public sealed class EmptyAdminKeySource : IAdminKeySource
{
    public string? GetKey(string providerId) => null;
}

public enum CredentialSource
{
    Missing,
    User,
    Admin
}

public sealed class ResolvedCredential
{
    public ResolvedCredential(CredentialSource source, string? value)
    {
        Source = source;
        Value = value;
    }

    public CredentialSource Source { get; }
    public string? Value { get; }

    public override string ToString() => $"ResolvedCredential(Source={Source})";
}
