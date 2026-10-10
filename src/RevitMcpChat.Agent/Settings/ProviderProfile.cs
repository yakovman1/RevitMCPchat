namespace RevitMcpChat.Agent.Settings;

/// <summary>
/// Non-secret provider configuration. API keys are stored through ISecretStore only.
/// </summary>
public sealed class ProviderProfile
{
    public string ProviderId { get; set; } = "openai-compatible";
    public string DisplayName { get; set; } = "OpenAI-compatible";
    public string BaseUrl { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
