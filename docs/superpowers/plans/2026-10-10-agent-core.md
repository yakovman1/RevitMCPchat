# Revit MCP Chat Agent Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a runnable .NET 8 console chat that connects to the installed `rvt-mcp` server, can use an OpenAI-compatible model, and safely handles tool calls.

**Architecture:** A headless agent owns the MCP stdio child process and the model loop. A small shared contract project defines events for later use by the Revit panel. In this plan a console sink supplies user input and approvals; later plans add storage, providers and WPF.

**Tech Stack:** C#/.NET 8, .NET Standard 2.0 contracts, official `ModelContextProtocol` C# SDK, `HttpClient`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-10-revit-mcp-chat-design.md`.

## Global Constraints

- Revit 2022–2024 is the first target; the add-in will use .NET Framework 4.8, while this agent uses .NET 8.
- The agent uses the installed `rvt-mcp` MCP server over stdio and binds it to the current Revit PID with `--target pid:<n>`.
- The existing `rvt-mcp` configuration decides which tools are exposed; the chat does not change read-only settings.
- Only positively classified read-only tools may run automatically; mutations, unknown tools and `revit_send_code_to_revit` require approval.
- A write whose response is lost has unknown outcome and must never be retried automatically.
- Real credentials must not enter source control, logs or test fixtures.

## Review Focus

1. A dead or replaced PID must not silently target another Revit instance; Task 2 tests explicit target binding and target loss.
2. A tool without read-only annotation must require approval; Task 3 tests the unknown classification.
3. A rejected `send_code` call must not reach MCP; Task 3 tests refusal and body visibility.
4. A model response with malformed tool arguments must not reach MCP; Task 3 tests schema rejection.
5. An interrupted write must surface an unknown outcome and not retry; Task 3 tests one call only.

## File map

- `RevitMCPchat/RevitMCPchat.slnx`: retain the prepared solution and add agent, contracts and tests.
- `src/RevitMcpChat.Contracts/`: plain DTOs for turns, tool approvals, events and model identity; no Revit dependency.
- `src/RevitMcpChat.Agent/Mcp/`: official SDK transport, target binding and tool catalog.
- `src/RevitMcpChat.Agent/Chat/`: provider-neutral turn loop, tool selection and approval policy.
- `src/RevitMcpChat.Agent/Providers/`: common provider interface and first OpenAI-compatible implementation.
- `src/RevitMcpChat.Agent/Console/`: runnable console sink and command-line bootstrap.
- `tests/RevitMcpChat.Agent.Tests/`: fake MCP and HTTP contract tests.

### Task 1: Solution and shared contracts

**Files:** Create `.gitignore`, `src/RevitMcpChat.Contracts/RevitMcpChat.Contracts.csproj`, `src/RevitMcpChat.Contracts/ChatContracts.cs`, `src/RevitMcpChat.Agent/RevitMcpChat.Agent.csproj`, `src/RevitMcpChat.Agent/Program.cs`, `tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj`; modify `RevitMCPchat/RevitMCPchat.slnx`.

**Interfaces:** Produce `ChatTurnRequest` (`ConversationId`, `ModelIdentity`, `ProviderId`, `ModelId`, `UserText`), `ToolApprovalRequest` (`CallId`, `ToolName`, `ArgumentsJson`, `Summary`, `IsCode`), `ChatEvent` (`ConversationId`, `Kind`, `Text`, `CallId`) and `IChatEventSink.PublishAsync(ChatEvent, CancellationToken)` / `RequestApprovalAsync(ToolApprovalRequest, CancellationToken)`.

- [ ] **Step 1: Write a failing contract test** `ChatContractsRoundTrip` that serializes and deserializes a turn with conversation ID, model identity `file:C:\\Project\\A.rvt` and provider `openai`, then asserts each value is retained.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter ChatContractsRoundTrip`; expect a failure because the contracts do not exist.
- [ ] **Step 3: Add the contracts and minimal agent project**; use DTO types compatible with .NET Framework 4.8 and .NET 8, and ignore `.vs/`, `bin/`, `obj/`, generated keys and packages in `.gitignore`.
- [ ] **Step 4: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter ChatContractsRoundTrip`; expect PASS, then `dotnet build src/RevitMcpChat.Agent/RevitMcpChat.Agent.csproj`.
- [ ] **Step 5: Commit** only the task's solution, contract, agent shell, test and ignore files with message `feat: establish agent and chat contracts`.

### Task 2: MCP process and target binding

**Files:** Create `src/RevitMcpChat.Agent/Mcp/IMcpGateway.cs`, `McpGateway.cs`, `McpToolDescriptor.cs`, `RvtMcpLocator.cs`; test `tests/RevitMcpChat.Agent.Tests/McpGatewayTests.cs`.

**Interfaces:** `IMcpGateway.ConnectAsync(int revitPid, string serverPath, CancellationToken)`, `ListToolsAsync(CancellationToken) : Task<IReadOnlyList<McpToolDescriptor>>`, `CallToolAsync(string name, string argumentsJson, CancellationToken) : Task<McpCallOutcome>`, `DisposeAsync()`. `McpToolDescriptor` contains `Name`, `Description`, `InputSchemaJson` and nullable `ReadOnlyHint`. `McpCallOutcome` contains `Content`, `IsError`, `OutcomeUnknown`. `RvtMcpLocator.FindInstalledServer() : string?` checks `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`.

- [ ] **Step 1: Write failing tests** `LaunchPinsExactPid` (fake stdio server sees `--target pid:12345`), `NoServerReportsMissingDependency`, `LostTargetDoesNotRebind` (PID loss yields a target error, no second target chosen).
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter FullyQualifiedName~McpGatewayTests`; expect FAIL.
- [ ] **Step 3: Implement the gateway** using `StdioClientTransport` and `McpClient`, with `InheritEnvironmentVariables=false` except a documented minimal environment; keep stderr out of MCP stdout and dispose the child process on shutdown.
- [ ] **Step 4: Run** the filtered tests and `dotnet build src/RevitMcpChat.Agent/RevitMcpChat.Agent.csproj`; expect PASS and build success.
- [ ] **Step 5: Commit** gateway and tests with message `feat: connect agent to pinned Revit MCP target`.

### Task 3: Tool selection and approval loop

**Files:** Create `src/RevitMcpChat.Agent/Chat/ChatEngine.cs`, `ToolPolicy.cs`, `ToolCatalogSelector.cs`, `ChatModelTypes.cs`; test `tests/RevitMcpChat.Agent.Tests/ChatEngineTests.cs` and `ToolPolicyTests.cs`.

**Interfaces:** `IChatProvider.StreamAsync(ProviderRequest, CancellationToken) : IAsyncEnumerable<ProviderEvent>`; `ChatEngine.RunAsync(ChatTurnRequest, IChatEventSink, CancellationToken) : Task`; `ToolPolicy.RequiresApproval(McpToolDescriptor) : bool`; `ToolCatalogSelector.Select(string userText, IReadOnlyList<McpToolDescriptor>) : IReadOnlyList<McpToolDescriptor>`. An approval is tied to the exact `CallId`, name and argument JSON that will be sent.

- [ ] **Step 1: Write failing tests** `ReadOnlyToolRunsWithoutApproval`, `UnknownToolRequiresApproval`, `RejectedSendCodeNeverCallsMcp`, `MalformedArgumentsNeverCallMcp`, `InterruptedWriteIsNotRetried`, and `CatalogOnlyContainsAdvertisedTools`; assert MCP call counts of 0 or 1 and the approval request's exact arguments.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter "FullyQualifiedName~ChatEngineTests|FullyQualifiedName~ToolPolicyTests"`; expect FAIL.
- [ ] **Step 3: Implement the model/tool loop**. Validate arguments against the advertised JSON schema before calling MCP; treat absent/ambiguous annotations and `revit_batch_execute` as requiring approval; force separate confirmation for `revit_send_code_to_revit`; present every batch subcommand; use bounded tool turns and explicit cancellation.
- [ ] **Step 4: Run** the filtered tests; expect PASS. Run the entire agent test project; expect PASS.
- [ ] **Step 5: Commit** engine, policy, selector and tests with message `feat: gate Revit tool calls through approval policy`.

### Task 4: First runnable provider and console flow

**Files:** Create `src/RevitMcpChat.Agent/Providers/OpenAiCompatibleProvider.cs`, `Providers/HttpStreamingParser.cs`, `Console/ConsoleChatSink.cs`; modify `Program.cs`; test `tests/RevitMcpChat.Agent.Tests/OpenAiCompatibleProviderTests.cs` and `ConsoleFlowTests.cs`.

**Interfaces:** `OpenAiCompatibleProvider(HttpClient, Uri baseUri, string apiKey)` implements `IChatProvider`; `ConsoleChatSink` implements `IChatEventSink`. The console command is `RevitMcpChat.Agent --console --revit-pid <pid> --provider openai-compatible --model <id> --base-url <url>` and reads the key from a process environment variable only until Plan 2 adds DPAPI settings.

- [ ] **Step 1: Write failing tests** `StreamsTextAndToolCallFromSse`, `Http401ReportsProviderAuthError`, `ConsoleApprovalRejectsWithoutMcpCall`; use a fake `HttpMessageHandler` and fake MCP gateway.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter "FullyQualifiedName~OpenAiCompatibleProviderTests|FullyQualifiedName~ConsoleFlowTests"`; expect FAIL.
- [ ] **Step 3: Implement the HTTP adapter and console host** with HTTP cancellation, SSE parsing, no key in exception messages and visible read/write outcomes.
- [ ] **Step 4: Run** the filtered tests and full agent tests; expect PASS. Run `dotnet run --project src/RevitMcpChat.Agent -- --help`; expect usage text without requiring a key or Revit.
- [ ] **Step 5: Commit** provider, console flow and tests with message `feat: run headless MCP chat with OpenAI-compatible model`.

**Working result:** The console agent can run a complete chat against an installed `rvt-mcp` with a compatible model; fake MCP/HTTP tests prove the same path without Revit or credentials.
