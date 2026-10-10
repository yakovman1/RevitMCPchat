namespace RevitMcpChat.Agent.Mcp;

public sealed class McpToolDescriptor
{
    public McpToolDescriptor(string name, string description, string inputSchemaJson, bool? readOnlyHint)
    {
        Name = name;
        Description = description;
        InputSchemaJson = inputSchemaJson;
        ReadOnlyHint = readOnlyHint;
    }

    public string Name { get; }
    public string Description { get; }
    public string InputSchemaJson { get; }
    public bool? ReadOnlyHint { get; }
}

public sealed class McpCallOutcome
{
    public McpCallOutcome(string content, bool isError, bool outcomeUnknown)
    {
        Content = content;
        IsError = isError;
        OutcomeUnknown = outcomeUnknown;
    }

    public string Content { get; }
    public bool IsError { get; }
    public bool OutcomeUnknown { get; }
}
