# Revit MCP Chat acceptance matrix

## Automated checks

Results recorded on 2026-10-10 from the current release source:

| Area | Expected behavior | Automated evidence | Result |
|---|---|---|---|
| Dockable pane registration | Revit manifest names the external application and stable pane ID | `ManifestTests.ManifestNamesExternalApplicationAndStablePane` | Pass |
| Per-Revit process isolation | Each Revit PID has a distinct agent pipe; stopping one host leaves other agents running | `AgentPipeTests.TwoRevitPidsUseDifferentPipeSessions`; `AgentLifecycleTests.ClosingOneHostStopsOnlyItsAgent` | Pass |
| Model identity | Saved paths normalize; unsaved identity binds on first save; Save As gets a new identity; a changed active model blocks pending approval | `ModelIdentityTests`; `ModelIdentityBridgeTests` | Pass |
| Active Revit document events | Null activation clears the active document; save events are scoped to that document; closing an inactive document does not change it; cancelled close keeps it active | `ActiveDocumentEventStateTests` | Pass |
| Agent crash recovery | Reconnect restarts an exited child for the same Revit PID and does not start a duplicate when it is alive | `AgentLifecycleTests.EnsureRunningRestartsExitedAgentForSameRevitProcess`; `AgentLifecycleTests.EnsureRunningDoesNotStartDuplicateAgent`; `ChatViewModelTests.ReconnectRestartsAgentAndRefreshesModelBeforeMcpReconnect` | Pass |
| Custom install location | Add-in resolves its sibling agent relative to its own manifest-loaded assembly path | `AgentLifecycleTests.InstalledAgentPathResolvesRelativeToAddinInCustomInstallRoot` | Pass |
| Chat and history | Streaming updates one assistant message; histories filter by current model or all models; rename/delete/open actions stay scoped | `ChatViewModelTests`; `HistoryViewModelTests`; `ChatHistoryTests` | Pass |
| Model mismatch | A chat for another model remains readable and cannot send MCP calls | `ChatViewModelTests.WrongModelChatShowsHistoryButDisablesSend`; `ChatEngineTests.WrongModelChatCannotIssueToolCall` | Pass |
| Read-only tool | A tool marked read-only can run without a confirmation prompt | `ToolPolicyTests.ReadOnlyToolRunsWithoutApproval`; `ChatEngineTests.ReadOnlyToolRunsWithoutApproval` | Pass |
| Denied mutation | Rejecting a write never calls the MCP tool | `ConsoleFlowTests.ConsoleApprovalRejectsWithoutMcpCall`; `ChatEngineTests.RejectedSendCodeNeverCallsMcp` | Pass |
| Approved mutation | An unknown or write-capable tool requires confirmation; approval is durably written before the MCP side effect starts | `ToolPolicyTests.UnknownToolRequiresApproval`; `ChatEngineTests.ApprovedWriteIsDurablyRecordedBeforeMcpSideEffectStarts` | Pass |
| `send_code` | Arbitrary C# always requires approval, even when the tool advertises a read-only hint; source body is omitted from persistent history | `ToolPolicyTests.SendCodeRequiresApprovalEvenWhenReadOnlyHintIsTrue`; `ChatHistoryTests.SendCodeBodyIsAbsentFromDatabase` | Pass |
| Lost write response | The operation is reported as unknown and is not automatically retried | `ChatEngineTests.InterruptedWriteIsNotRetried`; `ChatEngineTests.CancellationDuringInterruptedWriteStillReportsUnknownOutcome` | Pass |
| OpenAI | OpenAI-compatible streaming emits text and tool calls; errors do not echo the key | `OpenAiCompatibleProviderTests.StreamsTextAndToolCallFromSse`; `OpenAiCompatibleProviderTests.Http401ReportsProviderAuthErrorWithoutEchoingKey` | Pass |
| Anthropic | Messages API sends tool schemas and history; streaming emits text and tool use | `ProviderProtocolTests.AnthropicToolUseRoundTrips` | Pass |
| Gemini | Function declarations and tool history are sent; streaming emits text and function calls | `ProviderProtocolTests.GeminiFunctionCallRoundTrips` | Pass |
| DeepSeek | Configured endpoint and bearer key are used; streaming emits text and OpenAI-style tool calls | `ProviderProtocolTests.DeepSeekUsesConfiguredBaseUrl` | Pass |
| Yandex AI Studio / Alice AI | Folder-scoped model and Api-Key header are used; text and OpenAI-style function-call streams are handled | `ProviderProtocolTests.YandexUsesApiKeyHeaderAndFolderModel`; `ProviderProtocolTests.YandexStreamsFunctionCall` | Pass |
| Sber GigaChat | OAuth token refresh works; text and GigaChat function-call streams and function history are handled | `ProviderProtocolTests.GigaChatRefreshesRejectedAccessTokenOnce`; `ProviderProtocolTests.GigaChatStreamsFunctionCallAndFormatsToolHistory` | Pass |
| Other OpenAI-compatible endpoints | Custom HTTP endpoint, bearer key, streaming text and tool calls are supported by the configurable adapter | `ProviderProtocolTests.OpenAiCompatibleRequiresExplicitBaseUrl`; `OpenAiCompatibleProviderTests.StreamsTextAndToolCallFromSse` | Pass |
| User/admin key boundary | User key takes priority; DPAPI file is not plaintext; key input clears; admin presence and source do not return the value | `CredentialResolverTests`; `DpapiSecretStoreTests`; `SettingsViewModelTests`; `PackageTests.PrivateBuildKeyIsNotWrittenToConfig` | Pass |
| Missing `rvt-mcp` | The panel/installer gives an install link and next step | `ChatViewModelTests.MissingMcpShowsInstallGuidance`; `PackageTests.InstallWithoutRvtMcpShowsGuidance` | Pass |
| Incompatible `rvt-mcp` | MCP initialization/tool discovery failure or missing Revit tool catalog shows update guidance and leaves connection false | `PackageTests.IncompatibleRvtMcpShowsUpdateGuidance`; `McpGatewayTests.DispatcherReturnsUpdateGuidanceForIncompatibleRvtMcp` | Pass |
| Release package and uninstall | Public archive has no generated admin-key source; includes year manifests and self-contained agent; ordinary uninstall preserves history; purge removes known data files while preserving unrelated files | `PackageTests` (7 package tests), `verify-package.ps1` | Pass |

## Live Revit checks

No Revit 2022, 2023 or 2024 executable was found at the standard Program Files paths, and no Revit process was running. These host-dependent checks were not run. They must be completed on a workstation with the matching Revit year and a compatible `rvt-mcp` install before calling the release fully validated in Revit.

| Scenario | Expected observation | Result |
|---|---|---|
| Start each Revit year and open AI Chat | Add-in loads; pane opens, docks and reconnects to its own agent | Not run — host unavailable |
| Open, save and Save As a model | Active model identity changes as documented; old chats remain attached to the original model | Not run — host unavailable |
| Send a prompt and reopen a chat | Assistant output streams once; transcript survives Revit restart and can be reopened | Not run — host unavailable |
| Use `rvt-mcp` with `read_only` | Available read-only tools work; mutation tools are absent or rejected by `rvt-mcp` | Not run — host unavailable |
| Deny a proposed mutation | No model change occurs; rejection is returned to the conversation | Not run — host unavailable |
| Approve a proposed mutation | Full arguments are visible before confirmation; only then does the model change | Not run — host unavailable |
| Call `revit_send_code_to_revit` | Full C# body is shown and the call waits for an explicit approval | Not run — host unavailable |
| Interrupt a write after dispatch | UI reports unknown outcome, does not retry, and prompts the user to inspect the model | Not run — host unavailable |
| Disconnect/restart `rvt-mcp` | Panel shows connection state and can reconnect without binding to another Revit process | Not run — host unavailable |
| Run two Revit instances | Each pane controls only its own PID and model | Not run — host unavailable |

## Build and test commands

Commands run on 2026-10-10:

```powershell
dotnet test RevitMCPchat.slnx --no-restore --disable-build-servers -m:1 -v minimal --logger "console;verbosity=detailed"
dotnet build RevitMCPchat/RevitMCPchat.csproj -c Release -p:RevitYear=2022 --no-restore --disable-build-servers -m:1 -v minimal
dotnet build RevitMCPchat/RevitMCPchat.csproj -c Release -p:RevitYear=2023 --no-restore --disable-build-servers -m:1 -v minimal
dotnet build RevitMCPchat/RevitMCPchat.csproj -c Release -p:RevitYear=2024 --no-restore --disable-build-servers -m:1 -v minimal
pwsh -NoProfile -ExecutionPolicy Bypass -File packaging/build-release.ps1 -Configuration Release -OutputDirectory packaging/artifacts -Version 0.1.0
```

`dotnet test` passed 96 tests: 28 in `RevitMcpChat.RevitLogic.Tests` and 68 in `RevitMcpChat.Agent.Tests`. Agent test classes run serially because several clear the process-wide SQLite connection pools before deleting their temporary database files. The three Revit Release builds and the self-contained win-x64 agent build succeeded with zero warnings and errors. The staged package and final ZIP passed `verify-package.ps1`.

## Boundaries

- `rvt-mcp` remains a separately installed product. This release does not change its `read_only` setting or tool catalog.
- User API keys are DPAPI-protected for the current Windows account. An administrator key may be compiled into a private agent and stays out of the UI and settings JSON. A user with access to the client binary or process can extract a compiled key.
- Automated tests use simulated MCP servers and do not prove that a live Revit model can load the add-in or execute its tools.
- The Revit add-in targets .NET Framework 4.8 and Revit 2022–2024; Revit 2025+ is outside this release.
