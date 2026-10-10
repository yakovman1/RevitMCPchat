using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.Ipc;

public sealed class ModelBoundApprovalGate
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

    public async Task<bool> RequestAsync(
        ToolApprovalPrompt prompt,
        Func<ToolApprovalPrompt, CancellationToken, Task> publishPromptAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(publishPromptAsync);
        var key = Key(prompt.ConversationId, prompt.Approval.CallId);
        var pending = new PendingApproval(prompt);
        if (!_pending.TryAdd(key, pending))
        {
            throw new InvalidOperationException("A tool approval with this conversation and call ID is already pending.");
        }

        try
        {
            await publishPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public Task<AgentReply> DecideAsync(ToolApprovalDecision decision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(decision);
        var key = Key(decision.ConversationId, decision.CallId);
        if (!_pending.TryGetValue(key, out var pending))
        {
            return Task.FromResult(Error("approval_not_found", "The tool approval is no longer pending."));
        }

        if (decision.Approved
            && !string.Equals(decision.ActiveModelIdentity, pending.Prompt.ModelIdentity, StringComparison.Ordinal))
        {
            pending.Completion.TrySetResult(false);
            return Task.FromResult(Error(
                "active_model_changed",
                "The active Revit model changed after this approval was requested. The tool call was rejected."));
        }

        pending.Completion.TrySetResult(decision.Approved);
        return Task.FromResult(new AgentReply
        {
            Kind = AgentReplyKinds.Response,
            PayloadJson = "{}"
        });
    }

    private static string Key(Guid conversationId, string callId) =>
        conversationId.ToString("N") + ":" + (callId ?? string.Empty);

    private static AgentReply Error(string code, string message) => new()
    {
        Kind = AgentReplyKinds.Error,
        ErrorCode = code,
        PayloadJson = AgentPayloadJson.Serialize(new ErrorPayload { Message = message })
    };

    private sealed class PendingApproval
    {
        public PendingApproval(ToolApprovalPrompt prompt) => Prompt = prompt;

        public ToolApprovalPrompt Prompt { get; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [System.Runtime.Serialization.DataContract]
    private sealed class ErrorPayload
    {
        [System.Runtime.Serialization.DataMember(Order = 1)]
        public string Message { get; set; } = string.Empty;
    }
}
