using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Mcp;

namespace RevitMcpChat.Agent.Chat;

public interface IChatProvider
{
    IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request, CancellationToken cancellationToken);
}

public sealed class ProviderRequest
{
    public ProviderRequest(string modelId, IReadOnlyList<ProviderMessage> messages, IReadOnlyList<McpToolDescriptor> tools)
    {
        ModelId = modelId;
        Messages = messages;
        Tools = tools;
    }

    public string ModelId { get; }
    public IReadOnlyList<ProviderMessage> Messages { get; }
    public IReadOnlyList<McpToolDescriptor> Tools { get; }
}

public sealed class ProviderMessage
{
    public ProviderMessage(
        string role,
        string content,
        string? toolCallId = null,
        string? toolName = null,
        IReadOnlyList<ProviderToolCall>? toolCalls = null)
    {
        Role = role;
        Content = content;
        ToolCallId = toolCallId;
        ToolName = toolName;
        ToolCalls = toolCalls ?? new List<ProviderToolCall>();
    }

    public string Role { get; }
    public string Content { get; }
    public string? ToolCallId { get; }
    public string? ToolName { get; }
    public IReadOnlyList<ProviderToolCall> ToolCalls { get; }

    public static ProviderMessage System(string text) => new("system", text);
    public static ProviderMessage User(string text) => new("user", text);
    public static ProviderMessage Assistant(string text, IReadOnlyList<ProviderToolCall> calls) =>
        new("assistant", text, toolCalls: calls);
    public static ProviderMessage ToolResult(string callId, string toolName, string text) =>
        new("tool", text, callId, toolName);
}

public sealed class ProviderToolCall
{
    public ProviderToolCall(string callId, string name, string argumentsJson)
    {
        CallId = callId;
        Name = name;
        ArgumentsJson = argumentsJson;
    }

    public string CallId { get; }
    public string Name { get; }
    public string ArgumentsJson { get; }
}

public abstract class ProviderEvent
{
    private protected ProviderEvent() { }
}

public sealed class ProviderTextDeltaEvent : ProviderEvent
{
    public ProviderTextDeltaEvent(string text) => Text = text;
    public string Text { get; }
}

public sealed class ProviderToolCallEvent : ProviderEvent
{
    public ProviderToolCallEvent(string callId, string name, string argumentsJson)
    {
        CallId = callId;
        Name = name;
        ArgumentsJson = argumentsJson;
    }

    public string CallId { get; }
    public string Name { get; }
    public string ArgumentsJson { get; }
}
