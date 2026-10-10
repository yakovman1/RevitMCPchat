using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMcpChat.Contracts
{
    [DataContract]
    public sealed class ChatTurnRequest
    {
        [DataMember(Order = 1)]
        public Guid ConversationId { get; set; }
        [DataMember(Order = 2)]
        public string ModelIdentity { get; set; } = string.Empty;
        [DataMember(Order = 3)]
        public string ProviderId { get; set; } = string.Empty;
        [DataMember(Order = 4)]
        public string ModelId { get; set; } = string.Empty;
        [DataMember(Order = 5)]
        public string UserText { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class ToolApprovalRequest
    {
        [DataMember(Order = 1)]
        public string CallId { get; set; } = string.Empty;
        [DataMember(Order = 2)]
        public string ToolName { get; set; } = string.Empty;
        [DataMember(Order = 3)]
        public string ArgumentsJson { get; set; } = string.Empty;
        [DataMember(Order = 4)]
        public string Summary { get; set; } = string.Empty;
        [DataMember(Order = 5)]
        public bool IsCode { get; set; }
    }

    [DataContract]
    public sealed class ChatEvent
    {
        [DataMember(Order = 1)]
        public Guid ConversationId { get; set; }
        [DataMember(Order = 2)]
        public string Kind { get; set; } = string.Empty;
        [DataMember(Order = 3)]
        public string Text { get; set; } = string.Empty;
        [DataMember(Order = 4)]
        public string CallId { get; set; } = string.Empty;
        [DataMember(Order = 5)]
        public string ToolName { get; set; } = string.Empty;
    }

    public interface IChatEventSink
    {
        Task PublishAsync(ChatEvent chatEvent, CancellationToken cancellationToken);
        Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
    }
}
