namespace RevitMcpChat.Agent.Mcp;

public static class RvtMcpCompatibility
{
    public const string ProjectUrl = "https://github.com/bimwright/rvt-mcp";

    public static string MissingInstallGuidance =>
        $"rvt-mcp was not found at the standard user install path. Install it separately from {ProjectUrl}, then reconnect.";

    public static string GetIncompatibleGuidance(IEnumerable<McpToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Any(tool => tool.Name.StartsWith("revit_", StringComparison.OrdinalIgnoreCase))
            ? string.Empty
            : $"The installed rvt-mcp did not expose the required Revit tool catalog. Update rvt-mcp from {ProjectUrl}, then reconnect.";
    }

    public static void EnsureCompatible(IReadOnlyList<McpToolDescriptor> tools)
    {
        var guidance = GetIncompatibleGuidance(tools);
        if (guidance.Length > 0)
        {
            throw new McpConnectionException(guidance);
        }
    }
}
