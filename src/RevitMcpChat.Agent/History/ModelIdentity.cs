namespace RevitMcpChat.Agent.History;

public static class ModelIdentity
{
    public static string ForSaved(string normalizedLocation)
    {
        if (string.IsNullOrWhiteSpace(normalizedLocation))
        {
            throw new ArgumentException("A saved model location is required.", nameof(normalizedLocation));
        }

        var fullPath = Path.GetFullPath(normalizedLocation)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

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
