using System;
using System.Text.Json;
using Json.Schema;
using RevitMcpChat.Agent.Mcp;

namespace RevitMcpChat.Agent.Chat;

internal static class ToolArgumentValidator
{
    public static bool TryValidate(McpToolDescriptor tool, string argumentsJson, out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Tool arguments must be a JSON object.";
                return false;
            }

            var schema = JsonSchema.FromText(tool.InputSchemaJson);
            var result = schema.Evaluate(document.RootElement);
            if (!result.IsValid)
            {
                error = "Tool arguments do not match the advertised input schema.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception)
        {
            error = "Tool arguments or the advertised input schema could not be validated.";
            return false;
        }
    }
}
