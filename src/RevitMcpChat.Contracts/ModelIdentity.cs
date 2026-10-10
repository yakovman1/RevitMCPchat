using System;
using System.IO;

namespace RevitMcpChat.Contracts;

public static class ModelIdentity
{
    public static string ForSaved(string normalizedLocation)
    {
        if (string.IsNullOrWhiteSpace(normalizedLocation))
        {
            throw new ArgumentException("A saved model location is required.", nameof(normalizedLocation));
        }

        var location = normalizedLocation.Trim();
        if (location.IndexOf("://", StringComparison.Ordinal) >= 0)
        {
            var cloudPath = location.Replace('\\', '/').TrimEnd('/');
            return "file:" + cloudPath.ToUpperInvariant();
        }

        var fullPath = Path.GetFullPath(location)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (fullPath.Length > root.Length)
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar);
        }

        return "file:" + fullPath.ToUpperInvariant();
    }

    public static string ForUnsaved(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("An unsaved model session id is required.", nameof(sessionId));
        }

        return "unsaved:" + sessionId.ToString("N");
    }
}
