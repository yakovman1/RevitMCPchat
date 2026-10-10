using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.History;
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
    private readonly IChatHistory? _history;

    public ChatEngine(IChatProvider provider, IMcpGateway mcp, ToolPolicy? toolPolicy = null, IChatHistory? history = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _toolPolicy = toolPolicy ?? new ToolPolicy();
        _history = history;
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

        IReadOnlyList<ChatEvent> previousEvents = Array.Empty<ChatEvent>();
        if (_history is not null)
        {
            var transcript = await _history.LoadAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(transcript.ModelIdentity, request.ModelIdentity, StringComparison.Ordinal))
            {
                await PublishAsync(
                    eventSink,
                    request.ConversationId,
                    "chat_model_mismatch",
                    "This chat belongs to a different Revit model and cannot run tools for the active model.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(transcript.ProviderId, request.ProviderId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(transcript.ModelId, request.ModelId, StringComparison.Ordinal))
            {
                await PublishAsync(
                    eventSink,
                    request.ConversationId,
                    "chat_provider_mismatch",
                    "This chat is saved for a different provider or model. Open it with its saved provider and model, or start a new chat.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            previousEvents = transcript.Events;
        }
        IReadOnlyList<McpToolDescriptor> advertisedTools;
        try
        {
            advertisedTools = await _mcp.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpConnectionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new McpConnectionException();
        }

        var selectedTools = ToolCatalogSelector.Select(request.UserText, advertisedTools);
        var selectedByName = selectedTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var messages = new List<ProviderMessage>
        {
            ProviderMessage.System(SystemPrompt)
        };
        messages.AddRange(BuildPreviousMessages(previousEvents));
        messages.Add(ProviderMessage.User(request.UserText));

        var historyTurnId = Guid.NewGuid().ToString("N");
        var containsExecutableCode = false;
        var userEventPersisted = false;
        try
        {
            await PublishAsync(
                eventSink,
                request.ConversationId,
                "user",
                request.UserText,
                cancellationToken,
                historyTextOverride: "[User message is being processed.]",
                historyCallId: historyTurnId).ConfigureAwait(false);
            userEventPersisted = _history is not null;

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
                            await PublishAsync(
                                eventSink,
                                request.ConversationId,
                                "assistant_delta",
                                textDelta.Text,
                                cancellationToken).ConfigureAwait(false);
                            break;
                        case ProviderToolCallEvent toolCall:
                            toolCalls.Add(new ProviderToolCall(toolCall.CallId, toolCall.Name, toolCall.ArgumentsJson));
                            break;
                    }
                }

                if (toolCalls.Count == 0)
                {
                    await PublishAsync(
                        eventSink,
                        request.ConversationId,
                        "assistant_complete",
                        assistantText.ToString(),
                        cancellationToken,
                        historyTextOverride: containsExecutableCode
                            ? "[Turn text omitted because it contains executable C# source.]"
                            : null).ConfigureAwait(false);
                    return;
                }

                messages.Add(ProviderMessage.Assistant(assistantText.ToString(), toolCalls));

                foreach (var toolCall in toolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(toolCall.Name, "revit_send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                    {
                        containsExecutableCode = true;
                    }

                    await PublishAsync(
                        eventSink,
                        request.ConversationId,
                        "tool_call",
                        toolCall.ArgumentsJson,
                        cancellationToken,
                        toolCall.CallId,
                        toolCall.Name).ConfigureAwait(false);

                    if (!selectedByName.TryGetValue(toolCall.Name, out var descriptor))
                    {
                        var unavailable = "The model requested a tool that was not included in the advertised catalog. No MCP call was made.";
                        await PublishAsync(eventSink, request.ConversationId, "tool_rejected", unavailable, cancellationToken, toolCall.CallId, toolCall.Name)
                            .ConfigureAwait(false);
                        messages.Add(ProviderMessage.ToolResult(toolCall.CallId, toolCall.Name, unavailable));
                        continue;
                    }

                    if (!ToolArgumentValidator.TryValidate(descriptor, toolCall.ArgumentsJson, out var validationError))
                    {
                        await PublishAsync(eventSink, request.ConversationId, "tool_arguments_rejected", validationError, cancellationToken, toolCall.CallId, toolCall.Name)
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

                        var approved = await eventSink.RequestApprovalAsync(approval, cancellationToken).ConfigureAwait(false);
                        await PublishAsync(
                            eventSink,
                            request.ConversationId,
                            "tool_approval",
                            approved ? "approved" : "rejected",
                            cancellationToken,
                            toolCall.CallId,
                            toolCall.Name).ConfigureAwait(false);
                        if (!approved)
                        {
                            const string rejected = "The user rejected this tool call. Do not perform the requested change.";
                            await PublishAsync(eventSink, request.ConversationId, "tool_rejected", rejected, cancellationToken, toolCall.CallId, toolCall.Name)
                                .ConfigureAwait(false);
                            messages.Add(ProviderMessage.ToolResult(toolCall.CallId, toolCall.Name, rejected));
                            continue;
                        }
                    }

                    await PublishAsync(eventSink, request.ConversationId, "tool_running", toolCall.Name, cancellationToken, toolCall.CallId, toolCall.Name)
                        .ConfigureAwait(false);
                    var outcome = await _mcp.CallToolAsync(toolCall.Name, toolCall.ArgumentsJson, cancellationToken)
                        .ConfigureAwait(false);

                    if (outcome.OutcomeUnknown)
                    {
                        await PublishAsync(eventSink, request.ConversationId, "tool_outcome_unknown", outcome.Content, CancellationToken.None, toolCall.CallId, toolCall.Name)
                            .ConfigureAwait(false);
                        return;
                    }

                    var eventKind = outcome.IsError ? "tool_error" : "tool_result";
                    await PublishAsync(eventSink, request.ConversationId, eventKind, outcome.Content, CancellationToken.None, toolCall.CallId, toolCall.Name)
                        .ConfigureAwait(false);
                    messages.Add(ProviderMessage.ToolResult(toolCall.CallId, toolCall.Name, outcome.Content));
                }
            }

            await PublishAsync(
                eventSink,
                request.ConversationId,
                "turn_limit_reached",
                "The model reached the maximum number of tool rounds. Send a new message to continue.",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (userEventPersisted && _history is not null)
            {
                var safeUserText = containsExecutableCode
                    ? "[Turn text omitted because it contains executable C# source.]"
                    : request.UserText;
                await _history.ReplaceEventTextAsync(
                    request.ConversationId,
                    "user",
                    historyTurnId,
                    safeUserText,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string ApprovalSummary(McpToolDescriptor descriptor, string argumentsJson)
    {
        if (string.Equals(descriptor.Name, "revit_batch_execute", StringComparison.OrdinalIgnoreCase))
        {
            return descriptor.Description + " Review every command in the batch arguments below.";
        }

        return descriptor.Description;
    }

    private static IReadOnlyList<ProviderMessage> BuildPreviousMessages(IReadOnlyList<ChatEvent> events)
    {
        var messages = new List<ProviderMessage>();
        var pendingAssistantText = new StringBuilder();
        var callsWithStoredArguments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (item.Kind == "user")
            {
                FlushAssistantText(messages, pendingAssistantText);
                messages.Add(ProviderMessage.User(item.Text));
            }
            else if (item.Kind == "assistant_delta")
            {
                pendingAssistantText.Append(item.Text);
            }
            else if (item.Kind == "assistant_complete")
            {
                messages.Add(new ProviderMessage("assistant", item.Text));
                pendingAssistantText.Clear();
            }
            else if (item.Kind == "turn_limit_reached")
            {
                FlushAssistantText(messages, pendingAssistantText);
                messages.Add(new ProviderMessage("assistant", item.Text));
            }
            else if (item.Kind == "tool_call")
            {
                if (TryCreateStoredToolCall(item, out var toolCall))
                {
                    messages.Add(ProviderMessage.Assistant(pendingAssistantText.ToString(), new[] { toolCall }));
                    callsWithStoredArguments.Add(item.CallId);
                }
                else
                {
                    var omittedDetails = string.Equals(
                        item.ToolName,
                        "revit_send_code_to_revit",
                        StringComparison.OrdinalIgnoreCase)
                        ? "[A previous C# code call was omitted from local history.]"
                        : "[A previous tool call could not be reconstructed from local history.]";
                    var summary = pendingAssistantText.ToString() + omittedDetails;
                    messages.Add(new ProviderMessage("assistant", summary));
                }

                pendingAssistantText.Clear();
            }
            else if (item.Kind is "tool_result" or "tool_error" or "tool_rejected"
                or "tool_arguments_rejected" or "tool_outcome_unknown")
            {
                if (callsWithStoredArguments.Remove(item.CallId))
                {
                    messages.Add(ProviderMessage.ToolResult(item.CallId, item.ToolName, item.Text));
                }
                else
                {
                    var label = string.IsNullOrWhiteSpace(item.ToolName) ? "Revit tool" : item.ToolName;
                    messages.Add(ProviderMessage.User($"Previous {label} outcome ({item.Kind}): {item.Text}"));
                }
            }
        }

        FlushAssistantText(messages, pendingAssistantText);
        return messages;
    }

    private static bool TryCreateStoredToolCall(ChatEvent item, out ProviderToolCall toolCall)
    {
        toolCall = null!;
        if (string.IsNullOrWhiteSpace(item.ToolName) || string.IsNullOrWhiteSpace(item.CallId))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            toolCall = new ProviderToolCall(item.CallId, item.ToolName, item.Text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void FlushAssistantText(List<ProviderMessage> messages, StringBuilder pendingAssistantText)
    {
        if (pendingAssistantText.Length == 0)
        {
            return;
        }

        messages.Add(new ProviderMessage("assistant", pendingAssistantText.ToString()));
        pendingAssistantText.Clear();
    }

    private async Task PublishAsync(
        IChatEventSink eventSink,
        Guid conversationId,
        string kind,
        string text,
        CancellationToken cancellationToken,
        string callId = "",
        string toolName = "",
        string? historyTextOverride = null,
        string? historyCallId = null)
    {
        var chatEvent = new ChatEvent
        {
            ConversationId = conversationId,
            Kind = kind,
            Text = text,
            CallId = callId,
            ToolName = toolName
        };
        await eventSink.PublishAsync(chatEvent, cancellationToken).ConfigureAwait(false);
        if (_history is not null && !string.Equals(kind, "assistant_delta", StringComparison.Ordinal))
        {
            await _history.AppendAsync(conversationId, new ChatEvent
            {
                ConversationId = chatEvent.ConversationId,
                Kind = chatEvent.Kind,
                Text = historyTextOverride ?? chatEvent.Text,
                CallId = historyCallId ?? chatEvent.CallId,
                ToolName = chatEvent.ToolName
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
