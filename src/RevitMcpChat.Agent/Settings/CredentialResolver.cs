namespace RevitMcpChat.Agent.Settings;

public sealed class CredentialResolver
{
    private readonly ISecretStore _secretStore;
    private readonly IAdminKeySource _adminKeySource;

    public CredentialResolver(ISecretStore secretStore, IAdminKeySource adminKeySource)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _adminKeySource = adminKeySource ?? throw new ArgumentNullException(nameof(adminKeySource));
    }

    public async Task<ResolvedCredential> ResolveAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var userKey = await _secretStore.LoadAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(userKey))
        {
            return new ResolvedCredential(CredentialSource.User, userKey);
        }

        var adminKey = _adminKeySource.GetKey(providerId);
        if (!string.IsNullOrWhiteSpace(adminKey))
        {
            return new ResolvedCredential(CredentialSource.Admin, adminKey);
        }

        return new ResolvedCredential(CredentialSource.Missing, null);
    }
}
