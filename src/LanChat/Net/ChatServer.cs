using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanChat.Models;
using LanChat.Storage;

namespace LanChat.Net;

/// <summary>
/// Узел-«хозяин» чата: принимает TCP-подключения, раздаёт сообщения всем
/// участникам, хранит историю в SQLite и светит UDP-маяком для автообнаружения.
/// Своего интерфейса не имеет — хост подключается к нему обычным клиентом
/// по 127.0.0.1, поэтому UI одинаков в обоих режимах.
/// </summary>
public sealed class ChatServer : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly MessageStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly DiscoveryBeacon _beacon;
    private bool _disposed;

    public string Room { get; }
    public int Port { get; }

    /// <summary>Сервер слёг — показываем причину в UI хоста.</summary>
    public event Action<string>? Failed;

    public ChatServer(string room, int port = Protocol.TcpPort, MessageStore? store = null)
    {
        Room = room;
        Port = port;
        _store = store ?? new MessageStore();
        _listener = new TcpListener(IPAddress.Any, port);
        _beacon = new DiscoveryBeacon(() => new Beacon
        {
            Port = Port,
            Room = Room,
            Users = _sessions.Count(s => s.Value.Nick is not null),
        });
    }

    public void Start()
    {
        _listener.Start();
        _beacon.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                Failed?.Invoke($"Сервер перестал принимать подключения: {ex.Message}");
                return;
            }

            var session = new Session(tcp);
            _sessions[session.Id] = session;
            _ = Task.Run(() => ServeAsync(session, _cts.Token));
        }
    }

    private async Task ServeAsync(Session session, CancellationToken ct)
    {
        try
        {
            session.Tcp.NoDelay = true;
            using var stream = session.Tcp.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            session.Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;               // клиент закрыл соединение

                var packet = Packet.FromJson(line);
                if (packet is null) continue;          // мусор игнорируем

                if (session.Nick is null)
                {
                    if (packet.Type != PacketType.Hello) break;
                    await AcceptHelloAsync(session, packet);
                }
                else if (packet.Type == PacketType.Message)
                {
                    await HandleMessageAsync(session, packet);
                }
                else if (packet.Type == PacketType.PrivateHistoryRequest)
                {
                    await HandlePrivateHistoryRequestAsync(session, packet);
                }
                else if (packet.Type == PacketType.Edit)
                {
                    await HandleEditAsync(session, packet);
                }
                else if (packet.Type == PacketType.Delete)
                {
                    await HandleDeleteAsync(session, packet);
                }
                else if (packet.Type == PacketType.ClearConversation)
                {
                    await HandleClearAsync(session, packet);
                }
                else if (packet.Type == PacketType.File)
                {
                    await HandleFileAsync(session, packet);
                }
                else if (packet.Type == PacketType.FileRequest)
                {
                    await HandleFileRequestAsync(session, packet);
                }
                else if (packet.Type == PacketType.Delivered)
                {
                    await HandleDeliveredAsync(session, packet);
                }
                else if (packet.Type == PacketType.MarkRead)
                {
                    await HandleMarkReadAsync(session, packet);
                }
                else if (packet.Type == PacketType.Typing)
                {
                    await HandleTypingAsync(session, packet);
                }
            }
        }
        catch (OperationCanceledException) { /* остановка сервера */ }
        catch (IOException) { /* обрыв связи — обычный сценарий */ }
        catch (ObjectDisposedException) { }
        finally
        {
            await DropAsync(session);
        }
    }

    /// <summary>Первый пакет клиента: регистрируем ник, отдаём историю и список участников.</summary>
    private async Task AcceptHelloAsync(Session session, Packet packet)
    {
        var requested = Sanitize(packet.Nick, Protocol.MaxNickLength);
        if (requested.Length == 0) requested = "Гость";

        session.Nick = MakeUniqueNick(requested);

        await session.SendAsync(new Packet
        {
            Type = PacketType.Welcome,
            Nick = session.Nick,
            Room = Room,
            Users = CurrentUsers(),
            History = _store.GetLast(Protocol.HistorySize),
        });

        await BroadcastSystemAsync("joined", session.Nick!);
        await BroadcastUsersAsync();
    }

    private async Task HandleMessageAsync(Session session, Packet packet)
    {
        var text = Sanitize(packet.Text, Protocol.MaxTextLength);
        if (text.Length == 0) return;

        var to = Sanitize(packet.To, Protocol.MaxNickLength);
        if (to.Length == 0)
        {
            var msg = new ChatMessage { Nick = session.Nick!, Text = text, Ts = ChatMessage.Now() };
            _store.Add(msg);
            await BroadcastAsync(new Packet { Type = PacketType.Message, Message = msg });
            return;
        }

        await HandlePrivateMessageAsync(session, to, text);
    }

    /// <summary>
    /// Личное сообщение: сохраняется всегда, доставляется получателю, если он сейчас
    /// на связи, и эхом возвращается отправителю — тот же путь, каким придёт история.
    /// </summary>
    private async Task HandlePrivateMessageAsync(Session session, string to, string text)
    {
        var target = FindSessionByNick(to);
        var pm = new ChatMessage { Nick = session.Nick!, To = target?.Nick ?? to, Text = text, Ts = ChatMessage.Now() };
        _store.Add(pm);

        var packet = new Packet { Type = PacketType.Message, Message = pm };
        await session.SendAsync(packet);

        if (target is not null && target.Id != session.Id)
        {
            await target.SendAsync(packet);
        }
        else if (target is null)
        {
            await session.SendAsync(new Packet { Type = PacketType.Info, Nick = to, Kind = "offline" });
        }
    }

    private async Task HandlePrivateHistoryRequestAsync(Session session, Packet packet)
    {
        var peer = Sanitize(packet.Nick, Protocol.MaxNickLength);
        if (peer.Length == 0) return;

        await session.SendAsync(new Packet
        {
            Type = PacketType.PrivateHistory,
            Nick = peer,
            History = _store.GetPrivateLast(session.Nick!, peer, Protocol.HistorySize),
        });
    }

    /// <summary>Автор может изменить текст своего сообщения — рассылаем обновление тем же, кто видел оригинал.</summary>
    private async Task HandleEditAsync(Session session, Packet packet)
    {
        if (packet.MessageId is not { } id) return;
        var text = Sanitize(packet.Text, Protocol.MaxTextLength);
        if (text.Length == 0) return;

        var existing = _store.GetById(id);
        if (existing is null || existing.IsSystem || existing.IsDeleted) return;
        if (!string.Equals(existing.Nick, session.Nick, StringComparison.CurrentCultureIgnoreCase)) return;

        _store.UpdateText(id, text);
        existing.Text = text;
        existing.IsEdited = true;

        await RelayToConversationAsync(existing, new Packet { Type = PacketType.MessageUpdated, Message = existing });
    }

    /// <summary>Автор может удалить своё сообщение — текст стирается, остаётся только отметка.</summary>
    private async Task HandleDeleteAsync(Session session, Packet packet)
    {
        if (packet.MessageId is not { } id) return;

        var existing = _store.GetById(id);
        if (existing is null || existing.IsSystem || existing.IsDeleted) return;
        if (!string.Equals(existing.Nick, session.Nick, StringComparison.CurrentCultureIgnoreCase)) return;

        _store.MarkDeleted(id);
        existing.Text = "";
        existing.IsDeleted = true;

        await RelayToConversationAsync(existing, new Packet { Type = PacketType.MessageDeleted, Message = existing });
    }

    /// <summary>
    /// Очищает диалог целиком — общий чат для всех, личную переписку только для
    /// её двух участников. Это удаление из общей истории, а не «скрыть у себя».
    /// </summary>
    private async Task HandleClearAsync(Session session, Packet packet)
    {
        var to = Sanitize(packet.To, Protocol.MaxNickLength);

        if (to.Length == 0)
        {
            _store.ClearGroup();
            await BroadcastAsync(new Packet { Type = PacketType.ConversationCleared, To = "" });
            return;
        }

        _store.ClearPrivate(session.Nick!, to);
        await session.SendAsync(new Packet { Type = PacketType.ConversationCleared, To = to });

        var target = FindSessionByNick(to);
        if (target is not null && target.Id != session.Id)
        {
            // у собеседника этот же диалог называется от имени session.Nick, а не to
            await target.SendAsync(new Packet { Type = PacketType.ConversationCleared, To = session.Nick });
        }
    }

    /// <summary>
    /// Файл приходит одним пакетом целиком (см. Protocol.MaxFileBytes) и сохраняется в БД;
    /// рассылка несёт только имя/размер — сами байты качаются по отдельному запросу.
    /// </summary>
    private async Task HandleFileAsync(Session session, Packet packet)
    {
        if (packet.FileName is not { Length: > 0 } rawName || packet.Data is not { Length: > 0 } b64) return;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return; }

        if (bytes.LongLength > Protocol.MaxFileBytes)
        {
            await session.SendAsync(new Packet { Type = PacketType.Info, Kind = "file_too_big" });
            return;
        }

        var fileName = Sanitize(rawName, Protocol.MaxFileNameLength);
        if (fileName.Length == 0) return;

        var to = Sanitize(packet.To, Protocol.MaxNickLength);
        if (to.Length == 0)
        {
            var msg = new ChatMessage { Nick = session.Nick!, Text = "", FileName = fileName, FileSize = bytes.LongLength, Ts = ChatMessage.Now() };
            _store.Add(msg, bytes);
            await BroadcastAsync(new Packet { Type = PacketType.Message, Message = msg });
            return;
        }

        var target = FindSessionByNick(to);
        var pm = new ChatMessage { Nick = session.Nick!, To = target?.Nick ?? to, Text = "", FileName = fileName, FileSize = bytes.LongLength, Ts = ChatMessage.Now() };
        _store.Add(pm, bytes);

        var outPacket = new Packet { Type = PacketType.Message, Message = pm };
        await session.SendAsync(outPacket);

        if (target is not null && target.Id != session.Id)
        {
            await target.SendAsync(outPacket);
        }
        else if (target is null)
        {
            await session.SendAsync(new Packet { Type = PacketType.Info, Nick = to, Kind = "offline" });
        }
    }

    /// <summary>Байты файла качает только отправитель или получатель того сообщения — не кто угодно по Id.</summary>
    private async Task HandleFileRequestAsync(Session session, Packet packet)
    {
        if (packet.MessageId is not { } id) return;

        var meta = _store.GetById(id);
        if (meta is null || meta.FileName is null) return;

        if (meta.IsPrivate &&
            !string.Equals(meta.Nick, session.Nick, StringComparison.CurrentCultureIgnoreCase) &&
            !string.Equals(meta.To, session.Nick, StringComparison.CurrentCultureIgnoreCase))
        {
            return;
        }

        var data = _store.GetFileData(id);
        if (data is null) return;

        await session.SendAsync(new Packet
        {
            Type = PacketType.FileData,
            MessageId = id,
            FileName = meta.FileName,
            Data = Convert.ToBase64String(data),
        });
    }

    /// <summary>Получатель подтверждает, что личное сообщение реально дошло до его приложения.</summary>
    private async Task HandleDeliveredAsync(Session session, Packet packet)
    {
        if (packet.MessageId is not { } id) return;

        var existing = _store.GetById(id);
        if (existing is null || !existing.IsPrivate || existing.IsDelivered) return;
        // доставленным может отметить только получатель, не сам автор
        if (string.Equals(existing.Nick, session.Nick, StringComparison.CurrentCultureIgnoreCase)) return;

        _store.MarkDelivered(id);
        existing.IsDelivered = true;

        await RelayToConversationAsync(existing, new Packet { Type = PacketType.MessageUpdated, Message = existing });
    }

    /// <summary>
    /// Получатель отмечает переписку с автором (Nick) прочитанной по MessageId включительно.
    /// Автору рассылается MessageUpdated на каждое затронутое сообщение — тем же путём,
    /// каким приходят правки.
    /// </summary>
    private async Task HandleMarkReadAsync(Session session, Packet packet)
    {
        var author = Sanitize(packet.Nick, Protocol.MaxNickLength);
        if (author.Length == 0 || packet.MessageId is not { } upToId) return;

        var ids = _store.MarkReadUpTo(author, session.Nick!, upToId);
        if (ids.Count == 0) return;

        var authorSession = FindSessionByNick(author);
        if (authorSession is null) return;

        foreach (var id in ids)
        {
            var msg = _store.GetById(id);
            if (msg is null) continue;
            await authorSession.SendAsync(new Packet { Type = PacketType.MessageUpdated, Message = msg });
        }
    }

    /// <summary>«Печатает» — ничего не хранится, просто ретранслируется дальше.</summary>
    private async Task HandleTypingAsync(Session session, Packet packet)
    {
        var to = Sanitize(packet.To, Protocol.MaxNickLength);

        if (to.Length == 0)
        {
            var line = new Packet { Type = PacketType.UserTyping, Nick = session.Nick, To = "" }.ToJson();
            foreach (var s in _sessions.Values)
                if (s.Nick is not null && s.Id != session.Id) await s.SendRawAsync(line);
            return;
        }

        var target = FindSessionByNick(to);
        if (target is not null && target.Id != session.Id)
        {
            await target.SendAsync(new Packet { Type = PacketType.UserTyping, Nick = session.Nick, To = session.Nick });
        }
    }

    /// <summary>Рассылает обновление (правка/удаление) той же аудитории, что видела исходное сообщение.</summary>
    private async Task RelayToConversationAsync(ChatMessage msg, Packet packet)
    {
        if (!msg.IsPrivate)
        {
            await BroadcastAsync(packet);
            return;
        }

        var line = packet.ToJson();
        foreach (var s in _sessions.Values)
        {
            if (s.Nick is null) continue;
            if (string.Equals(s.Nick, msg.Nick, StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals(s.Nick, msg.To, StringComparison.CurrentCultureIgnoreCase))
            {
                await s.SendRawAsync(line);
            }
        }
    }

    private Session? FindSessionByNick(string nick) =>
        _sessions.Values.FirstOrDefault(s =>
            s.Nick is not null && string.Equals(s.Nick, nick, StringComparison.CurrentCultureIgnoreCase));

    private async Task DropAsync(Session session)
    {
        _sessions.TryRemove(session.Id, out _);
        session.Dispose();

        if (session.Nick is null) return;
        await BroadcastSystemAsync("left", session.Nick);
        await BroadcastUsersAsync();
    }

    /// <summary>
    /// Текст не запекаем на сервере — только код события ("joined"/"left") и ник.
    /// Каждый клиент сам собирает фразу на своём языке (см. Services.Strings).
    /// </summary>
    private async Task BroadcastSystemAsync(string kind, string nick)
    {
        var msg = new ChatMessage { Nick = nick, Text = "", Ts = ChatMessage.Now(), IsSystem = true, SystemKind = kind };
        _store.Add(msg);
        await BroadcastAsync(new Packet { Type = PacketType.Message, Message = msg });
    }

    private Task BroadcastUsersAsync() =>
        BroadcastAsync(new Packet { Type = PacketType.Users, Users = CurrentUsers() });

    private async Task BroadcastAsync(Packet packet)
    {
        var line = packet.ToJson();
        foreach (var session in _sessions.Values)
        {
            if (session.Nick is null) continue;
            await session.SendRawAsync(line);
        }
    }

    private List<string> CurrentUsers() =>
        _sessions.Values
            .Select(s => s.Nick)
            .Where(n => n is not null)
            .Select(n => n!)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Занятый ник получает номер: «Вася», «Вася (2)», «Вася (3)»…</summary>
    private string MakeUniqueNick(string wanted)
    {
        var taken = CurrentUsers().ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;

        for (var i = 2; ; i++)
        {
            var candidate = $"{wanted} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private static string Sanitize(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _beacon.Dispose();
        try { _listener.Stop(); } catch { /* ignore */ }
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _store.Dispose();
        _cts.Dispose();
    }

    /// <summary>Одно клиентское подключение глазами сервера.</summary>
    private sealed class Session(TcpClient tcp) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Guid Id { get; } = Guid.NewGuid();
        public TcpClient Tcp { get; } = tcp;
        public StreamWriter? Writer { get; set; }
        public string? Nick { get; set; }

        public Task SendAsync(Packet packet) => SendRawAsync(packet.ToJson());

        public async Task SendRawAsync(string line)
        {
            var writer = Writer;
            if (writer is null) return;

            await _writeLock.WaitAsync();
            try { await writer.WriteLineAsync(line); }
            catch (IOException) { /* отвалился — закроет свой цикл чтения */ }
            catch (ObjectDisposedException) { }
            finally { _writeLock.Release(); }
        }

        public void Dispose()
        {
            try { Tcp.Close(); } catch { /* ignore */ }
            _writeLock.Dispose();
        }
    }
}
