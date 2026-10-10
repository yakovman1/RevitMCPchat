# Local agent development

The agent is a .NET 8 console application used to develop and verify the services that the Revit panel will call. The Revit add-in remains a separate .NET Framework 4.8 project.

## Commands

List supported providers and whether a user or build key is available:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- providers
```

Save a provider key. The prompt reads keystrokes without echo and the value is protected with Windows DPAPI for the current account:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- settings set-key openai
```

Supported provider IDs include `openai`, `anthropic`, `gemini`, `deepseek`, `yandex-ai-studio`, `gigachat`, and `openai-compatible`. Remove a user key to reveal whether a build key is available:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- settings remove-key openai
```

Start a new chat. For OpenAI-compatible endpoints, provide the endpoint and model. Built-in provider endpoints are used when `--base-url` is omitted. Yandex AI Studio also needs `--folder-id`:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --provider openai --model gpt-4o
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --provider openai-compatible --model local-model --base-url http://localhost:1234/v1
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --provider yandex-ai-studio --model yandexgpt --folder-id your-folder-id
```

The agent starts `rvt-mcp` for the selected Revit process. Changes still require approval. Use `/exit` to close the chat; its ID is printed when the session starts.

List chats for the last model used, or list every saved chat. Open a chat transcript without connecting to Revit or the internet:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- history
dotnet run --project src/RevitMcpChat.Agent -- history --all
dotnet run --project src/RevitMcpChat.Agent -- open <chat-id>
```

Continue a saved chat while connected to the same model:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --chat-id <chat-id>
```

The console's default model identity is scoped to the Revit process ID. If the Revit process was restarted, pass the saved identity with `--model-identity` only when you have confirmed it is the same model. The Revit panel will provide the document identity directly so its history survives Revit process restarts and remains separated after Save As.

Run the checks:

```powershell
dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj
dotnet run --project src/RevitMcpChat.Agent -- --help
```

## Build-provided keys

`Program.CreateAdminKeySource()` currently returns `EmptyAdminKeySource`. A private installer build can replace it with an `IAdminKeySource` implementation generated from the build system's secret store. Keep real values out of the public source tree and packaged settings files. User keys take precedence; removing a user key makes the build key active when one is supplied.

A build-provided key is present in a client application that runs on the user's computer. Compilation and obfuscation do not make it impossible to extract from the binaries or process memory. Use a server-side proxy when the key must remain inaccessible to clients.
