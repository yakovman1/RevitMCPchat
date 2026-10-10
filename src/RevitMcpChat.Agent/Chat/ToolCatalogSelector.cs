using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RevitMcpChat.Agent.Mcp;

namespace RevitMcpChat.Agent.Chat;

public static class ToolCatalogSelector
{
    public const int MaximumTools = 24;

    public static IReadOnlyList<McpToolDescriptor> Select(
        string userText,
        IReadOnlyList<McpToolDescriptor> advertisedTools)
    {
        if (advertisedTools is null)
        {
            throw new ArgumentNullException(nameof(advertisedTools));
        }

        if (advertisedTools.Count <= MaximumTools)
        {
            return advertisedTools.ToArray();
        }

        var queryTerms = Tokenize(userText ?? string.Empty);
        var ranked = advertisedTools
            .Select((tool, index) => new
            {
                Tool = tool,
                Index = index,
                Score = Tokenize(tool.Name + " " + tool.Description).Count(queryTerms.Contains)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(MaximumTools)
            .Select(item => item.Tool)
            .ToArray();

        if (ranked.Length > 0)
        {
            return ranked;
        }

        return advertisedTools.Take(MaximumTools).ToArray();
    }

    private static HashSet<string> Tokenize(string value)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var token = new StringBuilder();

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else if (token.Length > 0)
            {
                terms.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            terms.Add(token.ToString());
        }

        return terms;
    }
}
