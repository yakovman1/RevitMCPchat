using System.Threading;
using System.Threading.Tasks;

namespace RevitMcpChat.Agent.Settings;

public interface ISecretStore
{
    Task SaveAsync(string providerId, string value, CancellationToken cancellationToken);
    Task<string?> LoadAsync(string providerId, CancellationToken cancellationToken);
    Task DeleteAsync(string providerId, CancellationToken cancellationToken);
}
