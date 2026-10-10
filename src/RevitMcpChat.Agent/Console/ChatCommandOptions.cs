using System;
using System.Collections.Generic;

namespace RevitMcpChat.Agent.Console;

internal static class ChatCommandOptions
{
    private static readonly HashSet<string> ValuelessOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--console"
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--revit-pid",
        "--model-identity",
        "--chat-id",
        "--provider",
        "--model",
        "--base-url",
        "--server-path",
        "--folder-id",
        "--scope",
        "--auth-scheme",
        "--auth-header",
        "--supports-tools"
    };

    public static bool TryParse(
        string[] args,
        int startIndex,
        out Dictionary<string, string?> options,
        out string error)
    {
        options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        for (var index = startIndex; index < args.Length; index++)
        {
            var name = args[index];
            if (ValuelessOptions.Contains(name))
            {
                if (!options.TryAdd(name, null))
                {
                    error = "A chat option was provided more than once.";
                    return false;
                }

                continue;
            }

            if (!ValueOptions.Contains(name))
            {
                error = "Unknown chat option: " + name;
                return false;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = "A value is required for chat option " + name + ".";
                return false;
            }

            if (!options.TryAdd(name, args[++index]))
            {
                error = "A chat option was provided more than once.";
                return false;
            }
        }

        return true;
    }

    public static bool TryResolveModelIdentity(
        IReadOnlyDictionary<string, string?> options,
        out string modelIdentity)
    {
        modelIdentity = options.TryGetValue("--model-identity", out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;
        return !string.IsNullOrWhiteSpace(modelIdentity);
    }
}
