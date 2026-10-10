using System;

namespace RevitMcpChat.Agent.Mcp;

public sealed class McpConnectionException : Exception
{
    public McpConnectionException()
        : base("Could not read the rvt-mcp tool list. Check the Revit connection, then restart the chat.")
    {
    }

    public McpConnectionException(string message)
        : base(message)
    {
    }
}
