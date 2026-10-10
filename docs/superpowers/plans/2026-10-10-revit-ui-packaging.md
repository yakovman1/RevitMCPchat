# Revit MCP Chat Panel and Packaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the separate dockable Revit 2022–2024 add-in and an installable package that operates the completed local chat agent.

**Architecture:** The prepared .NET Framework 4.8 project becomes a multi-year WPF Revit shell. It hosts a dockable pane and talks to the .NET 8 agent through a current-user named pipe; the agent launches the installed `rvt-mcp` server. Packaging builds three Revit variants and one self-contained agent, with no real key in the public artifact.

**Tech Stack:** C#/.NET Framework 4.8, Revit API 2022–2024 reference packages, WPF/MVVM, .NET 8 named pipes, PowerShell packaging, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-10-revit-mcp-chat-design.md`. Execute after the agent-core and providers-history plans.

## Global Constraints

- The chat is a separate Revit add-in; it does not modify or bundle `rvt-mcp`.
- Build a distinct add-in artifact for Revit 2022, 2023 and 2024, all targeting .NET Framework 4.8.
- The panel has Chat, History and Settings tabs, and is dockable inside Revit.
- The add-in never runs network or MCP work on the Revit UI thread.
- The active Revit PID is passed to the agent; a chat for another model cannot call MCP.
- The UI has no read-only/write-mode toggle; it reflects the tool set supplied by `rvt-mcp`.
- User keys stay DPAPI-protected; admin keys may be injected only for a private release build, and are not hidden against binary inspection.

## Review Focus

1. Two Revit processes of the same year must launch separately pinned agents; Task 2 tests distinct PIDs and pipe names.
2. Closing Revit must stop its child agent without touching other sessions; Task 2 tests lifecycle isolation.
3. Opening a chat for another model must show history but disable Send/tool calls; Task 3 tests this state.
4. Missing `rvt-mcp` must give install guidance rather than a blank panel; Task 4 tests the package and Task 3 tests the status.
5. A key saved in Settings must never be redisplayed; Task 3 tests a masked/empty input after save.

## File map

- `RevitMCPchat/RevitMCPchat/RevitMCPchat.csproj`: convert the prepared class library to SDK-style net48 WPF with `RevitYear=2022|2023|2024` and year-matched Revit API references.
- `RevitMCPchat/RevitMCPchat/RevitApp.cs`, `ShowChatCommand.cs`, `ChatPaneProvider.cs`: add-in registration, ribbon entry and dockable pane.
- `RevitMCPchat/RevitMCPchat/Model/`: active document identity reader and Revit event bridge.
- `RevitMCPchat/RevitMCPchat/Agent/`: agent process host and named-pipe client.
- `RevitMCPchat/RevitMCPchat/Views/`, `ViewModels/`: Chat, History and Settings UI.
- `src/RevitMcpChat.Agent/Ipc/`: named-pipe server and command dispatcher.
- `packaging/`: manifest template, release build, install, uninstall and smoke-check scripts.
- `tests/RevitMcpChat.Agent.Tests/` and `tests/RevitMcpChat.RevitLogic.Tests/`: IPC, view-model, identity and package tests that run without Revit.

### Task 1: Revit shell and dockable pane

**Files:** Modify `RevitMCPchat/RevitMCPchat/RevitMCPchat.csproj`, remove `Class1.cs`; create `RevitApp.cs`, `ShowChatCommand.cs`, `ChatPaneProvider.cs`, `Views/ChatPane.xaml`, `Views/ChatPane.xaml.cs`, `packaging/RevitMcpChat.addin.template`, `tests/RevitMcpChat.RevitLogic.Tests/ManifestTests.cs`.

**Interfaces:** `RevitApp : IExternalApplication` registers DockablePane GUID `87e65a3f-c768-4d13-bd68-0d895f78d77c` and a ribbon command. `ShowChatCommand : IExternalCommand` shows the pane. `ChatPaneProvider : IDockablePaneProvider` supplies the WPF page. The manifest references the built add-in DLL and `RevitMCPchat.RevitApp` full class name.

- [ ] **Step 1: Write a failing manifest test** `ManifestNamesExternalApplicationAndStablePane` that asserts a valid AddIn manifest, the exact application class and a nonempty stable GUID.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.RevitLogic.Tests/RevitMcpChat.RevitLogic.Tests.csproj --filter ManifestNamesExternalApplicationAndStablePane`; expect FAIL.
- [ ] **Step 3: Convert the prepared project and implement the minimal pane**. Use year-matched `Nice3point.Revit.Api.RevitAPI` and `RevitAPIUI` reference packages with `Private=false`; pin package versions for repeatable builds. Register the pane during `OnStartup` and dispose resources during `OnShutdown`.
- [ ] **Step 4: Run** the manifest test and `dotnet build RevitMCPchat/RevitMCPchat/RevitMCPchat.csproj -p:RevitYear=2022`, then 2023 and 2024; expect three successful builds.
- [ ] **Step 5: Commit** shell and manifest files with message `feat: register dockable chat pane in Revit 2022-2024`.

### Task 2: Pipe protocol and per-Revit agent lifecycle

**Files:** Create `src/RevitMcpChat.Agent/Ipc/AgentPipeServer.cs`, `AgentCommandDispatcher.cs`, `RevitMCPchat/RevitMCPchat/Agent/AgentProcessHost.cs`, `AgentPipeClient.cs`, `Model/ActiveDocumentReader.cs`, `Model/DocumentEventBridge.cs`; modify contracts for pipe commands; tests `AgentPipeTests.cs`, `AgentLifecycleTests.cs`, `ModelIdentityBridgeTests.cs`.

**Interfaces:** `AgentProcessHost.StartAsync(int revitPid, string pipeName, CancellationToken)` starts one agent for the current Revit process. `AgentPipeClient.SendAsync(AgentCommand, CancellationToken) : Task<AgentReply>` uses framed JSON over a pipe restricted to the current Windows user. `AgentCommand` has `RequestId`, `Kind`, `PayloadJson`; kinds are `send`, `approve`, `reject`, `history-list`, `history-open`, `history-rename`, `history-delete`, `settings-get`, `settings-save`, `settings-delete-key`, `reconnect`. `AgentReply` has `RequestId`, `Kind`, `PayloadJson`, `ErrorCode`; streamed `ChatEvent` messages use the same framing. `ActiveDocumentReader.Read(UIApplication) : ActiveModelInfo` returns the normalized saved/central path or unsaved session ID; only Revit API context calls it.

- [ ] **Step 1: Write failing tests** `TwoRevitPidsUseDifferentPipeSessions`, `ClosingOneHostStopsOnlyItsAgent`, `PipeRejectsInvalidCommand`, `DocumentSaveBindsUnsavedIdentity`, `SaveAsCreatesNewIdentity`, `ActiveDocumentChangeBeforeApprovalBlocksCall`; assert exact PID routing and no cross-session events.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter "FullyQualifiedName~AgentPipeTests|FullyQualifiedName~AgentLifecycleTests"` and the Revit logic tests; expect FAIL.
- [ ] **Step 3: Implement pipe and lifecycle** with length-bounded frames, same-user pipe ACL, no key on command line, async Revit-side I/O and a Revit API event bridge that marshals document reads through valid Revit context.
- [ ] **Step 4: Run** the IPC, lifecycle and identity tests; expect PASS, then build each Revit year.
- [ ] **Step 5: Commit** IPC and lifecycle files with message `feat: connect pane to isolated local agent`.

### Task 3: Chat, history and settings views

**Files:** Create `ViewModels/ChatViewModel.cs`, `HistoryViewModel.cs`, `SettingsViewModel.cs`, `Views/HistoryView.xaml`, `Views/SettingsView.xaml`, `Views/ToolApprovalView.xaml`; modify `ChatPane.xaml`; tests `ChatViewModelTests.cs`, `HistoryViewModelTests.cs`, `SettingsViewModelTests.cs`.

**Interfaces:** `ChatViewModel` consumes `ChatEvent` and sends `ChatTurnRequest`/approval commands over `AgentPipeClient`; `HistoryViewModel` requests current-model or all-chat summaries; `SettingsViewModel` edits provider/model/address, sends secret values once, and receives only `hasUserKey` / `hasAdminKey` booleans. All user-facing command states implement `INotifyPropertyChanged` and `ICommand`.

- [ ] **Step 1: Write failing tests** `WrongModelChatShowsHistoryButDisablesSend`, `NoOpenDocumentDisablesMcpButKeepsHistory`, `MissingMcpShowsInstallGuidance`, `ApprovalDisplaysExactToolArguments`, `SavingKeyClearsVisibleInput`, `CurrentModelAndAllChatFiltersDiffer`, `DeleteChatRemovesOnlySelectedChat`.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.RevitLogic.Tests/RevitMcpChat.RevitLogic.Tests.csproj --filter "FullyQualifiedName~ViewModelTests"`; expect FAIL.
- [ ] **Step 3: Implement the WPF tabs and view models** with status, streaming text, provider/model selector, new/open/rename/delete chat, full approval detail and disabled tool actions for model mismatch. Keep keys out of UI event logs and never return them over IPC.
- [ ] **Step 4: Run** view-model tests and all three Revit-year builds; expect PASS. Inspect the pane in a lightweight WPF preview host when Revit is unavailable.
- [ ] **Step 5: Commit** views and tests with message `feat: add dockable chat history and settings UI`.

### Task 4: Release package and admin-key build path

**Files:** Create `packaging/build-release.ps1`, `install.ps1`, `uninstall.ps1`, `verify-package.ps1`; create `src/RevitMcpChat.Agent/Settings/GeneratedAdminKeySource.cs` via build generation only; create `README.md`, `docs/install.md`; tests `PackageTests.cs`.

**Interfaces:** `build-release.ps1 -Configuration Release [-AdminKeysFile <local path>]` produces a ZIP with three add-in variants, self-contained `win-x64` agent, manifest template and installer scripts. `install.ps1` checks the documented installed `rvt-mcp` path, copies chat files only and writes per-year manifests. `uninstall.ps1` removes only chat files; user data is preserved unless an explicit purge option is passed.

- [ ] **Step 1: Write failing package tests** `PublicPackageHasNoEmbeddedKeySource`, `PackageContainsThreeYearManifests`, `InstallWithoutRvtMcpShowsGuidance`, `IncompatibleRvtMcpShowsUpdateGuidance`, `UninstallPreservesHistory`, `PrivateBuildKeyIsNotWrittenToConfig`; use temporary target directories and a disposable test key.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter FullyQualifiedName~PackageTests`; expect FAIL.
- [ ] **Step 3: Implement build/install scripts and docs**. Generate admin-key C# only under ignored build output and wire it as `IAdminKeySource` in the agent startup; avoid echoing it, remove temporary plaintext on completion, and keep the public build key-free. Verify `rvt-mcp` location and compatibility through MCP initialization and required capabilities rather than installing or modifying it.
- [ ] **Step 4: Run** package tests, `packaging/verify-package.ps1` and the full test suite; expect PASS. Build Release ZIP and inspect its file list.
- [ ] **Step 5: Commit** packaging, docs and tests with message `feat: package separate Revit MCP chat add-in`.

### Task 5: End-to-end validation and release notes

**Files:** Create `docs/test-matrix.md`; update `README.md` with supported versions, provider setup, `rvt-mcp` dependency, security boundary and known validation limits.

**Interfaces:** The test matrix records results for Revit 2022, 2023 and 2024, read-only and write-enabled `rvt-mcp`, each provider's text/tool support, missing dependency, disconnect and multi-instance cases.

- [ ] **Step 1: Write the acceptance matrix** with expected observations for panel load, model identity, chat/history, read-only tools, denied mutation, approved mutation, `send_code` confirmation and lost write response.
- [ ] **Step 2: Run** all unit, contract and package tests plus `dotnet build` for each Revit year; record exact commands and results.
- [ ] **Step 3: Run available live Revit checks** against installed Revit 2022–2024 and `rvt-mcp`; where Revit is not installed, mark those rows `Not run — host unavailable`, never `Pass`.
- [ ] **Step 4: Review built ZIP and documentation** against the spec, including no secret in source, config, UI or diagnostics.
- [ ] **Step 5: Commit** matrix and final documentation with message `docs: record Revit MCP chat release validation`.

**Working result:** A separate installable dockable add-in for Revit 2022–2024, with a local agent, all requested providers, model-scoped history, protected user keys and confirmed model changes when `rvt-mcp` exposes them.
