using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMcpChat.Contracts
{
    [DataContract]
    public sealed class AgentCommand
    {
        [DataMember(Order = 1)]
        public Guid RequestId { get; set; } = Guid.NewGuid();

        [DataMember(Order = 2)]
        public string Kind { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string PayloadJson { get; set; } = "{}";
    }

    [DataContract]
    public sealed class AgentReply
    {
        [DataMember(Order = 1)]
        public Guid RequestId { get; set; }

        [DataMember(Order = 2)]
        public string Kind { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string PayloadJson { get; set; } = "{}";

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public string? ErrorCode { get; set; }
    }

    public static class AgentCommandKinds
    {
        public const string Send = "send";
        public const string Approve = "approve";
        public const string Reject = "reject";
        public const string HistoryList = "history-list";
        public const string HistoryCreate = "history-create";
        public const string HistoryOpen = "history-open";
        public const string HistoryRename = "history-rename";
        public const string HistoryDelete = "history-delete";
        public const string SettingsGet = "settings-get";
        public const string SettingsSave = "settings-save";
        public const string SettingsDeleteKey = "settings-delete-key";
        public const string Reconnect = "reconnect";
        public const string ModelSaved = "model-saved";
        public const string ModelActive = "model-active";
        public const string Shutdown = "shutdown";

        public static bool IsSupported(string? kind)
        {
            switch (kind)
            {
                case Send:
                case Approve:
                case Reject:
                case HistoryList:
                case HistoryCreate:
                case HistoryOpen:
                case HistoryRename:
                case HistoryDelete:
                case SettingsGet:
                case SettingsSave:
                case SettingsDeleteKey:
                case Reconnect:
                case ModelSaved:
                case ModelActive:
                case Shutdown:
                    return true;
                default:
                    return false;
            }
        }
    }

    public static class AgentReplyKinds
    {
        public const string Response = "response";
        public const string Event = "event";
        public const string Approval = "approval";
        public const string Error = "error";
        public const string Shutdown = "shutdown";
    }

    [DataContract]
    public sealed class ActiveModelInfo
    {
        [DataMember(Order = 1)]
        public string Identity { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public bool IsSaved { get; set; }

        [DataMember(Order = 4)]
        public bool IsOpen { get; set; }
    }

    [DataContract]
    public sealed class ModelIdentityChange
    {
        [DataMember(Order = 1)]
        public string PreviousIdentity { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string CurrentIdentity { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public bool ShouldBindExistingChats { get; set; }

        public static ModelIdentityChange? FromSave(string previousIdentity, string currentIdentity)
        {
            if (string.IsNullOrWhiteSpace(previousIdentity))
            {
                throw new ArgumentException("The previous model identity is required.", nameof(previousIdentity));
            }

            if (string.IsNullOrWhiteSpace(currentIdentity))
            {
                throw new ArgumentException("The current model identity is required.", nameof(currentIdentity));
            }

            if (string.Equals(previousIdentity, currentIdentity, StringComparison.Ordinal))
            {
                return null;
            }

            return new ModelIdentityChange
            {
                PreviousIdentity = previousIdentity,
                CurrentIdentity = currentIdentity,
                ShouldBindExistingChats = previousIdentity.StartsWith("unsaved:", StringComparison.Ordinal)
            };
        }
    }

    [DataContract]
    public sealed class ToolApprovalPrompt
    {
        [DataMember(Order = 1)]
        public Guid ConversationId { get; set; }

        [DataMember(Order = 2)]
        public string ModelIdentity { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public ToolApprovalRequest Approval { get; set; } = new ToolApprovalRequest();
    }

    [DataContract]
    public sealed class ToolApprovalDecision
    {
        [DataMember(Order = 1)]
        public Guid ConversationId { get; set; }

        [DataMember(Order = 2)]
        public string CallId { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string ActiveModelIdentity { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public bool Approved { get; set; }
    }

    [DataContract]
    public sealed class HistoryListRequest
    {
        [DataMember(Order = 1)]
        public string ModelIdentity { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public bool AllChats { get; set; }
    }

    [DataContract]
    public sealed class HistoryCreateRequest
    {
        [DataMember(Order = 1)]
        public string ModelIdentity { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class HistoryChatRequest
    {
        [DataMember(Order = 1)]
        public Guid ChatId { get; set; }
    }

    [DataContract]
    public sealed class HistoryRenameRequest
    {
        [DataMember(Order = 1)]
        public Guid ChatId { get; set; }

        [DataMember(Order = 2)]
        public string Title { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class ProviderIdRequest
    {
        [DataMember(Order = 1)]
        public string ProviderId { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class ProviderSettingsUpdate
    {
        [DataMember(Order = 1)]
        public string ProviderId { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string BaseUrl { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public string ModelId { get; set; } = string.Empty;

        [DataMember(Order = 5)]
        public System.Collections.Generic.Dictionary<string, string> Parameters { get; set; } = new();

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public string? ApiKey { get; set; }
    }

    [DataContract]
    public sealed class CredentialAvailability
    {
        [DataMember(Order = 1)]
        public string ProviderId { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public bool HasUserKey { get; set; }

        [DataMember(Order = 3)]
        public bool HasAdminKey { get; set; }

        [DataMember(Order = 4)]
        public string EffectiveSource { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class AgentStatus
    {
        [DataMember(Order = 1)]
        public int RevitPid { get; set; }

        [DataMember(Order = 2)]
        public bool IsConnected { get; set; }

        [DataMember(Order = 3)]
        public string Message { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class ChatSummaryDto
    {
        [DataMember(Order = 1)]
        public Guid ChatId { get; set; }

        [DataMember(Order = 2)]
        public string Title { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string ModelIdentity { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public string ProviderId { get; set; } = string.Empty;

        [DataMember(Order = 5)]
        public string ModelId { get; set; } = string.Empty;

        [DataMember(Order = 6)]
        public DateTimeOffset CreatedAt { get; set; }

        [DataMember(Order = 7)]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class ChatTranscriptDto
    {
        [DataMember(Order = 1)]
        public Guid ChatId { get; set; }

        [DataMember(Order = 2)]
        public string Title { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string ModelIdentity { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public string ProviderId { get; set; } = string.Empty;

        [DataMember(Order = 5)]
        public string ModelId { get; set; } = string.Empty;

        [DataMember(Order = 6)]
        public System.Collections.Generic.List<ChatEvent> Events { get; set; } = new();
    }

    [DataContract]
    public sealed class ProviderSettingsDto
    {
        [DataMember(Order = 1)]
        public string ProviderId { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string BaseUrl { get; set; } = string.Empty;

        [DataMember(Order = 4)]
        public string ModelId { get; set; } = string.Empty;

        [DataMember(Order = 5)]
        public System.Collections.Generic.Dictionary<string, string> Parameters { get; set; } = new();

        [DataMember(Order = 6)]
        public CredentialAvailability Credential { get; set; } = new();
    }

    [DataContract]
    public sealed class SettingsSnapshot
    {
        [DataMember(Order = 1)]
        public string SelectedProviderId { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string SelectedModelId { get; set; } = string.Empty;

        [DataMember(Order = 3)]
        public string? LastModelIdentity { get; set; }

        [DataMember(Order = 4)]
        public System.Collections.Generic.List<ProviderSettingsDto> Providers { get; set; } = new();
    }

    public static class AgentPayloadJson
    {
        public static string Serialize<T>(T value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new SerializationException("A JSON payload is required.");
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                return (T)serializer.ReadObject(stream)!;
            }
        }
    }

    public static class AgentPipeNames
    {
        public static string ForRevitPid(int revitPid)
        {
            if (revitPid <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(revitPid));
            }

            return "RevitMcpChat-" + revitPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static class AgentPipeFrameCodec
    {
        public const int MaximumFrameBytes = 1024 * 1024;

        public static Task WriteCommandAsync(Stream stream, AgentCommand command, CancellationToken cancellationToken) =>
            WriteAsync(stream, command, cancellationToken);

        public static Task WriteReplyAsync(Stream stream, AgentReply reply, CancellationToken cancellationToken) =>
            WriteAsync(stream, reply, cancellationToken);

        public static Task<AgentCommand?> ReadCommandAsync(Stream stream, CancellationToken cancellationToken) =>
            ReadAsync<AgentCommand>(stream, cancellationToken);

        public static Task<AgentReply?> ReadReplyAsync(Stream stream, CancellationToken cancellationToken) =>
            ReadAsync<AgentReply>(stream, cancellationToken);

        private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                serializer.WriteObject(buffer, value!);
                payload = buffer.ToArray();
            }

            if (payload.Length == 0 || payload.Length > MaximumFrameBytes)
            {
                throw new InvalidDataException("The IPC frame size is invalid.");
            }

            var header = new byte[4];
            header[0] = (byte)(payload.Length >> 24);
            header[1] = (byte)(payload.Length >> 16);
            header[2] = (byte)(payload.Length >> 8);
            header[3] = (byte)payload.Length;
            await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            var first = await stream.ReadAsync(header, 0, 1, cancellationToken).ConfigureAwait(false);
            if (first == 0)
            {
                return default;
            }

            await ReadExactlyAsync(stream, header, 1, header.Length - 1, cancellationToken).ConfigureAwait(false);
            var length = ((header[0] & 0xff) << 24)
                | ((header[1] & 0xff) << 16)
                | ((header[2] & 0xff) << 8)
                | (header[3] & 0xff);
            if (length <= 0 || length > MaximumFrameBytes)
            {
                throw new InvalidDataException("The IPC frame size is invalid.");
            }

            var payload = new byte[length];
            await ReadExactlyAsync(stream, payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var buffer = new MemoryStream(payload, writable: false))
            {
                return (T?)serializer.ReadObject(buffer);
            }
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                var read = await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The IPC frame ended before all bytes were received.");
                }

                offset += read;
                count -= read;
            }
        }
    }
}
