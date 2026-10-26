using System.Text.Json;
using System.Text.Json.Serialization;
using LanChat.Models;

namespace LanChat.Net;

/// <summary>Общие настройки протокола: порты, имя приложения, формат JSON.</summary>
public static class Protocol
{
    public const string AppId = "LanChat";
    public const int Version = 1;

    /// <summary>TCP-порт, на котором сервер принимает клиентов.</summary>
    public const int TcpPort = 5000;

    /// <summary>UDP-порт, в который сервер шлёт broadcast-маяк «я тут».</summary>
    public const int DiscoveryPort = 5001;

    /// <summary>Период отправки маяка.</summary>
    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(1);

    /// <summary>Сервер считается пропавшим, если маяка не было дольше этого времени.</summary>
    public static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Сколько последних сообщений отдаём новому клиенту при входе.</summary>
    public const int HistorySize = 100;

    public const int MaxNickLength = 24;
    public const int MaxTextLength = 4000;
    public const int MaxFileNameLength = 255;

    /// <summary>
    /// Предел размера пересылаемого файла. Файл едет одним base64-пакетом поверх
    /// NDJSON (без чанкования), поэтому ограничение держим скромным — для фото
    /// и документов в локальной сети этого с запасом хватает.
    /// </summary>
    public const long MaxFileBytes = 25 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class PacketType
{
    /// <summary>Клиент → сервер, первый пакет: представляется ником.</summary>
    public const string Hello = "hello";

    /// <summary>Сервер → клиенту: подтверждение входа, история и список участников.</summary>
    public const string Welcome = "welcome";

    /// <summary>Клиент → сервер (текст) и сервер → всем (готовое сообщение).</summary>
    public const string Message = "msg";

    /// <summary>Сервер → всем: обновлённый список ников.</summary>
    public const string Users = "users";

    /// <summary>Сервер → всем: служебная строка (вход/выход).</summary>
    public const string System = "system";

    /// <summary>Сервер → клиенту: вход отклонён, следом разрыв.</summary>
    public const string Error = "error";

    /// <summary>Сервер → клиенту: необязательное к действию уведомление (например, «собеседник не в сети»). Связь не рвёт.</summary>
    public const string Info = "info";

    /// <summary>Клиент → сервер: «пришли мне историю переписки с этим ником».</summary>
    public const string PrivateHistoryRequest = "pm_history_req";

    /// <summary>Сервер → клиенту: история личной переписки с указанным ником.</summary>
    public const string PrivateHistory = "pm_history";

    /// <summary>Клиент → сервер: изменить текст своего ранее отправленного сообщения.</summary>
    public const string Edit = "edit";

    /// <summary>Сервер → участникам диалога: сообщение отредактировано (пришло обновлённым целиком).</summary>
    public const string MessageUpdated = "msg_updated";

    /// <summary>Клиент → сервер: удалить своё ранее отправленное сообщение.</summary>
    public const string Delete = "delete";

    /// <summary>Сервер → участникам диалога: сообщение удалено.</summary>
    public const string MessageDeleted = "msg_deleted";

    /// <summary>Клиент → сервер: очистить диалог целиком (Nick/To пустой — общий чат, иначе — личный с этим ником).</summary>
    public const string ClearConversation = "clear";

    /// <summary>Сервер → участникам: диалог очищен (To — какой; пусто значит общий чат).</summary>
    public const string ConversationCleared = "cleared";

    /// <summary>Клиент → сервер: отправка файла (сами байты, одним пакетом, см. Protocol.MaxFileBytes).</summary>
    public const string File = "file";

    /// <summary>Клиент → сервер: «пришли байты файла из этого сообщения» — файл качается по требованию, не при каждой рассылке.</summary>
    public const string FileRequest = "file_req";

    /// <summary>Сервер → клиенту: байты запрошенного файла.</summary>
    public const string FileData = "file_data";

    /// <summary>Клиент → сервер: подтверждение, что личное сообщение реально дошло до приложения получателя.</summary>
    public const string Delivered = "delivered";

    /// <summary>
    /// Клиент → сервер: «я прочитал переписку с этим собеседником (Nick) по это сообщение
    /// включительно (MessageId)». Ответ приходит исходному отправителю как MessageUpdated
    /// на каждое затронутое сообщение — отдельного типа пакета для этого не нужно.
    /// </summary>
    public const string MarkRead = "mark_read";

    /// <summary>Клиент → сервер: пользователь печатает (To пустой — в общий чат, иначе кому).</summary>
    public const string Typing = "typing";

    /// <summary>
    /// Сервер → адресатам: кто-то печатает. To="" — печатает в общий чат (рассылка всем,
    /// кроме автора); To=ник автора — печатает лично тебе (ключ диалога у получателя — это
    /// и есть ник автора, Nick и To совпадают для личного случая).
    /// </summary>
    public const string UserTyping = "user_typing";
}

/// <summary>
/// Единый конверт для всех пакетов — различаются по полю <see cref="Type"/>.
/// По TCP идут строками JSON, разделёнными переводом строки (NDJSON).
/// </summary>
public sealed class Packet
{
    public string Type { get; set; } = "";
    public string? Nick { get; set; }
    public string? Text { get; set; }
    public ChatMessage? Message { get; set; }
    public List<string>? Users { get; set; }
    public List<ChatMessage>? History { get; set; }
    public string? Room { get; set; }

    /// <summary>
    /// Получатель личного сообщения. Пусто/отсутствует — это сообщение в общий чат.
    /// Для <see cref="PacketType.PrivateHistoryRequest"/> в поле <see cref="Nick"/>
    /// передаётся ник собеседника, чью историю запрашивают.
    /// </summary>
    public string? To { get; set; }

    /// <summary>Id сообщения — для Edit/Delete/FileRequest и в ответах MessageUpdated/MessageDeleted/FileData.</summary>
    public long? MessageId { get; set; }

    /// <summary>Имя файла — в File (отправка) и FileData (ответ на скачивание).</summary>
    public string? FileName { get; set; }

    /// <summary>Размер файла в байтах.</summary>
    public long? FileSize { get; set; }

    /// <summary>Содержимое файла, base64 — только в File и FileData, никогда в обычной рассылке сообщений.</summary>
    public string? Data { get; set; }

    /// <summary>
    /// Смысловой код для <see cref="PacketType.Info"/> (сейчас только "offline") — текст
    /// по нему собирает сам клиент на своём языке, сервер готовый текст не присылает.
    /// </summary>
    public string? Kind { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, Protocol.Json);

    public static Packet? FromJson(string line)
    {
        try { return JsonSerializer.Deserialize<Packet>(line, Protocol.Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Содержимое UDP-маяка сервера.</summary>
public sealed class Beacon
{
    public string App { get; set; } = Protocol.AppId;
    public int Ver { get; set; } = Protocol.Version;
    public int Port { get; set; } = Protocol.TcpPort;
    public string Room { get; set; } = "";
    public int Users { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, Protocol.Json);

    public static Beacon? FromJson(string json)
    {
        try
        {
            var b = JsonSerializer.Deserialize<Beacon>(json, Protocol.Json);
            return b is { App: Protocol.AppId, Ver: Protocol.Version } ? b : null;
        }
        catch (JsonException) { return null; }
    }
}
