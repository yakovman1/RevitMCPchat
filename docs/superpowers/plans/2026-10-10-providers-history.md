# Revit MCP Chat Providers and History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the working console agent with every agreed provider, per-user key priority and persistent model-scoped chats.

**Architecture:** Provider adapters share the `IChatProvider` and normalized event types from the agent-core plan. A credential resolver chooses DPAPI-protected user keys before optional build-supplied keys. SQLite stores conversations and tool outcomes independently of provider protocols.

**Tech Stack:** C#/.NET 8, `HttpClient`, Windows DPAPI, `Microsoft.Data.Sqlite`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-10-revit-mcp-chat-design.md`. Execute after `docs/superpowers/plans/2026-10-10-agent-core.md`.

## Global Constraints

- Support OpenAI, Anthropic, Gemini, DeepSeek, Alice AI LLM via Yandex AI Studio, GigaChat and arbitrary OpenAI-compatible endpoints.
- A user key for the active provider wins over a build-supplied key; invalid user credentials do not fall back silently.
- User keys are encrypted with DPAPI `CurrentUser`; build-supplied keys are absent from public source and configuration.
- Do not log keys or full `send_code` bodies; GigaChat access tokens stay in memory.
- Chat history is local and model-scoped, with a separate all-chats view; wrong-model chats cannot call MCP.
- Preserve the agent-core tool approval and target-binding rules.

## Review Focus

1. A wrong user key must never spend an administrator key; Task 1 tests no fallback after HTTP 401.
2. A corrupted DPAPI blob must report recoverable settings error, not crash startup; Task 1 tests it.
3. GigaChat must refresh an expired access token once without exposing the authorization key; Task 3 tests it.
4. A copied model must not silently inherit write-capable chat context; Task 2 tests Save As identity.
5. A stored `send_code` event must omit the raw C# body; Task 2 tests persisted bytes.

## File map

- `src/RevitMcpChat.Agent/Settings/`: provider profiles, user settings, DPAPI secret store, admin key source and credential resolver.
- `src/RevitMcpChat.Agent/History/`: SQLite schema, migrations, chat records and model identity association.
- `src/RevitMcpChat.Agent/Providers/`: protocol adapters and provider-specific auth/token logic.
- `tests/RevitMcpChat.Agent.Tests/`: credential, history, fake-HTTP and end-to-end console tests.

### Task 1: Settings and credential resolution

**Files:** Create `Settings/ISecretStore.cs`, `DpapiSecretStore.cs`, `IAdminKeySource.cs`, `CredentialResolver.cs`, `ProviderProfile.cs`, `SettingsRepository.cs`; tests `CredentialResolverTests.cs`, `DpapiSecretStoreTests.cs`.

**Interfaces:** `ISecretStore.SaveAsync(string providerId, string value, CancellationToken)`, `LoadAsync(string providerId, CancellationToken) : Task<string?>`, `DeleteAsync(string providerId, CancellationToken)`; `IAdminKeySource.GetKey(string providerId) : string?`; `CredentialResolver.ResolveAsync(string providerId, CancellationToken) : Task<ResolvedCredential>` where `ResolvedCredential.Source` is `User`, `Admin` or `Missing`. Settings store the selected provider/model and non-secret endpoint fields.

- [ ] **Step 1: Write failing tests** `UserKeyOverridesAdmin`, `DeletingUserKeyRestoresAdmin`, `InvalidUserKeyDoesNotFallbackAfter401`, `CorruptProtectedKeyShowsSettingsError`, `SavedKeyIsNotPlaintext`; assert the selected source and that persisted bytes lack the test secret.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter "FullyQualifiedName~CredentialResolverTests|FullyQualifiedName~DpapiSecretStoreTests"`; expect FAIL.
- [ ] **Step 3: Implement settings and DPAPI** under `%LOCALAPPDATA%\RevitMcpChat`; use a default empty `IAdminKeySource` and leave build-time injection to the packaging plan. Never include credential values in `ToString`, events or exceptions.
- [ ] **Step 4: Run** the filtered and full agent tests; expect PASS.
- [ ] **Step 5: Commit** settings and tests with message `feat: protect user keys and resolve credential priority`.

### Task 2: Durable model-scoped history

**Files:** Create `History/IChatHistory.cs`, `SqliteChatHistory.cs`, `HistorySchema.cs`, `ModelIdentity.cs`; tests `ChatHistoryTests.cs`, `ModelIdentityTests.cs`.

**Interfaces:** `IChatHistory.CreateAsync(string modelIdentity, string providerId, string modelId, CancellationToken) : Task<Guid>`, `AppendAsync(Guid chatId, ChatEvent chatEvent, CancellationToken)`, `ListAsync(string? modelIdentity, CancellationToken) : Task<IReadOnlyList<ChatSummary>>`, `LoadAsync(Guid chatId, CancellationToken) : Task<ChatTranscript>`, `RenameAsync`, `DeleteAsync`, `BindUnsavedAsync`. `ModelIdentity.ForSaved(string normalizedLocation)` and `ForUnsaved(Guid sessionId)` return stable strings; Save As creates a new saved identity.

- [ ] **Step 1: Write failing tests** `HistorySurvivesProcessRestart`, `CurrentModelFilterExcludesOtherFiles`, `AllChatsIncludesBothModels`, `UnsavedFirstSaveRebinds`, `SaveAsDoesNotReuseOriginalIdentity`, `SendCodeBodyIsAbsentFromDatabase`, `HistoryRedactsKnownCredential`, `WrongModelChatCannotIssueToolCall`.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter "FullyQualifiedName~ChatHistoryTests|FullyQualifiedName~ModelIdentityTests"`; expect FAIL.
- [ ] **Step 3: Implement schema-versioned SQLite storage** with parameterized statements, transactions for event append, explicit redaction of secrets and raw C# bodies, and a model-identity guard before entering `ChatEngine.RunAsync`.
- [ ] **Step 4: Run** the filtered tests, then full agent tests; expect PASS.
- [ ] **Step 5: Commit** storage and tests with message `feat: persist chats by Revit model identity`.

### Task 3: Provider protocol adapters

**Files:** Create `Providers/ProviderFactory.cs`, `DeepSeekProvider.cs`, `YandexAiStudioProvider.cs`, `AnthropicProvider.cs`, `GeminiProvider.cs`, `GigaChatProvider.cs`, `GigaChatTokenClient.cs`; tests `ProviderProtocolTests.cs`, `GigaChatTokenTests.cs`.

**Interfaces:** `ProviderFactory.Create(ProviderProfile, ResolvedCredential, HttpClient) : IChatProvider`. DeepSeek uses the OpenAI-compatible implementation with its own endpoint. Yandex uses its OpenAI-compatible API with `Authorization: Api-Key` and folder/model URI. Anthropic, Gemini and GigaChat translate common text/tool events to their native wire formats; GigaChat obtains and refreshes an OAuth access token using its authorization key and scope.

- [ ] **Step 1: Write failing fake-HTTP tests** `DeepSeekUsesConfiguredBaseUrl`, `YandexUsesApiKeyHeaderAndFolderModel`, `AnthropicToolUseRoundTrips`, `GeminiFunctionCallRoundTrips`, `GigaChatTokenRefreshesAfterExpiry`, `UnsupportedToolModelReportsCapability`, `ProviderErrorDoesNotEchoSecret`.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter "FullyQualifiedName~ProviderProtocolTests|FullyQualifiedName~GigaChatTokenTests"`; expect FAIL.
- [ ] **Step 3: Implement adapters** with streaming text/tool normalization, provider-specific validation and certificate verification enabled. Use official protocol docs; no generic fallback that silently drops tool calls.
- [ ] **Step 4: Run** provider tests and full agent tests; expect PASS.
- [ ] **Step 5: Commit** adapters and tests with message `feat: add requested AI providers`.

### Task 4: Persisted console workflow

**Files:** Modify `Program.cs`, `Console/ConsoleChatSink.cs`, `Chat/ChatEngine.cs`; test `PersistedConsoleFlowTests.cs`; create `docs/development.md` with local console commands.

**Interfaces:** Console commands `providers`, `settings set-key <provider>`, `settings remove-key <provider>`, `chat --revit-pid <pid>`, `history [--all]`, `open <chat-id>` call the same services that the future panel will use. Key input is read without echo. `ChatEngine` appends a transcript event through `IChatHistory` after publication.

- [ ] **Step 1: Write failing integration tests** `UserCanResumeChatAfterAgentRestart`, `SwitchingProviderRetainsOriginalChatMetadata`, `RemovingKeyRevealsBuildAccessStatus`, `OfflineHistoryStillOpens`; use temporary profile directory and fake MCP/provider.
- [ ] **Step 2: Run** `dotnet test tests/RevitMcpChat.Agent.Tests/RevitMcpChat.Agent.Tests.csproj --filter FullyQualifiedName~PersistedConsoleFlowTests`; expect FAIL.
- [ ] **Step 3: Wire settings, credentials and history** into console and engine; document the local commands and the build-key extraction limitation.
- [ ] **Step 4: Run** all agent tests and `dotnet run --project src/RevitMcpChat.Agent -- --help`; expect PASS and documented commands.
- [ ] **Step 5: Commit** wiring and documentation with message `feat: resume provider-backed chats from local history`.

**Working result:** The console agent supports every requested provider, protected user keys, optional build-provided keys through an interface, and persistent model-scoped conversations.
