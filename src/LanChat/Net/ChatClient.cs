using System.IO;
using System.Net.Sockets;
using System.Text;
using LanChat.Models;
using LanChat.Services;

namespace LanChat.Net;

/// <summary>
/// Подключение к серверу чата. Этим классом пользуются оба режима: и гость,
/// и хост (последний соединяется со своим же сервером по 127.0.0.1).
/// Все события приходят из фонового потока — UI обязан делать Dispatcher.
/// </summary>
public sealed class ChatClient : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>Ник, под которым нас зарегистрировал сервер (мог измениться из-за дубля).</summary>
    public string Nick { get; private set; } = "";
    public string Room { get; private set; } = "";
    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>Сервер принял нас: пришли финальный ник, список участников и история.</summary>
    public event Action<string, string, IReadOnlyList<string>, IReadOnlyList<ChatMessage>>? Connected;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<IReadOnlyList<string>>? UsersChanged;

    /// <summary>Пришла история личной переписки с указанным ником (ответ на RequestPrivateHistoryAsync).</summary>
    public event Action<string, IReadOnlyList<ChatMessage>>? PrivateHistoryReceived;

    /// <summary>Необязательное уведомление от сервера: (ник, к кому оно относится; текст). Связь не рвёт.</summary>
    public event Action<string, string>? InfoReceived;

    /// <summary>Сообщение отредактировано — пришло целиком с новым текстом.</summary>
    public event Action<ChatMessage>? MessageUpdated;

    /// <summary>Сообщение удалено автором.</summary>
    public event Action<ChatMessage>? MessageDeleted;

    /// <summary>Диалог очищен целиком: "" — общий чат, иначе ник собеседника.</summary>
    public event Action<string>? ConversationCleared;

    /// <summary>Пришли байты файла — ответ на RequestFileAsync.</summary>
    public event Action<long, string, byte[]>? FileDataReceived;

    /// <summary>Кто-то печатает: (ник автора, ключ диалога — "" для общего чата, иначе = нику автора).</summary>
    public event Action<string, string>? TypingReceived;

    /// <summary>Соединение закрыто: текст причины либо null, если вышли сами.</summary>
    public event Action<string?>? Disconnected;

    public async Task<bool> ConnectAsync(string host, int port, string nick, TimeSpan timeout)
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true };

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            connectCts.CancelAfter(timeout);
            await _tcp.ConnectAsync(host, port, connectCts.Token);

            var stream = _tcp.GetStream();
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(stream, new UTF8Encoding(false));

            await SendAsync(new Packet { Type = PacketType.Hello, Nick = nick });
            _ = Task.Run(() => ReadLoopAsync(reader, _cts.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            Cleanup();
            Disconnected?.Invoke(Strings.Get("err_connect_timeout", host, port));
            return false;
        }
        catch (SocketException ex)
        {
            Cleanup();
            Disconnected?.Invoke(Strings.Get("err_connect_failed", host, port, ex.Message));
            return false;
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        string? reason = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    reason = Strings.Get("err_connection_lost");
                    break;
                }

                var packet = Packet.FromJson(line);
                if (packet is null) continue;

                switch (packet.Type)
                {
                    case PacketType.Welcome:
                        Nick = packet.Nick ?? "";
                        Room = packet.Room ?? "";
                        foreach (var m in packet.History ?? []) { m.IsMine = m.Nick == Nick; LocalizeSystem(m); }
                        Connected?.Invoke(Nick, Room, packet.Users ?? [], packet.History ?? []);
                        break;

                    case PacketType.Message when packet.Message is not null:
                        packet.Message.IsMine = packet.Message.Nick == Nick;
                        LocalizeSystem(packet.Message);
                        MessageReceived?.Invoke(packet.Message);
                        if (packet.Message.IsPrivate && !packet.Message.IsMine)
                            _ = SendAsync(new Packet { Type = PacketType.Delivered, MessageId = packet.Message.Id });
                        break;

                    case PacketType.Users:
                        UsersChanged?.Invoke(packet.Users ?? []);
                        break;

                    case PacketType.PrivateHistory:
                        foreach (var m in packet.History ?? []) { m.IsMine = m.Nick == Nick; LocalizeSystem(m); }
                        PrivateHistoryReceived?.Invoke(packet.Nick ?? "", packet.History ?? []);
                        break;

                    case PacketType.Info:
                        InfoReceived?.Invoke(packet.Nick ?? "", FormatInfo(packet));
                        break;

                    case PacketType.UserTyping:
                        TypingReceived?.Invoke(packet.Nick ?? "", packet.To ?? "");
                        break;

                    case PacketType.MessageUpdated when packet.Message is not null:
                        packet.Message.IsMine = packet.Message.Nick == Nick;
                        MessageUpdated?.Invoke(packet.Message);
                        break;

                    case PacketType.MessageDeleted when packet.Message is not null:
                        packet.Message.IsMine = packet.Message.Nick == Nick;
                        MessageDeleted?.Invoke(packet.Message);
                        break;

                    case PacketType.ConversationCleared:
                        ConversationCleared?.Invoke(packet.To ?? "");
                        break;

                    case PacketType.FileData when packet.MessageId is { } fid && packet.FileName is not null && packet.Data is not null:
                        FileDataReceived?.Invoke(fid, packet.FileName, Convert.FromBase64String(packet.Data));
                        break;

                    case PacketType.Error:
                        reason = packet.Text ?? Strings.Get("err_rejected");
                        break;
                }

                if (reason is not null) break;
            }
        }
        catch (OperationCanceledException) { /* вышли сами */ }
        catch (IOException) { reason = Strings.Get("err_connection_lost"); }
        catch (ObjectDisposedException) { }
        finally
        {
            reader.Dispose();
            if (!_cts.IsCancellationRequested)
            {
                Cleanup();
                Disconnected?.Invoke(reason ?? Strings.Get("err_connection_lost"));
            }
        }
    }

    public Task SendMessageAsync(string text) =>
        SendAsync(new Packet { Type = PacketType.Message, Text = text });

    public Task SendPrivateMessageAsync(string toNick, string text) =>
        SendAsync(new Packet { Type = PacketType.Message, To = toNick, Text = text });

    public Task RequestPrivateHistoryAsync(string peerNick) =>
        SendAsync(new Packet { Type = PacketType.PrivateHistoryRequest, Nick = peerNick });

    public Task EditMessageAsync(long messageId, string newText) =>
        SendAsync(new Packet { Type = PacketType.Edit, MessageId = messageId, Text = newText });

    public Task DeleteMessageAsync(long messageId) =>
        SendAsync(new Packet { Type = PacketType.Delete, MessageId = messageId });

    /// <summary>Очищает диалог целиком: peer=null/"" — общий чат, иначе личную переписку с этим ником.</summary>
    public Task ClearConversationAsync(string? peer) =>
        SendAsync(new Packet { Type = PacketType.ClearConversation, To = peer ?? "" });

    public Task RequestFileAsync(long messageId) =>
        SendAsync(new Packet { Type = PacketType.FileRequest, MessageId = messageId });

    /// <summary>«Я прочитал переписку с этим автором по это сообщение включительно».</summary>
    public Task MarkReadAsync(string authorNick, long upToMessageId) =>
        SendAsync(new Packet { Type = PacketType.MarkRead, Nick = authorNick, MessageId = upToMessageId });

    /// <summary>peer=null — печатает в общий чат, иначе лично этому собеседнику.</summary>
    public Task SendTypingAsync(string? peer) =>
        SendAsync(new Packet { Type = PacketType.Typing, To = peer });

    /// <summary>Читает файл с диска и отправляет его целиком. peer=null — в общий чат.</summary>
    public async Task<(bool Ok, string? Error)> SendFileAsync(string? peer, string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) return (false, Strings.Get("err_file_not_found"));
        if (info.Length > Protocol.MaxFileBytes)
            return (false, Strings.Get("err_file_too_big", Protocol.MaxFileBytes / 1024 / 1024));

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(filePath); }
        catch (IOException ex) { return (false, Strings.Get("err_file_read", ex.Message)); }
        catch (UnauthorizedAccessException ex) { return (false, Strings.Get("err_file_read", ex.Message)); }

        await SendAsync(new Packet
        {
            Type = PacketType.File,
            To = peer,
            FileName = info.Name,
            FileSize = bytes.LongLength,
            Data = Convert.ToBase64String(bytes),
        });
        return (true, null);
    }

    /// <summary>Для служебных сообщений сервер шлёт код события, а не готовый текст — собираем его тут на нужном языке.</summary>
    private static void LocalizeSystem(ChatMessage m)
    {
        if (m.SystemKind is { } kind) m.Text = Strings.FormatSystem(kind, m.Nick);
    }

    private static string FormatInfo(Packet packet) => packet.Kind switch
    {
        "offline" => Strings.Get("info_offline", packet.Nick ?? ""),
        "file_too_big" => Strings.Get("err_file_too_big", Protocol.MaxFileBytes / 1024 / 1024),
        _ => packet.Text ?? "",
    };

    private async Task SendAsync(Packet packet)
    {
        var writer = _writer;
        if (writer is null) return;

        await _writeLock.WaitAsync();
        try { await writer.WriteLineAsync(packet.ToJson()); }
        catch (IOException) { /* разрыв обнаружит цикл чтения */ }
        catch (ObjectDisposedException) { }
        finally { _writeLock.Release(); }
    }

    private void Cleanup()
    {
        try { _tcp?.Close(); } catch { /* ignore */ }
        _tcp = null;
        _writer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        Cleanup();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
