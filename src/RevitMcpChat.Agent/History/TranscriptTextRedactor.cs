using System.Text.RegularExpressions;

namespace RevitMcpChat.Agent.History;

internal static class TranscriptTextRedactor
{
    private static readonly Regex FencedCode = new(
        @"(?s)(```|~~~).*?\1",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CSharpStatements = new(
        @"(?i)(?:\b(?:doc|document|uidoc|transaction|collector)\s*\.[A-Za-z_][A-Za-z0-9_]*\s*\([^;\r\n]*\)\s*;|\b(?:var|int|string|bool|double|ElementId|FilteredElementCollector)\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*[^;\r\n]+;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = FencedCode.Replace(text, "[code omitted]");
        return CSharpStatements.Replace(text, "[code omitted]");
    }
}
