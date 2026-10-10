using System;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMcpChat.Contracts
{
    public sealed class ChatTurnRequest
    {
        public Guid ConversationId { get; set; }
        public string ModelIdentity { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string UserText { get; set; } = string.Empty;
    }

    public sealed class ToolApprovalRequest
    {
        public string CallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public bool IsCode { get; set; }
    }

    public sealed class ChatEvent
    {
        public Guid ConversationId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string CallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
    }

    public interface IChatEventSink
    {
        Task PublishAsync(ChatEvent chatEvent, CancellationToken cancellationToken);
        Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
    }
}
