using System.Text.Json.Serialization;

namespace LanChat.Models;

/// <summary>Одно сообщение чата. Этот же класс ходит по сети внутри пакетов и лежит в БД.</summary>
public sealed class ChatMessage
{
    public long Id { get; set; }
    public string Nick { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Получатель личного сообщения; null/пусто — сообщение в общий чат.</summary>
    public string? To { get; set; }

    /// <summary>Время отправки, Unix-время в миллисекундах (UTC).</summary>
    public long Ts { get; set; }

    /// <summary>Служебное сообщение («Вася вошёл в чат»), а не реплика пользователя.</summary>
    public bool IsSystem { get; set; }

    /// <summary>Текст меняли после отправки.</summary>
    public bool IsEdited { get; set; }

    /// <summary>Сообщение удалено автором — текста больше нет, остаётся только отметка.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Личное сообщение реально дошло до приложения получателя (галочки — вторая).</summary>
    public bool IsDelivered { get; set; }

    /// <summary>Получатель открыл диалог и увидел сообщение (кружок вместо галочек).</summary>
    public bool IsRead { get; set; }

    /// <summary>
    /// Для служебных сообщений — семантический код ("joined"/"left"), по которому клиент сам
    /// собирает текст на своём языке. Сервер готовый русский текст больше не присылает.
    /// </summary>
    public string? SystemKind { get; set; }

    /// <summary>Имя приложенного файла; null — сообщение без файла.</summary>
    public string? FileName { get; set; }

    /// <summary>Размер файла в байтах.</summary>
    public long? FileSize { get; set; }

    /// <summary>Проставляется только на стороне получателя, по сети не передаётся.</summary>
    [JsonIgnore]
    public bool IsMine { get; set; }

    [JsonIgnore]
    public bool IsNormal => !IsSystem;

    [JsonIgnore]
    public bool IsPrivate => !string.IsNullOrEmpty(To);

    [JsonIgnore]
    public bool HasFile => !string.IsNullOrEmpty(FileName);

    [JsonIgnore]
    public string FileSizeText => FileSize is { } n ? LanChat.Services.Strings.FormatFileSize(n) : "";

    /// <summary>✓ отправлено · ✓✓ доставлено · ● прочитано. Показывается только для своих сообщений.</summary>
    [JsonIgnore]
    public string TickGlyph => IsRead ? "●" : IsDelivered ? "✓✓" : "✓";

    /// <summary>Собеседник в личном диалоге: тот, кто не я — отправитель либо получатель.</summary>
    public string PeerFor(string myNick) =>
        string.Equals(Nick, myNick, StringComparison.CurrentCultureIgnoreCase) ? To ?? "" : Nick;

    [JsonIgnore]
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Ts).LocalDateTime.ToString("HH:mm");

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
