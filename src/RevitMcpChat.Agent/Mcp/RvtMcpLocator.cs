using System;
using System.IO;

namespace RevitMcpChat.Agent.Mcp;

public static class RvtMcpLocator
{
    public static string? FindInstalledServer(string? localAppDataRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;

        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var serverPath = Path.Combine(root, "Bimwright", "rvt-mcp", "server", "current", "rvt-mcp.exe");
        return File.Exists(serverPath) ? serverPath : null;
    }
}
