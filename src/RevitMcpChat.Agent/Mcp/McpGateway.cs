using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RevitMcpChat.Agent.Mcp;

public sealed class McpGateway : IMcpGateway
{
    private static readonly string[] RvtMcpConfigurationVariables =
    {
        "BIMWRIGHT_TOOLSETS",
        "BIMWRIGHT_READ_ONLY",
        "BIMWRIGHT_ENABLE_SEND_CODE",
        "BIMWRIGHT_ENABLE_CALL_LOG",
        "BIMWRIGHT_ENABLE_CHANGE_HISTORY",
        "BIMWRIGHT_ENABLE_RESPONSE_GUARD",
        "BIMWRIGHT_RESPONSE_WARN_BYTES",
        "BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES",
        "BIMWRIGHT_RESPONSE_BUDGET_BYTES"
    };

    private McpClient? _client;
    private HashSet<string> _advertisedTools = new(StringComparer.Ordinal);

    public async Task ConnectAsync(int revitPid, string serverPath, CancellationToken cancellationToken)
    {
        if (revitPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revitPid), "A positive Revit process ID is required.");
        }

        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            throw new FileNotFoundException(
                "The rvt-mcp server was not found. Install or repair rvt-mcp, then reconnect.",
                serverPath);
        }

        if (_client is not null)
        {
            throw new InvalidOperationException("This MCP gateway is already connected.");
        }

        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var variableName in RvtMcpConfigurationVariables)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (value is not null)
            {
                environment[variableName] = value;
            }
        }

        // The explicit PID passed below is authoritative. Do not let an inherited target override it.
        environment.Remove("BIMWRIGHT_TARGET");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = $"rvt-mcp pid:{revitPid}",
            Command = serverPath,
            Arguments = new[] { "--target", $"pid:{revitPid}" },
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            StandardErrorLines = _ => { }
        });

        _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _advertisedTools = new HashSet<string>(tools.Select(tool => tool.Name), StringComparer.Ordinal);

        var descriptors = tools.Select(tool =>
        {
            var schema = tool.ProtocolTool.InputSchema;
            var schemaJson = schema.ValueKind == JsonValueKind.Undefined ? "{}" : schema.GetRawText();
            return new McpToolDescriptor(
                tool.Name,
                tool.Description,
                schemaJson,
                tool.ProtocolTool.Annotations?.ReadOnlyHint);
        }).ToArray();
        RvtMcpCompatibility.EnsureCompatible(descriptors);
        return descriptors;
    }

    public async Task<McpCallOutcome> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        if (!_advertisedTools.Contains(name))
        {
            throw new InvalidOperationException($"The MCP tool '{name}' is not in the current advertised tool list.");
        }

        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
        if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("MCP tool arguments must be a JSON object.", nameof(argumentsJson));
        }

        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
            ?? new Dictionary<string, object?>();

        try
        {
            var result = await RequireClient()
                .CallToolAsync(name, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var content = string.Join(Environment.NewLine, result.Content.Select(SerializeContentBlock));
            return new McpCallOutcome(content, result.IsError == true, outcomeUnknown: false);
        }
        catch (Exception)
        {
            // A failed request may have reached Revit. Surface the uncertainty and never retry here.
            return new McpCallOutcome(
                "The MCP response was lost or interrupted. The operation outcome is unknown; inspect the Revit model before retrying.",
                isError: true,
                outcomeUnknown: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _advertisedTools.Clear();
    }

    private McpClient RequireClient() =>
        _client ?? throw new InvalidOperationException("Connect to rvt-mcp before using this gateway.");

    private static string SerializeContentBlock(ContentBlock block)
    {
        if (block is TextContentBlock text)
        {
            return text.Text;
        }

        return JsonSerializer.Serialize(block, block.GetType());
    }
}
