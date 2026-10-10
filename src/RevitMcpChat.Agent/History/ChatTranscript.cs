using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.History;

public sealed class ChatTranscript
{
    public Guid ChatId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ModelIdentity { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public IReadOnlyList<ChatEvent> Events { get; init; } = Array.Empty<ChatEvent>();
}
