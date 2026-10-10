namespace RevitMcpChat.Agent.History;

public sealed class ChatSummary
{
    public Guid ChatId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ModelIdentity { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
