using System.IO;
using LanChat.Models;
using Microsoft.Data.Sqlite;

namespace LanChat.Storage;

/// <summary>
/// История сообщений в SQLite-файле. Живёт только на стороне сервера:
/// клиенты получают свою копию истории пакетом welcome при подключении,
/// а личную переписку подтягивают отдельным запросом при открытии диалога.
/// Групповое сообщение — то, где "to" пустой; личное — то, где он заполнен.
/// Байты файлов лежат в отдельной колонке и отдаются только по явному запросу
/// (FileRequest), а не при обычной выдаче истории — чтобы не тащить мегабайты
/// туда, где нужен только список сообщений.
/// </summary>
public sealed class MessageStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Lock _gate = new();

    public string FilePath { get; }

    public MessageStore(string? path = null)
    {
        FilePath = path ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        _conn = new SqliteConnection($"Data Source={FilePath}");
        _conn.Open();

        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS messages (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                nick     TEXT    NOT NULL,
                text     TEXT    NOT NULL,
                ts       INTEGER NOT NULL,
                isSystem INTEGER NOT NULL DEFAULT 0,
                "to"     TEXT    NULL,
                fileName TEXT    NULL,
                fileSize INTEGER NULL,
                fileData BLOB    NULL,
                edited    INTEGER NOT NULL DEFAULT 0,
                deleted   INTEGER NOT NULL DEFAULT 0,
                delivered INTEGER NOT NULL DEFAULT 0,
                seen      INTEGER NOT NULL DEFAULT 0,
                systemKind TEXT   NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_ts ON messages(ts);
            """);

        // Переход со старых схем — недостающие колонки добавляем по одной.
        foreach (var ddl in new[]
        {
            """ALTER TABLE messages ADD COLUMN "to" TEXT NULL;""",
            """ALTER TABLE messages ADD COLUMN fileName TEXT NULL;""",
            """ALTER TABLE messages ADD COLUMN fileSize INTEGER NULL;""",
            """ALTER TABLE messages ADD COLUMN fileData BLOB NULL;""",
            """ALTER TABLE messages ADD COLUMN edited INTEGER NOT NULL DEFAULT 0;""",
            """ALTER TABLE messages ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0;""",
            """ALTER TABLE messages ADD COLUMN delivered INTEGER NOT NULL DEFAULT 0;""",
            """ALTER TABLE messages ADD COLUMN seen INTEGER NOT NULL DEFAULT 0;""",
            """ALTER TABLE messages ADD COLUMN systemKind TEXT NULL;""",
        })
        {
            try { Execute(ddl); }
            catch (SqliteException) { /* колонка уже есть */ }
        }
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanChat", "chat.db");

    /// <summary>Сохраняет сообщение (при желании — с приложенным файлом) и проставляет ему Id.</summary>
    public void Add(ChatMessage msg, byte[]? fileData = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (nick, text, ts, isSystem, "to", fileName, fileSize, fileData, edited, deleted, systemKind)
                VALUES ($nick, $text, $ts, $sys, $to, $fn, $fs, $fd, 0, 0, $sk);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$nick", msg.Nick);
            cmd.Parameters.AddWithValue("$text", msg.Text);
            cmd.Parameters.AddWithValue("$ts", msg.Ts);
            cmd.Parameters.AddWithValue("$sys", msg.IsSystem ? 1 : 0);
            cmd.Parameters.AddWithValue("$to", (object?)msg.To ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fn", (object?)msg.FileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fs", (object?)msg.FileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fd", (object?)fileData ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sk", (object?)msg.SystemKind ?? DBNull.Value);
            msg.Id = (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>Последние <paramref name="count"/> сообщений общего чата (без личных, без байтов файлов).</summary>
    public List<ChatMessage> GetLast(int count)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                {SelectColumns} FROM messages
                WHERE "to" IS NULL
                ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", count);
            return ReadAll(cmd);
        }
    }

    /// <summary>
    /// Последние <paramref name="count"/> личных сообщений между двумя никами
    /// (в любую сторону), в хронологическом порядке, без байтов файлов.
    /// </summary>
    public List<ChatMessage> GetPrivateLast(string nickA, string nickB, int count)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                {SelectColumns} FROM messages
                WHERE (nick = $a COLLATE NOCASE AND "to" = $b COLLATE NOCASE)
                   OR (nick = $b COLLATE NOCASE AND "to" = $a COLLATE NOCASE)
                ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$a", nickA);
            cmd.Parameters.AddWithValue("$b", nickB);
            cmd.Parameters.AddWithValue("$n", count);
            return ReadAll(cmd);
        }
    }

    /// <summary>Метаданные одного сообщения (без байтов файла) — для проверки владельца при Edit/Delete/FileRequest.</summary>
    public ChatMessage? GetById(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"{SelectColumns} FROM messages WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }
    }

    /// <summary>Байты приложенного файла — читаются только когда их реально запросили на скачивание.</summary>
    public byte[]? GetFileData(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT fileData FROM messages WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public void UpdateText(long id, string text)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET text = $text, edited = 1 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkDeleted(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET text = '', deleted = 1 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkDelivered(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET delivered = 1 WHERE id = $id AND delivered = 0;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Отмечает прочитанными сообщения от <paramref name="authorNick"/> к <paramref name="readerNick"/>
    /// с id не больше <paramref name="upToId"/>. Возвращает id реально затронутых строк —
    /// именно по ним сервер разошлёт MessageUpdated автору.
    /// </summary>
    public List<long> MarkReadUpTo(string authorNick, string readerNick, long upToId)
    {
        lock (_gate)
        {
            using var select = _conn.CreateCommand();
            select.CommandText = """
                SELECT id FROM messages
                WHERE nick = $author COLLATE NOCASE AND "to" = $reader COLLATE NOCASE
                  AND id <= $upTo AND seen = 0;
                """;
            select.Parameters.AddWithValue("$author", authorNick);
            select.Parameters.AddWithValue("$reader", readerNick);
            select.Parameters.AddWithValue("$upTo", upToId);

            var ids = new List<long>();
            using (var reader = select.ExecuteReader())
                while (reader.Read()) ids.Add(reader.GetInt64(0));

            if (ids.Count == 0) return ids;

            using var update = _conn.CreateCommand();
            update.CommandText = """
                UPDATE messages SET seen = 1, delivered = 1
                WHERE nick = $author COLLATE NOCASE AND "to" = $reader COLLATE NOCASE AND id <= $upTo;
                """;
            update.Parameters.AddWithValue("$author", authorNick);
            update.Parameters.AddWithValue("$reader", readerNick);
            update.Parameters.AddWithValue("$upTo", upToId);
            update.ExecuteNonQuery();

            return ids;
        }
    }

    /// <summary>Полностью стирает общий чат — и обычные, и служебные записи.</summary>
    public void ClearGroup() => Execute("""DELETE FROM messages WHERE "to" IS NULL;""");

    /// <summary>Стирает личную переписку между двумя никами (в обе стороны).</summary>
    public void ClearPrivate(string nickA, string nickB)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM messages
                WHERE (nick = $a COLLATE NOCASE AND "to" = $b COLLATE NOCASE)
                   OR (nick = $b COLLATE NOCASE AND "to" = $a COLLATE NOCASE);
                """;
            cmd.Parameters.AddWithValue("$a", nickA);
            cmd.Parameters.AddWithValue("$b", nickB);
            cmd.ExecuteNonQuery();
        }
    }

    public void Clear() => Execute("DELETE FROM messages;");

    private const string SelectColumns =
        """SELECT id, nick, text, ts, isSystem, "to", fileName, fileSize, edited, deleted, delivered, seen, systemKind""";

    private static List<ChatMessage> ReadAll(SqliteCommand cmd)
    {
        var list = new List<ChatMessage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadRow(reader));
        list.Reverse();
        return list;
    }

    private static ChatMessage ReadRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Nick = reader.GetString(1),
        Text = reader.GetString(2),
        Ts = reader.GetInt64(3),
        IsSystem = reader.GetInt64(4) != 0,
        To = reader.IsDBNull(5) ? null : reader.GetString(5),
        FileName = reader.IsDBNull(6) ? null : reader.GetString(6),
        FileSize = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        IsEdited = reader.GetInt64(8) != 0,
        IsDeleted = reader.GetInt64(9) != 0,
        IsDelivered = reader.GetInt64(10) != 0,
        IsRead = reader.GetInt64(11) != 0,
        SystemKind = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    private void Execute(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
