using System.Globalization;
using Microsoft.Data.Sqlite;
using RevitMcpChat.Contracts;

namespace RevitMcpChat.Agent.History;

public sealed class SqliteChatHistory : IChatHistory, IAsyncDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SecretRedactor _redactor;

    public SqliteChatHistory(string? localApplicationDataPath = null, SecretRedactor? redactor = null)
    {
        var root = localApplicationDataPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The Windows local application data directory is unavailable.");
        }

        var directory = Path.Combine(root, "RevitMcpChat");
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "history.sqlite3");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString();
        _redactor = redactor ?? new SecretRedactor();
        InitializeSchema();
    }

    public string DatabasePath { get; }

    public Task<Guid> CreateAsync(
        string modelIdentity,
        string providerId,
        string modelId,
        CancellationToken cancellationToken)
    {
        RequireText(modelIdentity, nameof(modelIdentity));
        RequireText(providerId, nameof(providerId));
        RequireText(modelId, nameof(modelId));
        cancellationToken.ThrowIfCancellationRequested();

        var id = Guid.NewGuid();
        var now = UtcNow();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO chats (id, model_identity, provider_id, model_id, title, created_utc, updated_utc)
            VALUES ($id, $modelIdentity, $providerId, $modelId, $title, $now, $now);";

        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$modelIdentity", modelIdentity);
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue("$modelId", modelId);
        command.Parameters.AddWithValue("$title", "New chat");
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        transaction.Commit();

        return Task.FromResult(id);
    }

    public Task AppendAsync(Guid chatId, ChatEvent chatEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);
        if (chatId == Guid.Empty)
        {
            throw new ArgumentException("A chat id is required.", nameof(chatId));
        }

        RequireText(chatEvent.Kind, nameof(chatEvent.Kind));
        cancellationToken.ThrowIfCancellationRequested();

        var toolName = chatEvent.ToolName ?? string.Empty;
        var text = string.Equals(toolName, "revit_send_code_to_revit", StringComparison.OrdinalIgnoreCase)
            ? "[C# source omitted]"
            : TranscriptTextRedactor.Redact(_redactor.Redact(chatEvent.Text ?? string.Empty));
        var now = UtcNow();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM chats WHERE id = $id;";
            exists.Parameters.AddWithValue("$id", chatId.ToString("D"));
            if (exists.ExecuteScalar() is null)
            {
                throw new KeyNotFoundException($"Chat '{chatId}' was not found.");
            }
        }

        long sequence;
        using (var sequenceCommand = connection.CreateCommand())
        {
            sequenceCommand.Transaction = transaction;
            sequenceCommand.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM events WHERE chat_id = $id;";
            sequenceCommand.Parameters.AddWithValue("$id", chatId.ToString("D"));
            sequence = Convert.ToInt64(sequenceCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO events (chat_id, sequence, kind, tool_name, call_id, text, created_utc)
                VALUES ($chatId, $sequence, $kind, $toolName, $callId, $text, $now);";

            insert.Parameters.AddWithValue("$chatId", chatId.ToString("D"));
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$kind", chatEvent.Kind);
            insert.Parameters.AddWithValue("$toolName", toolName);
            insert.Parameters.AddWithValue("$callId", chatEvent.CallId ?? string.Empty);
            insert.Parameters.AddWithValue("$text", text);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE chats SET updated_utc = $now WHERE id = $id;";
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", chatId.ToString("D"));
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatSummary>> ListAsync(
        string? modelIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = modelIdentity is null
            ? "SELECT id, title, model_identity, provider_id, model_id, created_utc, updated_utc FROM chats ORDER BY updated_utc DESC, id;"
            : "SELECT id, title, model_identity, provider_id, model_id, created_utc, updated_utc FROM chats WHERE model_identity = $modelIdentity ORDER BY updated_utc DESC, id;";
        if (modelIdentity is not null)
        {
            command.Parameters.AddWithValue("$modelIdentity", modelIdentity);
        }

        var result = new List<ChatSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadSummary(reader));
        }

        return Task.FromResult<IReadOnlyList<ChatSummary>>(result);
    }

    public Task<ChatTranscript> LoadAsync(Guid chatId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        ChatSummary summary;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, title, model_identity, provider_id, model_id, created_utc, updated_utc FROM chats WHERE id = $id;";
            command.Parameters.AddWithValue("$id", chatId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"Chat '{chatId}' was not found.");
            }

            summary = ReadSummary(reader);
        }

        var events = new List<ChatEvent>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT kind, tool_name, call_id, text FROM events WHERE chat_id = $id ORDER BY sequence;";
            command.Parameters.AddWithValue("$id", chatId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new ChatEvent
                {
                    ConversationId = chatId,
                    Kind = reader.GetString(0),
                    ToolName = reader.GetString(1),
                    CallId = reader.GetString(2),
                    Text = reader.GetString(3)
                });
            }
        }

        return Task.FromResult(new ChatTranscript
        {
            ChatId = summary.ChatId,
            Title = summary.Title,
            ModelIdentity = summary.ModelIdentity,
            ProviderId = summary.ProviderId,
            ModelId = summary.ModelId,
            Events = events
        });
    }

    public Task RenameAsync(Guid chatId, string title, CancellationToken cancellationToken)
    {
        RequireText(title, nameof(title));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chats SET title = $title, updated_utc = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$now", UtcNow());
        command.Parameters.AddWithValue("$id", chatId.ToString("D"));
        if (command.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"Chat '{chatId}' was not found.");
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid chatId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chats WHERE id = $id;";
        command.Parameters.AddWithValue("$id", chatId.ToString("D"));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task BindUnsavedAsync(
        Guid chatId,
        string savedModelIdentity,
        CancellationToken cancellationToken)
    {
        RequireText(savedModelIdentity, nameof(savedModelIdentity));
        if (!savedModelIdentity.StartsWith("file:", StringComparison.Ordinal))
        {
            throw new ArgumentException("The destination must be a saved model identity.", nameof(savedModelIdentity));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE chats SET model_identity = $savedIdentity, updated_utc = $now
            WHERE id = $id AND model_identity LIKE 'unsaved:%';";

        command.Parameters.AddWithValue("$savedIdentity", savedModelIdentity);
        command.Parameters.AddWithValue("$now", UtcNow());
        command.Parameters.AddWithValue("$id", chatId.ToString("D"));
        if (command.ExecuteNonQuery() == 1)
        {
            return Task.CompletedTask;
        }

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM chats WHERE id = $id;";
        check.Parameters.AddWithValue("$id", chatId.ToString("D"));
        if (check.ExecuteScalar() is null)
        {
            throw new KeyNotFoundException($"Chat '{chatId}' was not found.");
        }

        throw new InvalidOperationException("Only chats for an unsaved model can be rebound.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void InitializeSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"History database version {version} is newer than this application supports.");
        }

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS chats (
                id TEXT PRIMARY KEY NOT NULL,
                model_identity TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                title TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_chats_model_updated ON chats(model_identity, updated_utc DESC);
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                call_id TEXT NOT NULL,
                text TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(chat_id, sequence)
            );
            PRAGMA user_version = 1;";

        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static ChatSummary ReadSummary(SqliteDataReader reader) => new()
    {
        ChatId = Guid.Parse(reader.GetString(0)),
        Title = reader.GetString(1),
        ModelIdentity = reader.GetString(2),
        ProviderId = reader.GetString(3),
        ModelId = reader.GetString(4),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)
    };

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}
