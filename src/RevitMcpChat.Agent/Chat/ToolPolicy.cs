using System;
using RevitMcpChat.Agent.Mcp;

namespace RevitMcpChat.Agent.Chat;

public sealed class ToolPolicy
{
    public bool RequiresApproval(McpToolDescriptor tool)
    {
        if (tool is null)
        {
            throw new ArgumentNullException(nameof(tool));
        }

        if (string.Equals(tool.Name, "revit_send_code_to_revit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tool.Name, "revit_batch_execute", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return tool.ReadOnlyHint != true;
    }
}
