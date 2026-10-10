using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.Chat;

public sealed class ChatEngine
{
    private const int MaximumToolRounds = 8;
    private const string SystemPrompt =
        "You assist with the active Revit model using only the MCP tools supplied in this request. " +
        "Never invent tool names or arguments. Model changes are performed only after explicit user approval.";

    private readonly IChatProvider _provider;
    private readonly IMcpGateway _mcp;
    private readonly ToolPolicy _toolPolicy;

    public ChatEngine(IChatProvider provider, IMcpGateway mcp, ToolPolicy? toolPolicy = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _toolPolicy = toolPolicy ?? new ToolPolicy();
    }

    public async Task RunAsync(
        ChatTurnRequest request,
        IChatEventSink eventSink,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (eventSink is null)
        {
            throw new ArgumentNullException(nameof(eventSink));
        }

        if (string.IsNullOrWhiteSpace(request.UserText))
        {
            throw new ArgumentException("A user message is required.", nameof(request));
        }

        var advertisedTools = await _mcp.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        var selectedTools = ToolCatalogSelector.Select(request.UserText, advertisedTools);
        var selectedByName = selectedTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var messages = new List<ProviderMessage>
        {
            ProviderMessage.System(SystemPrompt),
            ProviderMessage.User(request.UserText)
        };

        await PublishAsync(eventSink, request.ConversationId, "user", request.UserText, cancellationToken)
            .ConfigureAwait(false);

        for (var round = 0; round < MaximumToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerRequest = new ProviderRequest(request.ModelId, messages.ToArray(), selectedTools);
            var assistantText = new StringBuilder();
            var toolCalls = new List<ProviderToolCall>();

            await foreach (var item in _provider.StreamAsync(providerRequest, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                switch (item)
                {
                    case ProviderTextDeltaEvent textDelta:
                        assistantText.Append(textDelta.Text);
                        await PublishAsync(eventSink, request.ConversationId, "assistant_delta", textDelta.Text, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case ProviderToolCallEvent toolCall:
                        toolCalls.Add(new ProviderToolCall(toolCall.CallId, toolCall.Name, toolCall.ArgumentsJson));
                        break;
                }
            }

            if (toolCalls.Count == 0)
            {
                await PublishAsync(eventSink, request.ConversationId, "assistant_complete", assistantText.ToString(), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            messages.Add(ProviderMessage.Assistant(assistantText.ToString(), toolCalls));

            foreach (var toolCall in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!selectedByName.TryGetValue(toolCall.Name, out var descriptor))
                {
                    var unavailable = "The model requested a tool that was not included in the advertised catalog. No MCP call was made.";
                    await PublishAsync(eventSink, request.ConversationId, "tool_rejected", unavailable, cancellationToken, toolCall.CallId)
                        .ConfigureAwait(false);
                    messages.Add(ProviderMessage.ToolResult(toolCall.CallId, toolCall.Name, unavailable));
                    continue;
                }

                if (!ToolArgumentValidator.TryValidate(descriptor, toolCall.ArgumentsJson, out var validationError))
                {
                    await PublishAsync(eventSink, request.ConversationId, "tool_arguments_rejected", validationError, cancellationToken, toolCall.CallId)
                        .ConfigureAwait(false);
                    messages.Add(ProviderMessage.ToolResult(toolCall.CallId, toolCall.Name, validationError));
                    continue;
                }

                if (_toolPolicy.RequiresApproval(descriptor))
                {
                    var approval = new ToolApprovalRequest
                    {
                        CallId = toolCall.CallId,
                        ToolName = toolCall.Name,
                        ArgumentsJson = toolCall.ArgumentsJson,
                        Summary = ApprovalSummary(descriptor, toolCall.ArgumentsJson),
                        IsCode = string.Equals(toolCall.Name, "revit_send_code_to_revit", StringComparison.OrdinalIgnoreCase)
                    };

                    if (!await eventSink.RequestApprovalAsync(approval, cancellationToken).ConfigureAwait(false))
                    {
                        const string rejected = "The user rejected this tool call. Do not perform the requested change.";
                        await PublishAsync(eventSink, request.ConversationId, "tool_rejected", rejected, cancellationToken, toolCall.CallId)
                            .ConfigureAwait(false);
                        messages.Add(ProviderMessage.ToolResult(toolCall.CallId, toolCall.Name, rejected));
                        continue;
                    }
                }

                await PublishAsync(eventSink, request.ConversationId, "tool_running", toolCall.Name, cancellationToken, toolCall.CallId)
                    .ConfigureAwait(false);
                var outcome = await _mcp.CallToolAsync(toolCall.Name, toolCall.ArgumentsJson, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.OutcomeUnknown)
                {
                    await PublishAsync(eventSink, request.ConversationId, "tool_outcome_unknown", outcome.Content, CancellationToken.None, toolCall.CallId)
                        .ConfigureAwait(false);
                    return;
                }

                var eventKind = outcome.IsError ? "tool_error" : "tool_result";
                await PublishAsync(eventSink, request.ConversationId, eventKind, outcome.Content, cancellationToken, toolCall.CallId)
                    .ConfigureAwait(false);
                messages.Add(ProviderMessage.ToolResult(toolCall.CallId, toolCall.Name, outcome.Content));
            }
        }

        await PublishAsync(
            eventSink,
            request.ConversationId,
            "turn_limit_reached",
            "The model reached the maximum number of tool rounds. Send a new message to continue.",
            cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ApprovalSummary(McpToolDescriptor descriptor, string argumentsJson)
    {
        if (string.Equals(descriptor.Name, "revit_batch_execute", StringComparison.OrdinalIgnoreCase))
        {
            return descriptor.Description + " Review every command in the batch arguments below.";
        }

        return descriptor.Description;
    }

    private static Task PublishAsync(
        IChatEventSink eventSink,
        Guid conversationId,
        string kind,
        string text,
        CancellationToken cancellationToken,
        string callId = "") =>
        eventSink.PublishAsync(new ChatEvent
        {
            ConversationId = conversationId,
            Kind = kind,
            Text = text,
            CallId = callId
        }, cancellationToken);
}
