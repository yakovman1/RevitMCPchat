using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Console;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.Providers;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent;

internal static class Program
{
    private const string ApiKeyEnvironmentVariable = "REVITMCPCHAT_API_KEY";

    private static async Task<int> Main(string[] args)
    {
        if (Array.Exists(args, argument => argument == "--help" || argument == "-h"))
        {
            PrintUsage();
            return 0;
        }

        if (!TryParseArguments(args, out var options, out var parseError))
        {
            System.Console.Error.WriteLine(parseError);
            PrintUsage();
            return 2;
        }

        if (!options.ContainsKey("--console"))
        {
            System.Console.Error.WriteLine("The --console mode is required.");
            PrintUsage();
            return 2;
        }

        if (!int.TryParse(options.GetValueOrDefault("--revit-pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var revitPid) ||
            revitPid <= 0)
        {
            System.Console.Error.WriteLine("--revit-pid must be a positive process ID.");
            return 2;
        }

        var providerId = options.GetValueOrDefault("--provider");
        if (!string.Equals(providerId, "openai-compatible", StringComparison.OrdinalIgnoreCase))
        {
            System.Console.Error.WriteLine("This build supports --provider openai-compatible.");
            return 2;
        }

        var modelId = options.GetValueOrDefault("--model");
        var baseUrlValue = options.GetValueOrDefault("--base-url");
        if (string.IsNullOrWhiteSpace(modelId) ||
            !Uri.TryCreate(baseUrlValue, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            System.Console.Error.WriteLine("--model and an absolute HTTP(S) --base-url are required.");
            return 2;
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            System.Console.Error.WriteLine("Set the API key in the REVITMCPCHAT_API_KEY environment variable.");
            return 2;
        }

        var serverPath = options.GetValueOrDefault("--server-path") ?? RvtMcpLocator.FindInstalledServer();
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            System.Console.Error.WriteLine(
                "rvt-mcp was not found. Install it from https://github.com/yakovman1/rvt-mcp, then run this command again.");
            return 3;
        }

        using var httpClient = new HttpClient();
        var provider = new OpenAiCompatibleProvider(httpClient, baseUri, apiKey);
        await using var gateway = new McpGateway();
        try
        {
            await gateway.ConnectAsync(revitPid, serverPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.IO.FileNotFoundException || exception is HttpRequestException)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 3;
        }
        catch (Exception)
        {
            System.Console.Error.WriteLine("Could not connect to rvt-mcp. Confirm Revit is open and reconnect.");
            return 3;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        System.Console.CancelKeyPress += cancelHandler;

        try
        {
            var sink = new ConsoleChatSink();
            var engine = new ChatEngine(provider, gateway);
            var conversationId = Guid.NewGuid();

            System.Console.WriteLine("Connected to rvt-mcp for Revit PID " + revitPid + ". Type /exit to close.");
            while (!cancellation.IsCancellationRequested)
            {
                System.Console.Write("> ");
                System.Console.Out.Flush();
                var userText = System.Console.ReadLine();
                if (userText is null || string.Equals(userText.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                try
                {
                    await engine.RunAsync(new ChatTurnRequest
                    {
                        ConversationId = conversationId,
                        ModelIdentity = "revit-pid:" + revitPid.ToString(CultureInfo.InvariantCulture),
                        ProviderId = "openai-compatible",
                        ModelId = modelId,
                        UserText = userText
                    }, sink, cancellation.Token).ConfigureAwait(false);
                }
                catch (ProviderAuthenticationException exception)
                {
                    System.Console.Error.WriteLine(exception.Message);
                    return 4;
                }
                catch (ProviderRequestException exception)
                {
                    System.Console.Error.WriteLine(exception.Message);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
            }

            return 0;
        }
        finally
        {
            System.Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static bool TryParseArguments(
        string[] args,
        out Dictionary<string, string?> options,
        out string error)
    {
        options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--console")
            {
                options[name] = null;
                continue;
            }

            if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = "Invalid command-line arguments.";
                return false;
            }

            options[name] = args[++index];
        }

        return true;
    }

    private static void PrintUsage()
    {
        System.Console.WriteLine(
            "Usage:" + Environment.NewLine +
            "  RevitMcpChat.Agent --console --revit-pid <pid> --provider openai-compatible --model <id> --base-url <url> [--server-path <path>]" +
            Environment.NewLine +
            "Reads the API key from the REVITMCPCHAT_API_KEY environment variable." +
            Environment.NewLine +
            "Type /exit to close the chat.");
    }
}
