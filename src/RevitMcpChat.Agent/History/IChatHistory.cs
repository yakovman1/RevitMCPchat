using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.History;

public interface IChatHistory
{
    Task<Guid> CreateAsync(string modelIdentity, string providerId, string modelId, CancellationToken cancellationToken);
    Task AppendAsync(Guid chatId, ChatEvent chatEvent, CancellationToken cancellationToken);
    Task ReplaceEventTextAsync(Guid chatId, string kind, string callId, string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatSummary>> ListAsync(string? modelIdentity, CancellationToken cancellationToken);
    Task<ChatTranscript> LoadAsync(Guid chatId, CancellationToken cancellationToken);
    Task RenameAsync(Guid chatId, string title, CancellationToken cancellationToken);
    Task DeleteAsync(Guid chatId, CancellationToken cancellationToken);
    Task BindUnsavedAsync(Guid chatId, string savedModelIdentity, CancellationToken cancellationToken);
}
