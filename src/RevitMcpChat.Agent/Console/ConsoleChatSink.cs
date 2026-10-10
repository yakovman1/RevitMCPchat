using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.History;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.Console;

public sealed class ConsoleChatSink : IChatEventSink
{
    private readonly TextReader _input;
    private readonly TextWriter _output;

    public ConsoleChatSink() : this(System.Console.In, System.Console.Out) { }

    public ConsoleChatSink(TextReader input, TextWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public Task PublishAsync(ChatEvent chatEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (chatEvent.Kind)
        {
            case "user":
                break;
            case "assistant_delta":
                _output.Write(chatEvent.Text);
                _output.Flush();
                break;
            case "assistant_complete":
                _output.WriteLine();
                break;
            case "tool_call":
                _output.WriteLine();
                _output.WriteLine("Requested tool: " + chatEvent.ToolName);
                if (string.Equals(chatEvent.ToolName, "revit_send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                {
                    _output.WriteLine("The code will be shown in the approval prompt.");
                }
                else
                {
                    _output.WriteLine(FormatJson(chatEvent.Text));
                }
                break;
            case "tool_approval":
                _output.WriteLine(chatEvent.Text == "approved" ? "Action approved." : "Action rejected.");
                break;
            case "tool_running":
                _output.WriteLine();
                _output.WriteLine("Running tool: " + chatEvent.Text);
                break;
            case "tool_result":
                _output.WriteLine("Tool result: " + chatEvent.Text);
                break;
            case "tool_error":
            case "tool_rejected":
            case "tool_arguments_rejected":
            case "tool_outcome_unknown":
                _output.WriteLine("[" + chatEvent.Kind + "] " + chatEvent.Text);
                break;
            case "turn_limit_reached":
                _output.WriteLine(chatEvent.Text);
                break;
            default:
                _output.WriteLine("[" + chatEvent.Kind + "] " + chatEvent.Text);
                break;
        }

        return Task.CompletedTask;
    }

    public void DisplayTranscript(ChatTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        _output.WriteLine(transcript.Title + " (" + transcript.ChatId.ToString("D") + ")");
        _output.WriteLine("Model identity: " + transcript.ModelIdentity);
        _output.WriteLine("Provider/model: " + transcript.ProviderId + " / " + transcript.ModelId);

        foreach (var chatEvent in transcript.Events)
        {
            switch (chatEvent.Kind)
            {
                case "user":
                    _output.WriteLine("You: " + chatEvent.Text);
                    break;
                case "assistant_complete":
                    _output.WriteLine("AI: " + chatEvent.Text);
                    break;
                case "tool_call":
                    _output.WriteLine("Tool call: " + chatEvent.ToolName);
                    _output.WriteLine(FormatJson(chatEvent.Text));
                    break;
                case "tool_result":
                case "tool_error":
                case "tool_rejected":
                case "tool_arguments_rejected":
                case "tool_outcome_unknown":
                    _output.WriteLine("[" + chatEvent.Kind + "] " + chatEvent.ToolName + ": " + chatEvent.Text);
                    break;
                case "tool_approval":
                    _output.WriteLine("[approval] " + chatEvent.Text);
                    break;
                case "assistant_delta":
                    break;
                default:
                    _output.WriteLine("[" + chatEvent.Kind + "] " + chatEvent.Text);
                    break;
            }
        }

        _output.Flush();
    }

    public Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine();
        _output.WriteLine("Approval required: " + request.ToolName);
        if (!string.IsNullOrWhiteSpace(request.Summary))
        {
            _output.WriteLine(request.Summary);
        }

        if (request.IsCode)
        {
            _output.WriteLine("C# code:");
            _output.WriteLine(ExtractCode(request.ArgumentsJson) ?? FormatJson(request.ArgumentsJson));
        }
        else
        {
            _output.WriteLine("Arguments:");
            _output.WriteLine(FormatJson(request.ArgumentsJson));
        }

        _output.Write("Execute this action? [y/N]: ");
        _output.Flush();
        var answer = _input.ReadLine();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractCode(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "code", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string FormatJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
