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

Start a new chat. Every console chat requires a model identity because a Revit process can have more than one document open. Use the identity supplied by the Revit panel or, for a saved model, its canonical `file:` identity. For OpenAI-compatible endpoints, provide the endpoint and model. Yandex AI Studio also needs `--folder-id`:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --model-identity "file:C:\MODELS\HOUSE.RVT" --provider openai --model gpt-4o
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --model-identity "file:C:\MODELS\HOUSE.RVT" --provider openai-compatible --model local-model --base-url http://localhost:1234/v1
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --model-identity "file:C:\MODELS\HOUSE.RVT" --provider yandex-ai-studio --model yandexgpt --folder-id your-folder-id
```

The agent starts `rvt-mcp` for the selected Revit process. Changes still require approval. Use `/exit` to close the chat; its ID is printed when the session starts.

List chats for the last model used, or list every saved chat. Open a chat transcript without connecting to Revit or the internet:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- history
dotnet run --project src/RevitMcpChat.Agent -- history --all
dotnet run --project src/RevitMcpChat.Agent -- open <chat-id>
```

Continue a saved chat while connected to the same model. Pass the same model identity and confirm it belongs to the active Revit document; the console does not infer document identity from the process ID:

```powershell
dotnet run --project src/RevitMcpChat.Agent -- chat --revit-pid 1234 --model-identity "file:C:\MODELS\HOUSE.RVT" --chat-id <chat-id>
```

The console requires `--model-identity` on every chat because a process ID does not identify a Revit document. Unknown chat options are rejected. The Revit panel will supply the document identity from the Revit API so its history survives Revit process restarts and remains separated after Save As.

History stores complete user and assistant text after a turn finishes, but omits streaming deltas. When a turn invokes `revit_send_code_to_revit`, its user and assistant text is replaced with a privacy placeholder, and the C# call body is never written to SQLite. Fenced code and common inline Revit C# statements are also redacted when written directly to history.

Run the checks:

```powershell
dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj
dotnet run --project src/RevitMcpChat.Agent -- --help
```

## Build-provided keys

`Program.CreateAdminKeySource()` currently returns `EmptyAdminKeySource`. A private installer build can replace it with an `IAdminKeySource` implementation generated from the build system's secret store. Keep real values out of the public source tree and packaged settings files. User keys take precedence; removing a user key makes the build key active when one is supplied.

A build-provided key is present in a client application that runs on the user's computer. Compilation and obfuscation do not make it impossible to extract from the binaries or process memory. Use a server-side proxy when the key must remain inaccessible to clients.
