using System.Globalization;
using RevitMcpChat.Agent.Console;
using RevitMcpChat.Agent.History;
using RevitMcpChat.Agent.Ipc;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Agent.Providers;
using RevitMcpChat.Agent.Settings;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase)
            || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        await using var workspace = new AgentWorkspace(adminKeySource: CreateAdminKeySource());
        try
        {
            var command = args[0];
            if (string.Equals(command, "verify-mcp", StringComparison.OrdinalIgnoreCase))
            {
                return await VerifyMcpAsync(args).ConfigureAwait(false);
            }

            if (string.Equals(command, "agent", StringComparison.OrdinalIgnoreCase))
            {
                return await RunPipeAgentAsync(workspace, args).ConfigureAwait(false);
            }

            if (string.Equals(command, "providers", StringComparison.OrdinalIgnoreCase))
            {
                return await ListProvidersAsync(workspace).ConfigureAwait(false);
            }

            if (string.Equals(command, "settings", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleSettingsAsync(workspace, args).ConfigureAwait(false);
            }

            if (string.Equals(command, "history", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleHistoryAsync(workspace, args).ConfigureAwait(false);
            }

            if (string.Equals(command, "open", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleOpenAsync(workspace, args).ConfigureAwait(false);
            }

            if (string.Equals(command, "chat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "--console", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleChatAsync(workspace, args, string.Equals(command, "chat", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ConfigureAwait(false);
            }

            System.Console.Error.WriteLine("Unknown command: " + command);
            PrintUsage();
            return 2;
        }
        catch (SecretStoreException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 5;
        }
        catch (KeyNotFoundException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (ProviderAuthenticationException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 4;
        }
        catch (ProviderRequestException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 4;
        }
        catch (ProviderCapabilityException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 4;
        }
        catch (InvalidOperationException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> RunPipeAgentAsync(AgentWorkspace workspace, string[] args)
    {
        if (args.Length != 5
            || !string.Equals(args[1], "--revit-pid", StringComparison.Ordinal)
            || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revitPid)
            || revitPid <= 0
            || !string.Equals(args[3], "--pipe-name", StringComparison.Ordinal))
        {
            System.Console.Error.WriteLine("Agent mode requires --revit-pid <pid> --pipe-name <name>.");
            return 2;
        }

        var pipeName = args[4];
        if (!string.Equals(pipeName, AgentPipeNames.ForRevitPid(revitPid), StringComparison.Ordinal))
        {
            System.Console.Error.WriteLine("The pipe name does not match the Revit process ID.");
            return 2;
        }

        await using var server = new AgentPipeServer(
            pipeName,
            new AgentCommandDispatcher(workspace, revitPid));
        await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> VerifyMcpAsync(string[] args)
    {
        if (args.Length != 5
            || !string.Equals(args[1], "--revit-pid", StringComparison.Ordinal)
            || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revitPid)
            || revitPid <= 0
            || !string.Equals(args[3], "--server-path", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[4]))
        {
            System.Console.Error.WriteLine("Use: verify-mcp --revit-pid <pid> --server-path <rvt-mcp.exe>");
            return 2;
        }

        try
        {
            await using var gateway = new McpGateway();
            await gateway.ConnectAsync(revitPid, args[4], CancellationToken.None).ConfigureAwait(false);
            var tools = await gateway.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
            RvtMcpCompatibility.EnsureCompatible(tools);
            System.Console.WriteLine($"MCP handshake succeeded; {tools.Count} Revit tools are available.");
            return 0;
        }
        catch (McpConnectionException exception)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 3;
        }
        catch (Exception)
        {
            System.Console.Error.WriteLine(
                $"Could not complete the rvt-mcp MCP handshake for Revit PID {revitPid}. Update or reconnect rvt-mcp from {RvtMcpCompatibility.ProjectUrl}.");
            return 3;
        }
    }

    private static async Task<int> ListProvidersAsync(AgentWorkspace workspace)
    {
        var providers = new (string Id, string Name, string Endpoint)[]
        {
            ("openai", "OpenAI", "https://api.openai.com/v1"),
            ("anthropic", "Anthropic", "https://api.anthropic.com"),
            ("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta"),
            ("deepseek", "DeepSeek", "https://api.deepseek.com"),
            ("yandex-ai-studio", "Alice AI / Yandex AI Studio", "https://ai.api.cloud.yandex.net/v1"),
            ("gigachat", "Sber GigaChat", "https://api.giga.chat"),
            ("openai-compatible", "Any OpenAI-compatible API", "custom endpoint")
        };

        foreach (var item in providers)
        {
            string access;
            try
            {
                var credential = await workspace.ResolveCredentialAsync(item.Id).ConfigureAwait(false);
                access = credential.Source switch
                {
                    CredentialSource.User => "user key saved for this Windows account",
                    CredentialSource.Admin => "build-provided access available",
                    _ => "no key saved"
                };
            }
            catch (SecretStoreException)
            {
                access = "saved key cannot be read; re-enter it";
            }

            System.Console.WriteLine($"{item.Id,-18} {item.Name} — {item.Endpoint} ({access})");
        }

        return 0;
    }

    private static async Task<int> HandleSettingsAsync(AgentWorkspace workspace, string[] args)
    {
        if (args.Length != 3)
        {
            System.Console.Error.WriteLine("Use: settings set-key <provider> | settings remove-key <provider>");
            return 2;
        }

        var action = args[1];
        var providerId = args[2];
        if (string.Equals(action, "set-key", StringComparison.OrdinalIgnoreCase))
        {
            var key = ConsoleSecretReader.ReadFromConsole("API key (hidden): ");
            if (string.IsNullOrWhiteSpace(key))
            {
                System.Console.Error.WriteLine("The key is empty; no value was saved.");
                return 2;
            }

            await workspace.SetUserKeyAsync(providerId, key).ConfigureAwait(false);
            System.Console.WriteLine("Key saved for this Windows account.");
            return 0;
        }

        if (string.Equals(action, "remove-key", StringComparison.OrdinalIgnoreCase))
        {
            var credential = await workspace.RemoveUserKeyAsync(providerId).ConfigureAwait(false);
            System.Console.WriteLine(credential.Source switch
            {
                CredentialSource.Admin => "User key removed. Build-provided access is now active.",
                _ => "User key removed. No key is configured for this provider."
            });
            return 0;
        }

        System.Console.Error.WriteLine("Unknown settings action: " + action);
        return 2;
    }

    private static async Task<int> HandleHistoryAsync(AgentWorkspace workspace, string[] args)
    {
        var allChats = args.Length == 2 && string.Equals(args[1], "--all", StringComparison.OrdinalIgnoreCase);
        if (args.Length > 2 || (args.Length == 2 && !allChats))
        {
            System.Console.Error.WriteLine("Use: history [--all]");
            return 2;
        }

        var settings = await workspace.LoadSettingsAsync().ConfigureAwait(false);
        if (!allChats && string.IsNullOrWhiteSpace(settings.LastModelIdentity))
        {
            System.Console.WriteLine("No current model is recorded. Use 'history --all' to list every chat.");
            return 0;
        }

        var chats = await workspace.ListHistoryAsync(allChats).ConfigureAwait(false);
        if (chats.Count == 0)
        {
            System.Console.WriteLine("No chats found.");
            return 0;
        }

        foreach (var chat in chats)
        {
            System.Console.WriteLine(
                $"{chat.ChatId:D}  {chat.Title}  [{chat.ProviderId}/{chat.ModelId}]  {chat.UpdatedAt.ToLocalTime():g}");
        }

        return 0;
    }

    private static async Task<int> HandleOpenAsync(AgentWorkspace workspace, string[] args)
    {
        if (args.Length != 2 || !Guid.TryParse(args[1], out var chatId))
        {
            System.Console.Error.WriteLine("Use: open <chat-id>");
            return 2;
        }

        var transcript = await workspace.OpenChatAsync(chatId).ConfigureAwait(false);
        new ConsoleChatSink().DisplayTranscript(transcript);
        return 0;
    }

    private static async Task<int> HandleChatAsync(AgentWorkspace workspace, string[] args, int optionStart)
    {
        if (!ChatCommandOptions.TryParse(args, optionStart, out var options, out var parseError))
        {
            System.Console.Error.WriteLine(parseError);
            PrintUsage();
            return 2;
        }

        if (!int.TryParse(options.GetValueOrDefault("--revit-pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var revitPid)
            || revitPid <= 0)
        {
            System.Console.Error.WriteLine("--revit-pid must be a positive process ID.");
            return 2;
        }

        if (!ChatCommandOptions.TryResolveModelIdentity(options, out var modelIdentity))
        {
            System.Console.Error.WriteLine("--model-identity is required. Use the exact active model identity; a Revit PID does not identify a document.");
            return 2;
        }
        var chatIdValue = options.GetValueOrDefault("--chat-id");
        var isResuming = !string.IsNullOrWhiteSpace(chatIdValue);
        ChatTranscript? transcript = null;
        ProviderProfile profile;

        if (isResuming)
        {
            if (!Guid.TryParse(chatIdValue, out var parsedChatId))
            {
                System.Console.Error.WriteLine("--chat-id must be a chat GUID.");
                return 2;
            }

            transcript = await workspace.OpenChatAsync(parsedChatId).ConfigureAwait(false);
            if (!string.Equals(transcript.ModelIdentity, modelIdentity, StringComparison.Ordinal))
            {
                System.Console.Error.WriteLine("This chat belongs to a different Revit model. Connect to its original model before continuing.");
                return 3;
            }

            profile = await workspace.GetProfileForChatAsync(parsedChatId).ConfigureAwait(false);
            if ((options.TryGetValue("--provider", out var requestedProvider)
                    && !string.Equals(requestedProvider, transcript.ProviderId, StringComparison.OrdinalIgnoreCase))
                || (options.TryGetValue("--model", out var requestedModel)
                    && !string.Equals(requestedModel, transcript.ModelId, StringComparison.Ordinal)))
            {
                System.Console.Error.WriteLine("A resumed chat uses its saved provider and model. Start a new chat to switch.");
                return 2;
            }
        }
        else
        {
            profile = await GetSelectedProfileAsync(workspace, options).ConfigureAwait(false);
            await workspace.SetActiveProviderAsync(profile).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(profile.ModelId))
        {
            System.Console.Error.WriteLine("Set a model with --model <id> before starting a chat.");
            return 2;
        }

        var credential = await workspace.ResolveCredentialAsync(profile.ProviderId).ConfigureAwait(false);
        if (credential.Source == CredentialSource.Missing)
        {
            System.Console.Error.WriteLine($"No key is configured for '{profile.ProviderId}'. Run: settings set-key {profile.ProviderId}");
            return 4;
        }

        var serverPath = options.GetValueOrDefault("--server-path") ?? RvtMcpLocator.FindInstalledServer();
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            System.Console.Error.WriteLine("rvt-mcp was not found. Install it from https://github.com/yakovman1/rvt-mcp, then run this command again.");
            return 3;
        }

        using var httpClient = new HttpClient();
        var provider = ProviderFactory.Create(profile, credential, httpClient);
        await using var gateway = new McpGateway();
        try
        {
            await gateway.ConnectAsync(revitPid, serverPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or HttpRequestException)
        {
            System.Console.Error.WriteLine(exception.Message);
            return 3;
        }
        catch (Exception)
        {
            System.Console.Error.WriteLine("Could not connect to rvt-mcp. Confirm Revit is open and reconnect.");
            return 3;
        }

        if (!isResuming)
        {
            chatIdValue = (await workspace.CreateChatAsync(modelIdentity).ConfigureAwait(false)).ToString("D");
        }

        var conversationId = Guid.Parse(chatIdValue!);
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
            var engine = workspace.CreateChatEngine(provider, gateway);
            System.Console.WriteLine("Connected to rvt-mcp for Revit PID " + revitPid + ". Chat id: " + conversationId.ToString("D"));
            System.Console.WriteLine("Type /exit to close this chat.");
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
                        ModelIdentity = modelIdentity,
                        ProviderId = profile.ProviderId,
                        ModelId = profile.ModelId,
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
                catch (ProviderCapabilityException exception)
                {
                    System.Console.Error.WriteLine(exception.Message);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (McpConnectionException exception)
                {
                    System.Console.Error.WriteLine(exception.Message);
                    return 3;
                }
            }

            return 0;
        }
        finally
        {
            System.Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<ProviderProfile> GetSelectedProfileAsync(
        AgentWorkspace workspace,
        IReadOnlyDictionary<string, string?> options)
    {
        var settings = await workspace.LoadSettingsAsync().ConfigureAwait(false);
        var providerId = options.GetValueOrDefault("--provider") ?? settings.SelectedProviderId;
        var existing = settings.Providers?.FirstOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        var profile = existing is null
            ? new ProviderProfile { ProviderId = providerId, DisplayName = providerId }
            : new ProviderProfile
            {
                ProviderId = existing.ProviderId,
                DisplayName = existing.DisplayName,
                BaseUrl = existing.BaseUrl,
                ModelId = existing.ModelId,
                Parameters = new Dictionary<string, string>(existing.Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            };

        var selectedModel = options.GetValueOrDefault("--model");
        if (string.IsNullOrWhiteSpace(selectedModel)
            && string.Equals(providerId, settings.SelectedProviderId, StringComparison.OrdinalIgnoreCase))
        {
            selectedModel = settings.SelectedModelId;
        }
        if (!string.IsNullOrWhiteSpace(selectedModel))
        {
            profile.ModelId = selectedModel;
        }

        if (options.TryGetValue("--base-url", out var baseUrl))
        {
            profile.BaseUrl = baseUrl ?? string.Empty;
        }

        SetParameter(options, profile, "--folder-id", "folder_id");
        SetParameter(options, profile, "--scope", "scope");
        SetParameter(options, profile, "--auth-scheme", "auth_scheme");
        SetParameter(options, profile, "--auth-header", "auth_header");
        SetParameter(options, profile, "--supports-tools", "supports_tools");
        return profile;
    }

    private static void SetParameter(
        IReadOnlyDictionary<string, string?> options,
        ProviderProfile profile,
        string option,
        string parameter)
    {
        if (options.TryGetValue(option, out var value))
        {
            profile.Parameters[parameter] = value ?? string.Empty;
        }
    }

    private static IAdminKeySource CreateAdminKeySource()
    {
#if ADMIN_KEYS
        return new GeneratedAdminKeySource();
#else
        return new EmptyAdminKeySource();
#endif
    }

    private static void PrintUsage()
    {
        System.Console.WriteLine(
            "Revit MCP Chat local agent" + Environment.NewLine +
            "  providers" + Environment.NewLine +
            "  verify-mcp --revit-pid <pid> --server-path <rvt-mcp.exe>" + Environment.NewLine +
            "  settings set-key <provider>       Save a key protected for this Windows account" + Environment.NewLine +
            "  settings remove-key <provider>    Remove the user key and reveal build access if configured" + Environment.NewLine +
            "  chat --revit-pid <pid> --model-identity <id> --provider <id> --model <id> [--base-url <url>] [--server-path <path>]" + Environment.NewLine +
            "  chat --revit-pid <pid> --model-identity <id> --chat-id <id>  Resume a saved chat" + Environment.NewLine +
            "  history [--all]" + Environment.NewLine +
            "  open <chat-id>" + Environment.NewLine +
            "  agent --revit-pid <pid> --pipe-name <name> (internal Revit session)" + Environment.NewLine +
            "  --help");
    }
}
