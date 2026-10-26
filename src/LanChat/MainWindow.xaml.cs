using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LanChat.Models;
using LanChat.Net;
using LanChat.Services;
using LanChat.Storage;

namespace LanChat;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChatMessage> _groupMessages = [];
    private readonly Dictionary<string, ObservableCollection<ChatMessage>> _privateThreads =
        new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> _historyRequested = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly ObservableCollection<ConversationItem> _conversations = [];
    private readonly ObservableCollection<DiscoveredServer> _servers = [];
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SoundPlayer _sounds = new();
    private readonly Dictionary<long, string> _downloadedFiles = [];
    private readonly HashSet<long> _pendingDownloads = [];

    /// <summary>Кто сейчас печатает, по ключу диалога ("" — общий чат, иначе ник собеседника).</summary>
    private readonly Dictionary<string, HashSet<string>> _typingByConversation =
        new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<(string Conv, string Nick), DispatcherTimer> _typingTimers = [];
    private DateTime _lastTypingSent = DateTime.MinValue;

    private DiscoveryListener? _discovery;
    private ChatServer? _server;
    private ChatClient? _client;
    private TrayIcon? _tray;

    /// <summary>null — выбран общий чат; иначе ник собеседника в личной переписке.</summary>
    private string? _activePeer;

    /// <summary>Id сообщения, которое сейчас правим, пока не отправили новый текст. null — обычная отправка.</summary>
    private long? _editingMessageId;

    /// <summary>Имя комнаты от сервера — обычно имя компьютера хоста, без локализуемых слов внутри.</summary>
    private string _roomName = "";
    private bool _trayHintShown;
    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();

        ConversationList.ItemsSource = _conversations;
        ServerList.ItemsSource = _servers;
        MessageList.ItemsSource = _groupMessages;

        NickBox.Text = string.IsNullOrWhiteSpace(_settings.Nick) ? Environment.UserName : _settings.Nick;
        if (!_settings.AutoStartInitialized)
        {
            // Первый запуск программы — включаем автозапуск по умолчанию.
            // Дальше это уже выбор пользователя, сами больше не трогаем.
            AutoStart.SetEnabled(true);
            _settings.AutoStartInitialized = true;
            _settings.Save();
        }
        AutoStartCheckBox.IsChecked = AutoStart.IsEnabled();

        Icon = LoadEmbeddedImage("Assets.Icon.app.ico");
        AuthorPhotoBrush.ImageSource = LoadEmbeddedImage("Assets.About.author-square.jpg");

        UpdateThemeIcon();
        UpdateLanguageIcon();
        Strings.Changed += OnLanguageChanged;

        Loaded += (_, _) =>
        {
            StartDiscovery();
            SetupTray();
            NickBox.Focus();
            NickBox.SelectAll();
        };
    }

    private static System.Windows.Media.ImageSource LoadEmbeddedImage(string resourceSuffix)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"{asm.GetName().Name}.{resourceSuffix}")
            ?? throw new FileNotFoundException($"Embedded-ресурс не найден: {resourceSuffix}");
        return System.Windows.Media.Imaging.BitmapFrame.Create(
            stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
    }

    private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e) =>
        AutoStart.SetEnabled(AutoStartCheckBox.IsChecked == true);

    // ===================== О программе =====================

    private void AboutButton_Click(object sender, RoutedEventArgs e) =>
        AboutOverlay.Visibility = AboutOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void AboutOverlay_Click(object sender, MouseButtonEventArgs e) => AboutOverlay.Visibility = Visibility.Collapsed;

    private void AboutCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true; // не закрывать по клику внутри карточки

    // ===================== Тема и язык =====================

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = _settings.Theme == "light" ? "dark" : "light";
        _settings.Save();
        (Application.Current as App)?.ApplyTheme(_settings.Theme);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon() => ThemeToggleButton.Content = _settings.Theme == "light" ? "🌙" : "☀️";

    private void LanguageToggle_Click(object sender, RoutedEventArgs e)
    {
        var i = Array.IndexOf(Strings.Available, Strings.Current);
        var next = Strings.Available[(i + 1) % Strings.Available.Length];
        _settings.Language = next;
        _settings.Save();
        Strings.SetLanguage(next); // поднимет Changed — остальное обновится само
    }

    private void UpdateLanguageIcon() => LanguageToggleButton.Content = Strings.Current.ToUpperInvariant();

    /// <summary>
    /// DynamicResource в XAML уже сам перекрашивает статичные подписи (кнопки, лейблы).
    /// Здесь — то, что не биндится из XAML: заголовки, уже построенные из кода, трей.
    /// </summary>
    private void OnLanguageChanged() => Dispatcher.Invoke(() =>
    {
        UpdateLanguageIcon();
        RefreshDynamicText();
        _tray?.ApplyLanguage();
    });

    // ===================== Системный трей =====================

    private void SetupTray()
    {
        _tray = new TrayIcon();
        _tray.OpenRequested += () => Dispatcher.Invoke(RestoreFromTray);
        _tray.ExitRequested += () => Dispatcher.Invoke(() =>
        {
            _reallyClosing = true;
            Close();
        });
    }

    internal void RestoreFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    // ===================== Поиск чатов в сети =====================

    private void StartDiscovery()
    {
        if (_discovery is not null) return;

        try
        {
            _discovery = new DiscoveryListener();
            _discovery.ServersChanged += OnServersChanged;
            _discovery.Start();
        }
        catch (SocketException ex)
        {
            _discovery = null;
            ShowConnectError(Strings.Get("err_discovery_failed", Protocol.DiscoveryPort, ex.Message));
        }
    }

    private void StopDiscovery()
    {
        if (_discovery is null) return;
        _discovery.ServersChanged -= OnServersChanged;
        _discovery.Dispose();
        _discovery = null;
    }

    private void OnServersChanged(IReadOnlyList<DiscoveredServer> found) => Dispatcher.Invoke(() =>
    {
        var selectedKey = (ServerList.SelectedItem as DiscoveredServer)?.Key;

        _servers.Clear();
        foreach (var s in found) _servers.Add(s);

        if (selectedKey is not null)
            ServerList.SelectedItem = _servers.FirstOrDefault(s => s.Key == selectedKey);

        var any = _servers.Count > 0;
        ServerList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        UpdateJoinButton();
    });

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateJoinButton();

    private void UpdateJoinButton() => JoinButton.IsEnabled = ServerList.SelectedItem is not null;

    // ===================== Кнопки экрана подключения =====================

    private async void ServerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is DiscoveredServer server) await JoinAsync(server.Address, server.Port);
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is DiscoveredServer server) await JoinAsync(server.Address, server.Port);
    }

    private async void NickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ServerList.SelectedItem is DiscoveredServer server) await JoinAsync(server.Address, server.Port);
    }

    private async void ManualIpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await ConnectManualAsync();
    }

    private async void ManualConnect_Click(object sender, RoutedEventArgs e) => await ConnectManualAsync();

    private async Task ConnectManualAsync()
    {
        var raw = ManualIpBox.Text.Trim();
        if (raw.Length == 0)
        {
            ShowConnectError(Strings.Get("err_enter_address"));
            return;
        }

        var host = raw;
        var port = Protocol.TcpPort;

        // Допускаем и «192.168.1.10», и «192.168.1.10:5000».
        var colon = raw.LastIndexOf(':');
        if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var parsed))
        {
            host = raw[..colon];
            port = parsed;
        }

        await JoinAsync(host, port);
    }

    private async void HostButton_Click(object sender, RoutedEventArgs e)
    {
        var nick = ReadNick();
        if (nick is null) return;

        HostButton.IsEnabled = false;
        try
        {
            // Без переводимых слов внутри — имя комнаты уходит по сети всем, у кого может
            // быть другой язык интерфейса; локализуемая обвязка добавляется уже на экране.
            _server = new ChatServer(Environment.MachineName);
            _server.Failed += reason => Dispatcher.Invoke(() => StatusText.Text = reason);
            _server.Start();
        }
        catch (SocketException ex)
        {
            _server?.Dispose();
            _server = null;
            HostButton.IsEnabled = true;
            ShowConnectError(Strings.Get("err_port_busy", Protocol.TcpPort, ex.Message));
            return;
        }

        // Хост общается со своим же сервером как обычный клиент — так UI одинаков в обоих режимах.
        if (!await ConnectClientAsync("127.0.0.1", Protocol.TcpPort, nick))
        {
            _server?.Dispose();
            _server = null;
        }

        HostButton.IsEnabled = true;
    }

    private async Task JoinAsync(string host, int port)
    {
        var nick = ReadNick();
        if (nick is null) return;

        JoinButton.IsEnabled = false;
        await ConnectClientAsync(host, port, nick);
        UpdateJoinButton();
    }

    /// <summary>Ник из поля ввода, либо null — тогда причина уже показана пользователю.</summary>
    private string? ReadNick()
    {
        var nick = NickBox.Text.Trim();
        if (nick.Length == 0)
        {
            ShowConnectError(Strings.Get("err_enter_name"));
            NickBox.Focus();
            return null;
        }

        _settings.Nick = nick;
        _settings.Save();
        HideConnectError();
        return nick;
    }

    private async Task<bool> ConnectClientAsync(string host, int port, string nick)
    {
        _client = new ChatClient();
        _client.Connected += OnConnected;
        _client.MessageReceived += OnMessageReceived;
        _client.UsersChanged += OnUsersChanged;
        _client.PrivateHistoryReceived += OnPrivateHistoryReceived;
        _client.InfoReceived += OnInfoReceived;
        _client.MessageUpdated += OnMessageUpdated;
        _client.MessageDeleted += OnMessageDeleted;
        _client.ConversationCleared += OnConversationCleared;
        _client.FileDataReceived += OnFileDataReceived;
        _client.TypingReceived += OnTypingReceived;
        _client.Disconnected += OnDisconnected;

        var ok = await _client.ConnectAsync(host, port, nick, TimeSpan.FromSeconds(5));
        if (!ok)
        {
            DetachClient();
            return false;
        }
        return true;
    }

    // ===================== События клиента =====================

    private void OnConnected(string nick, string room, IReadOnlyList<string> users,
                             IReadOnlyList<ChatMessage> history) => Dispatcher.Invoke(() =>
    {
        StopDiscovery();

        _groupMessages.Clear();
        foreach (var m in history) _groupMessages.Add(m);

        _roomName = string.IsNullOrWhiteSpace(room) ? Environment.MachineName : room;

        RebuildConversations(users);
        ActivateConversation(_conversations.First(c => c.IsGroup));

        ConnectView.Visibility = Visibility.Collapsed;
        ChatView.Visibility = Visibility.Visible;
        InputBox.Focus();
    });

    private void OnMessageReceived(ChatMessage msg) => Dispatcher.Invoke(() =>
    {
        if (msg.IsPrivate)
        {
            var peer = msg.PeerFor(_client?.Nick ?? "");
            var thread = GetOrCreateThread(peer);
            thread.Add(msg);

            if (IsActivePeer(peer)) { ScrollToEnd(); MarkThreadReadIfActive(peer); }
            else BumpUnread(peer);
        }
        else
        {
            _groupMessages.Add(msg);
            if (_activePeer is null) ScrollToEnd();
            else BumpUnread(null);
        }

        if (!msg.IsMine && !msg.IsSystem)
        {
            _sounds.PlayReceived();
            RestoreFromTray();
        }
    });

    private void OnTypingReceived(string nick, string conversationKey) => Dispatcher.Invoke(() =>
    {
        if (!_typingByConversation.TryGetValue(conversationKey, out var set))
        {
            set = new(StringComparer.CurrentCultureIgnoreCase);
            _typingByConversation[conversationKey] = set;
        }
        set.Add(nick);
        RefreshTypingDisplay();

        var timerKey = (conversationKey, nick);
        if (_typingTimers.TryGetValue(timerKey, out var existingTimer)) existingTimer.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            set.Remove(nick);
            _typingTimers.Remove(timerKey);
            RefreshTypingDisplay();
        };
        _typingTimers[timerKey] = timer;
        timer.Start();
    });

    private void OnUsersChanged(IReadOnlyList<string> users) => Dispatcher.Invoke(() => RebuildConversations(users));

    private void OnPrivateHistoryReceived(string peer, IReadOnlyList<ChatMessage> history) => Dispatcher.Invoke(() =>
    {
        if (peer.Length == 0) return;

        var thread = GetOrCreateThread(peer);
        // Историю вставляем перед тем, что уже могло прийти вживую, пока она грузилась.
        for (var i = history.Count - 1; i >= 0; i--) thread.Insert(0, history[i]);

        if (IsActivePeer(peer)) ScrollToEnd();
    });

    private void OnInfoReceived(string peer, string text) => Dispatcher.Invoke(() =>
    {
        if (peer.Length == 0 || text.Length == 0) return;

        var thread = GetOrCreateThread(peer);
        thread.Add(new ChatMessage { Text = text, Ts = ChatMessage.Now(), IsSystem = true });

        if (IsActivePeer(peer)) ScrollToEnd();
        else BumpUnread(peer);
    });

    private void OnMessageUpdated(ChatMessage msg) => Dispatcher.Invoke(() => ReplaceMessage(msg));

    private void OnMessageDeleted(ChatMessage msg) => Dispatcher.Invoke(() => ReplaceMessage(msg));

    private void OnConversationCleared(string peer) => Dispatcher.Invoke(() =>
    {
        if (peer.Length == 0) _groupMessages.Clear();
        else GetOrCreateThread(peer).Clear();
    });

    private void OnFileDataReceived(long id, string fileName, byte[] data) => Dispatcher.Invoke(() =>
    {
        _pendingDownloads.Remove(id);
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanChat");
            Directory.CreateDirectory(dir);
            var path = UniquePath(Path.Combine(dir, fileName));
            File.WriteAllBytes(path, data);
            _downloadedFiles[id] = path;
            OpenFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowLocalNotice(Strings.Get("save_file_failed", fileName, ex.Message));
        }
    });

    private void OnDisconnected(string? reason) => Dispatcher.Invoke(() =>
    {
        DetachClient();
        StopServer();
        BackToConnectView();

        if (reason is not null) ShowConnectError(reason);
    });

    // ===================== Диалоги: общий чат + личные переписки =====================

    private ObservableCollection<ChatMessage> GetOrCreateThread(string peer)
    {
        if (!_privateThreads.TryGetValue(peer, out var thread))
        {
            thread = [];
            _privateThreads[peer] = thread;
        }
        return thread;
    }

    private bool IsActivePeer(string peer) =>
        _activePeer is not null && string.Equals(_activePeer, peer, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Находит сообщение по Id в нужном списке (общий чат или личный диалог) и заменяет целиком —
    /// так ObservableCollection поднимет уведомление Replace, и UI обновит именно этот пузырь.</summary>
    private bool ReplaceMessage(ChatMessage updated)
    {
        var list = updated.IsPrivate ? GetOrCreateThread(updated.PeerFor(_client?.Nick ?? "")) : _groupMessages;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == updated.Id) { list[i] = updated; return true; }
        }
        return false;
    }

    /// <summary>Локальная (не сетевая) системная строка в текущем диалоге — для ошибок вроде «файл не отправлен».</summary>
    private void ShowLocalNotice(string text)
    {
        var list = _activePeer is null ? _groupMessages : GetOrCreateThread(_activePeer);
        list.Add(new ChatMessage { Text = text, Ts = ChatMessage.Now(), IsSystem = true });
        ScrollToEnd(); // list — это и есть сейчас отображаемый диалог
    }

    /// <summary>
    /// Перестраивает список диалогов по текущим участникам, сохраняя непрочитанные
    /// счётчики и не давая пропасть открытому сейчас личному диалогу, даже если
    /// собеседник вышел из сети.
    /// </summary>
    private void RebuildConversations(IReadOnlyList<string> onlineUsers)
    {
        var myNick = _client?.Nick ?? "";
        var unreadByKey = _conversations.ToDictionary(ConversationKey, c => c.Unread);

        var names = onlineUsers
            .Where(u => !string.Equals(u, myNick, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        if (_activePeer is not null && !names.Any(n => string.Equals(n, _activePeer, StringComparison.CurrentCultureIgnoreCase)))
            names.Add(_activePeer);

        _conversations.Clear();

        var group = new ConversationItem { IsGroup = true, Name = "Общий чат", IsOnline = true };
        group.Unread = unreadByKey.GetValueOrDefault("");
        _conversations.Add(group);

        foreach (var name in names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
        {
            var isOnline = onlineUsers.Any(u => string.Equals(u, name, StringComparison.CurrentCultureIgnoreCase));
            var item = new ConversationItem { IsGroup = false, Name = name, IsOnline = isOnline };
            item.Unread = unreadByKey.GetValueOrDefault(name);
            _conversations.Add(item);
        }

        ConversationList.SelectedItem = _conversations.FirstOrDefault(c => ConversationKey(c) == (_activePeer ?? ""));
    }

    private static string ConversationKey(ConversationItem c) => c.IsGroup ? "" : c.Name;

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is ConversationItem item) ActivateConversation(item);
    }

    private void ActivateConversation(ConversationItem item)
    {
        if (_editingMessageId is not null) CancelEdit();
        item.Unread = 0;

        _activePeer = item.IsGroup ? null : item.Name;
        MessageList.ItemsSource = item.IsGroup ? _groupMessages : GetOrCreateThread(item.Name);

        if (!item.IsGroup && _historyRequested.Add(item.Name))
            _ = _client?.RequestPrivateHistoryAsync(item.Name);

        RefreshDynamicText();
        MarkThreadReadIfActive(_activePeer);
        ScrollToEnd();
    }

    private void BumpUnread(string? peer)
    {
        var key = peer ?? "";
        var item = _conversations.FirstOrDefault(c => ConversationKey(c) == key);
        if (item is not null) item.Unread++;
    }

    /// <summary>Сообщаем собеседнику, что прочитали всё из его сообщений по самое свежее включительно.</summary>
    private void MarkThreadReadIfActive(string? peer)
    {
        if (peer is null || _client is null) return;

        var thread = GetOrCreateThread(peer);
        var maxId = thread.Where(m => !m.IsMine && !m.IsSystem).Select(m => m.Id).DefaultIfEmpty(0).Max();
        if (maxId > 0) _ = _client.MarkReadAsync(peer, maxId);
    }

    /// <summary>
    /// Текст шапки (заголовок, онлайн-статус, окно) собирается тут, а не один раз в
    /// OnConnected/ActivateConversation — чтобы при смене языка на лету всё, что уже
    /// показано, перерисовалось на новом языке, а не осталось в старом.
    /// </summary>
    private void RefreshDynamicText()
    {
        if (_client is null) return;

        if (_activePeer is null)
        {
            RoomTitle.Text = GroupTitle();
            StatusText.Text = ConnectionStatusText();
        }
        else
        {
            var item = _conversations.FirstOrDefault(c =>
                !c.IsGroup && string.Equals(c.Name, _activePeer, StringComparison.CurrentCultureIgnoreCase));
            RoomTitle.Text = Strings.Get("dm_title", _activePeer);
            StatusText.Text = (item?.IsOnline ?? false) ? Strings.Get("online") : Strings.Get("offline_hint");
        }

        Title = Strings.Get("window_title", GroupTitle());
        RefreshTypingDisplay();
    }

    private string GroupTitle() => $"{Strings.Get("default_room_name")} · {_roomName}";

    private string ConnectionStatusText() => _server is not null
        ? Strings.Get("status_hosting", _client?.Nick ?? "")
        : Strings.Get("status_connected", _client?.Nick ?? "");

    private void RefreshTypingDisplay()
    {
        var key = _activePeer ?? "";
        if (_typingByConversation.TryGetValue(key, out var set) && set.Count > 0)
        {
            TypingText.Text = Strings.FormatTyping(set);
            TypingText.Visibility = Visibility.Visible;
        }
        else
        {
            TypingText.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== Отправка =====================

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_client is null || InputBox.Text.Length == 0) return;

        var now = DateTime.UtcNow;
        if ((now - _lastTypingSent).TotalSeconds < 2) return; // не спамим «печатает» на каждую букву
        _lastTypingSent = now;
        _ = _client.SendTypingAsync(_activePeer);
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Shift+Enter — перенос строки, просто Enter — отправка.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        Send();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || _client is null) return;

        if (_editingMessageId is { } editId)
        {
            InputBox.Clear();
            EditBar.Visibility = Visibility.Collapsed;
            _editingMessageId = null;
            _ = _client.EditMessageAsync(editId, text);
            return;
        }

        InputBox.Clear();

        if (_activePeer is null) _ = _client.SendMessageAsync(text);
        else _ = _client.SendPrivateMessageAsync(_activePeer, text);

        _sounds.PlaySent();
    }

    // ===================== Правка и удаление сообщений =====================

    private void EditMessage_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChatMessage msg) return;
        if (!msg.IsMine || msg.IsDeleted || msg.HasFile) return;

        _editingMessageId = msg.Id;
        InputBox.Text = msg.Text;
        InputBox.CaretIndex = InputBox.Text.Length;
        InputBox.Focus();
        EditBar.Visibility = Visibility.Visible;
    }

    private void CancelEdit_Click(object sender, MouseButtonEventArgs e) => CancelEdit();

    private void CancelEdit()
    {
        _editingMessageId = null;
        InputBox.Clear();
        EditBar.Visibility = Visibility.Collapsed;
    }

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChatMessage msg) return;
        if (!msg.IsMine || msg.IsDeleted) return;

        if (_editingMessageId == msg.Id) CancelEdit();
        await (_client?.DeleteMessageAsync(msg.Id) ?? Task.CompletedTask);
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;

        var label = _activePeer is null ? Strings.Get("clear_group_label") : Strings.Get("clear_peer_label", _activePeer);
        var result = MessageBox.Show(
            Strings.Get("clear_confirm_text", label),
            Strings.Get("clear_confirm_title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await _client.ClearConversationAsync(_activePeer);
    }

    // ===================== Эмодзи =====================

    private void EmojiButton_Click(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = !EmojiPopup.IsOpen;

    private void EmojiPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string emoji }) return;

        var caret = InputBox.CaretIndex;
        InputBox.Text = InputBox.Text.Insert(caret, emoji);
        InputBox.CaretIndex = caret + emoji.Length;
        InputBox.Focus();
        EmojiPopup.IsOpen = false;
    }

    // ===================== Файлы =====================

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;

        using var dialog = new System.Windows.Forms.OpenFileDialog { Title = Strings.Get("attach_tooltip") };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        await SendFilePathAsync(dialog.FileName);
    }

    /// <summary>Общий путь для кнопки 📎 и drag-and-drop — оба просто дают путь к файлу.</summary>
    private async Task SendFilePathAsync(string path)
    {
        if (_client is null) return;

        var (ok, error) = await _client.SendFileAsync(_activePeer, path);
        if (ok) _sounds.PlaySent();
        else ShowLocalNotice(error ?? Strings.Get("send_file_failed", Path.GetFileName(path)));
    }

    // ===================== Drag-and-drop файлов из проводника =====================

    private void ChatView_DragEnter(object sender, DragEventArgs e)
    {
        var accept = _client is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void ChatView_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void ChatView_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (_client is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                ShowLocalNotice(Strings.Get("folder_not_file", Path.GetFileName(path)));
                continue;
            }
            if (!File.Exists(path)) continue;

            await SendFilePathAsync(path);
        }
    }

    private async void FileChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChatMessage msg || !msg.HasFile) return;

        if (_downloadedFiles.TryGetValue(msg.Id, out var existing) && File.Exists(existing))
        {
            OpenFile(existing);
            return;
        }

        if (!_pendingDownloads.Add(msg.Id)) return; // уже качаем этот файл
        await (_client?.RequestFileAsync(msg.Id) ?? Task.CompletedTask);
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // не получилось открыть — файл всё равно лежит в папке загрузок, откроют сами
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(MessageScroll.ScrollToEnd,
        System.Windows.Threading.DispatcherPriority.ContextIdle);

    // ===================== Выход и уборка =====================

    private void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        DetachClient();
        StopServer();
        BackToConnectView();
    }

    private void BackToConnectView()
    {
        ChatView.Visibility = Visibility.Collapsed;
        ConnectView.Visibility = Visibility.Visible;
        Title = "LAN Chat";

        _conversations.Clear();
        _groupMessages.Clear();
        _privateThreads.Clear();
        _historyRequested.Clear();
        _downloadedFiles.Clear();
        _pendingDownloads.Clear();
        foreach (var t in _typingTimers.Values) t.Stop();
        _typingTimers.Clear();
        _typingByConversation.Clear();
        _activePeer = null;
        CancelEdit();
        _servers.Clear();

        ServerList.Visibility = Visibility.Collapsed;
        SearchHint.Visibility = Visibility.Visible;
        UpdateJoinButton();
        StartDiscovery();
    }

    private void DetachClient()
    {
        if (_client is null) return;

        _client.Connected -= OnConnected;
        _client.MessageReceived -= OnMessageReceived;
        _client.UsersChanged -= OnUsersChanged;
        _client.PrivateHistoryReceived -= OnPrivateHistoryReceived;
        _client.InfoReceived -= OnInfoReceived;
        _client.MessageUpdated -= OnMessageUpdated;
        _client.MessageDeleted -= OnMessageDeleted;
        _client.ConversationCleared -= OnConversationCleared;
        _client.FileDataReceived -= OnFileDataReceived;
        _client.TypingReceived -= OnTypingReceived;
        _client.Disconnected -= OnDisconnected;
        _client.Dispose();
        _client = null;
    }

    private void StopServer()
    {
        _server?.Dispose();
        _server = null;
    }

    private void ShowConnectError(string text)
    {
        ConnectError.Text = text;
        ConnectError.Visibility = Visibility.Visible;
    }

    private void HideConnectError() => ConnectError.Visibility = Visibility.Collapsed;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            // Крестик сворачивает в трей вместо выхода — чат продолжает ловить сообщения.
            e.Cancel = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloon(Strings.Get("tray_title"), Strings.Get("tray_hidden_msg"));
            }
            return;
        }

        DetachClient();
        StopServer();
        StopDiscovery();
        _tray?.Dispose();
        base.OnClosing(e);
    }
}
