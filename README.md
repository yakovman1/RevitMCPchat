# Revit MCP Chat

Revit MCP Chat is a separate, dockable Revit add-in for Revit 2022, 2023 and 2024. This release does not support Revit 2025 or newer. It provides an AI chat, model-scoped chat history, provider settings and confirmation prompts for model-changing MCP tools.

The add-in targets .NET Framework 4.8. Its local .NET 8 agent starts the separately installed [`rvt-mcp`](https://github.com/yakovman1/rvt-mcp) server and talks to it over MCP stdio. Revit MCP Chat does not install or modify `rvt-mcp`.

## Install

Download and extract the release ZIP, then run `install.ps1` in PowerShell. The installer detects installed Revit 2022–2024 versions, copies only the chat add-in and agent files, and writes a manifest for each detected version. It does not overwrite user history, settings or DPAPI-protected keys.

Install `rvt-mcp` separately using its own instructions. The expected server path is `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`. If it is missing, the chat installer prints the upstream install link and continues installing the chat add-in. If Revit is open, the installer checks MCP initialization and the Revit tool catalog. Otherwise, the same checks run when the chat reconnects inside Revit.

See [install.md](docs/install.md) for detailed setup and removal instructions.

## AI services

Built-in profiles are provided for OpenAI, Anthropic, Gemini, DeepSeek, Yandex AI Studio/Alice AI, Sber GigaChat and OpenAI-compatible endpoints. Other protocols need an adapter. Model IDs and endpoint addresses can be edited in Settings.

User API keys are protected with Windows DPAPI for the current Windows account. If a user key and an administrator key both exist for the same provider, the user key takes priority. The interface and settings file only expose whether a key exists and which source is active.

## Administrator-provided keys

Administrators can build a private package with a local JSON file that maps provider IDs to keys:

```json
{
  "openai": "replace-with-a-real-key",
  "gemini": "replace-with-another-key"
}
```

Run `packaging/build-release.ps1 -Configuration Release -AdminKeysFile <local-json-path>`. The build generates a temporary C# key source, compiles it into the private agent, and removes the temporary source after the build. The key is not placed in the UI or settings configuration. A key compiled into a client binary can still be extracted by someone who can inspect that binary or its process memory; use a server-side proxy if the key must remain secret from users.

The public build omits the generated key source. Do not commit the key file or distribute a private package beyond its intended audience.

## Release validation

See [docs/test-matrix.md](docs/test-matrix.md) for automated results, live-host checks and known validation limits. Live Revit tests are reported separately from tests that use simulated MCP servers.

## Build and verify

```powershell
dotnet test RevitMCPchat.slnx
dotnet build RevitMCPchat/RevitMCPchat.csproj -p:RevitYear=2022
dotnet build RevitMCPchat/RevitMCPchat.csproj -p:RevitYear=2023
dotnet build RevitMCPchat/RevitMCPchat.csproj -p:RevitYear=2024
./packaging/build-release.ps1 -Configuration Release
$package = Get-ChildItem ./packaging/artifacts/RevitMcpChat-*-win-x64.zip | Select-Object -First 1
Expand-Archive $package.FullName ./packaging/artifacts/verify
./packaging/verify-package.ps1 -PackageRoot ./packaging/artifacts/verify
```

The release script writes a versioned ZIP under `packaging/artifacts`. Revit MCP Chat and `rvt-mcp` are released and updated independently.

## Validation limits

Automated tests cover the local agent, provider protocols, IPC, view models and package scripts. Loading the add-in and exercising Revit tools still requires a live Revit 2022–2024 installation with a compatible `rvt-mcp` add-in and server.
