using System.Windows;

namespace LanChat.Services;

/// <summary>
/// Локализация интерфейса (ru/en/tk). Простой словарь вместо .resx — языков всего три,
/// и переключение должно работать мгновенно, без перезапуска окна.
/// Строки, которые когда-либо уходят по сети (системные «вошёл/вышел», офлайн-уведомление),
/// сервер НЕ форматирует сам — он шлёт только код события + параметры (ChatMessage.SystemKind,
/// Packet.Kind), а текст на нужном языке собирает уже клиент. Иначе у разноязыких участников
/// одного чата системные строки принудительно были бы на языке того, кто их вызвал.
/// </summary>
public static class Strings
{
    public static readonly string[] Available = ["ru", "en", "tk"];

    private const string Fallback = "ru";

    public static string Current { get; private set; } = Fallback;

    /// <summary>Сменить язык интерфейса. UI, подписанный на Changed, перерисовывает статичный текст.</summary>
    public static void SetLanguage(string code)
    {
        if (!Map.ContainsKey(code) || code == Current) return;
        Current = code;
        Changed?.Invoke();
    }

    public static event Action? Changed;

    public static string Get(string key) =>
        (Map.TryGetValue(Current, out var dict) && dict.TryGetValue(key, out var v)) ? v
        : (Map[Fallback].TryGetValue(key, out var fb) ? fb : key);

    public static string Get(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>
    /// Кладёт все строки текущего языка в ресурсы (ключи "Str_xxx") — так же, как палитра
    /// темы живёт в DynamicResource. XAML биндится на {DynamicResource Str_xxx} и сам
    /// перекрашивается при смене языка, без обхода визуального дерева руками.
    /// </summary>
    public static void PushAllToResources(ResourceDictionary target)
    {
        foreach (var key in Map[Fallback].Keys) target[$"Str_{key}"] = Get(key);
    }

    public static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} {Get("unit_b")}",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} {Get("unit_kb")}",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} {Get("unit_mb")}",
    };

    /// <summary>Текст для системного ChatMessage по его SystemKind ("joined"/"left").</summary>
    public static string FormatSystem(string kind, string nick) => kind switch
    {
        "joined" => Get("sys_joined", nick),
        "left" => Get("sys_left", nick),
        _ => nick,
    };

    public static string FormatTyping(IReadOnlyCollection<string> nicks) =>
        nicks.Count == 1 ? Get("typing_one", nicks.First()) : Get("typing_many", string.Join(", ", nicks));

    private static readonly Dictionary<string, Dictionary<string, string>> Map = new()
    {
        ["ru"] = new()
        {
            ["app_subtitle"] = "Чат для локальной сети — без интернета и серверов в облаке",
            ["your_name_label"] = "ВАШЕ ИМЯ",
            ["nick_placeholder"] = "Как вас назвать в чате",
            ["chats_in_network_label"] = "ЧАТЫ В ВАШЕЙ СЕТИ",
            ["searching_hint"] = "Ищем чаты в сети…",
            ["searching_hint2"] = "Если ни одного не нашлось — создайте свой",
            ["join_button"] = "Подключиться",
            ["or_divider"] = "или",
            ["host_button"] = "Создать свой чат",
            ["manual_ip_header"] = "Подключиться по IP вручную",
            ["manual_connect_button"] = "Вперёд",
            ["err_enter_address"] = "Введите адрес, например 192.168.1.10",
            ["err_enter_name"] = "Введите имя, под которым вас увидят в чате.",
            ["err_discovery_failed"] = "Не удалось слушать сеть (UDP {0}): {1}. Подключиться по IP вручную всё ещё можно.",
            ["err_port_busy"] = "Не удалось занять порт {0}: {1}. Возможно, чат на этом компьютере уже запущен.",
            ["err_connect_timeout"] = "Не удалось подключиться к {0}:{1} — сервер не ответил.",
            ["err_connect_failed"] = "Не удалось подключиться к {0}:{1} — {2}",
            ["err_connection_lost"] = "Связь с сервером потеряна.",
            ["err_rejected"] = "Сервер отклонил подключение.",
            ["default_room_name"] = "Общий чат",
            ["status_hosting"] = "Вы: {0}  ·  этот компьютер раздаёт чат",
            ["status_connected"] = "Вы: {0}  ·  подключены",
            ["clear_button"] = "Очистить",
            ["leave_button"] = "Выйти",
            ["dialogs_label"] = "ДИАЛОГИ",
            ["dm_title"] = "Переписка с {0}",
            ["online"] = "в сети",
            ["offline_hint"] = "сейчас не в сети — сообщения дойдут при подключении",
            ["edit_menu"] = "Изменить",
            ["delete_menu"] = "Удалить",
            ["editing_label"] = "Редактирование сообщения",
            ["cancel_edit"] = "Отменить",
            ["attach_tooltip"] = "Отправить файл",
            ["emoji_tooltip"] = "Эмодзи",
            ["input_placeholder"] = "Написать сообщение…  (Enter — отправить, Shift+Enter — новая строка)",
            ["send_button"] = "Отправить",
            ["drop_hint"] = "Отпустите, чтобы отправить файл",
            ["deleted_text"] = "Сообщение удалено",
            ["edited_mark"] = "изменено · ",
            ["folder_not_file"] = "«{0}» — это папка, папки отправлять нельзя.",
            ["send_file_failed"] = "Не удалось отправить «{0}».",
            ["save_file_failed"] = "Не удалось сохранить файл «{0}»: {1}",
            ["clear_confirm_title"] = "Очистить диалог",
            ["clear_confirm_text"] = "Удалить всю историю ({0}) для всех участников? Это необратимо.",
            ["clear_group_label"] = "общий чат",
            ["clear_peer_label"] = "переписку с «{0}»",
            ["window_title"] = "LAN Chat — {0}",
            ["tray_open"] = "Открыть чат",
            ["tray_exit"] = "Выход",
            ["tray_hidden_msg"] = "Чат свёрнут в трей и продолжает работать. Новое сообщение снова откроет окно.",
            ["tray_title"] = "LAN Chat",
            ["sys_joined"] = "{0} вошёл в чат",
            ["sys_left"] = "{0} вышел из чата",
            ["info_offline"] = "{0} сейчас не в сети — сообщение сохранено, но не доставлено.",
            ["err_file_too_big"] = "Файл больше {0} МБ — такой лимит для LAN-чата.",
            ["err_file_not_found"] = "Файл не найден.",
            ["err_file_read"] = "Не удалось прочитать файл: {0}",
            ["typing_one"] = "{0} печатает…",
            ["typing_many"] = "{0} печатают…",
            ["unit_b"] = "Б",
            ["unit_kb"] = "КБ",
            ["unit_mb"] = "МБ",
            ["server_subtitle"] = "{0}:{1}  ·  участников: {2}",
            ["about_title"] = "О программе",
            ["about_role"] = "Программист · Software Engineer",
            ["about_tagline"] = "LAN Chat — программа от Anvar Sharipov",
            ["about_mobile"] = "Мобильный",
            ["about_home"] = "Домашний",
            ["autostart_label"] = "Запускать при включении компьютера",
        },
        ["en"] = new()
        {
            ["app_subtitle"] = "LAN chat — no internet, no cloud servers",
            ["your_name_label"] = "YOUR NAME",
            ["nick_placeholder"] = "What should we call you in chat",
            ["chats_in_network_label"] = "CHATS ON YOUR NETWORK",
            ["searching_hint"] = "Looking for chats on the network…",
            ["searching_hint2"] = "Nothing found? Create your own",
            ["join_button"] = "Connect",
            ["or_divider"] = "or",
            ["host_button"] = "Create your own chat",
            ["manual_ip_header"] = "Connect by IP manually",
            ["manual_connect_button"] = "Go",
            ["err_enter_address"] = "Enter an address, e.g. 192.168.1.10",
            ["err_enter_name"] = "Enter the name others will see in chat.",
            ["err_discovery_failed"] = "Couldn't listen on the network (UDP {0}): {1}. You can still connect by IP manually.",
            ["err_port_busy"] = "Couldn't bind port {0}: {1}. A chat may already be running on this computer.",
            ["err_connect_timeout"] = "Couldn't connect to {0}:{1} — the server didn't respond.",
            ["err_connect_failed"] = "Couldn't connect to {0}:{1} — {2}",
            ["err_connection_lost"] = "Lost connection to the server.",
            ["err_rejected"] = "The server rejected the connection.",
            ["default_room_name"] = "Group chat",
            ["status_hosting"] = "You: {0}  ·  this computer is hosting the chat",
            ["status_connected"] = "You: {0}  ·  connected",
            ["clear_button"] = "Clear",
            ["leave_button"] = "Leave",
            ["dialogs_label"] = "CHATS",
            ["dm_title"] = "Chat with {0}",
            ["online"] = "online",
            ["offline_hint"] = "offline now — messages will arrive once they're back",
            ["edit_menu"] = "Edit",
            ["delete_menu"] = "Delete",
            ["editing_label"] = "Editing message",
            ["cancel_edit"] = "Cancel",
            ["attach_tooltip"] = "Send a file",
            ["emoji_tooltip"] = "Emoji",
            ["input_placeholder"] = "Type a message…  (Enter — send, Shift+Enter — new line)",
            ["send_button"] = "Send",
            ["drop_hint"] = "Drop to send the file",
            ["deleted_text"] = "Message deleted",
            ["edited_mark"] = "edited · ",
            ["folder_not_file"] = "“{0}” is a folder — folders can't be sent.",
            ["send_file_failed"] = "Couldn't send “{0}”.",
            ["save_file_failed"] = "Couldn't save file “{0}”: {1}",
            ["clear_confirm_title"] = "Clear chat",
            ["clear_confirm_text"] = "Delete all history ({0}) for every participant? This can't be undone.",
            ["clear_group_label"] = "the group chat",
            ["clear_peer_label"] = "the chat with “{0}”",
            ["window_title"] = "LAN Chat — {0}",
            ["tray_open"] = "Open chat",
            ["tray_exit"] = "Exit",
            ["tray_hidden_msg"] = "The chat is minimized to the tray and still running. A new message will reopen the window.",
            ["tray_title"] = "LAN Chat",
            ["sys_joined"] = "{0} joined the chat",
            ["sys_left"] = "{0} left the chat",
            ["info_offline"] = "{0} is offline right now — the message was saved but not delivered.",
            ["err_file_too_big"] = "File is bigger than {0} MB — that's the limit for this LAN chat.",
            ["err_file_not_found"] = "File not found.",
            ["err_file_read"] = "Couldn't read the file: {0}",
            ["typing_one"] = "{0} is typing…",
            ["typing_many"] = "{0} are typing…",
            ["unit_b"] = "B",
            ["unit_kb"] = "KB",
            ["unit_mb"] = "MB",
            ["server_subtitle"] = "{0}:{1}  ·  participants: {2}",
            ["about_title"] = "About",
            ["about_role"] = "Programmer · Software Engineer",
            ["about_tagline"] = "LAN Chat — software by Anvar Sharipov",
            ["about_mobile"] = "Mobile",
            ["about_home"] = "Home",
            ["autostart_label"] = "Launch when the computer starts",
        },
        ["tk"] = new()
        {
            ["app_subtitle"] = "Ýerli ulgam üçin söhbetdeşlik — internetsiz we bulut serwerlersiz",
            ["your_name_label"] = "ADYŇYZ",
            ["nick_placeholder"] = "Söhbetde nädip atlandyrylmak isleýärsiňiz",
            ["chats_in_network_label"] = "ULGAMYŇYZDAKY SÖHBETLER",
            ["searching_hint"] = "Ulgamda söhbetler gözlenýär…",
            ["searching_hint2"] = "Hiç zat tapylmasa — özüňiziňkini dörediň",
            ["join_button"] = "Birikmek",
            ["or_divider"] = "ýa-da",
            ["host_button"] = "Öz söhbediňizi dörediň",
            ["manual_ip_header"] = "IP arkaly elde birikmek",
            ["manual_connect_button"] = "Dowam",
            ["err_enter_address"] = "Salgyny giriziň, mysal üçin 192.168.1.10",
            ["err_enter_name"] = "Söhbetde görkeziljek adyňyzy giriziň.",
            ["err_discovery_failed"] = "Ulgamy diňläp bolmady (UDP {0}): {1}. IP arkaly elde birikmek henizem mümkin.",
            ["err_port_busy"] = "{0} portuny eýelemek başartmady: {1}. Belki, bu kompýuterde söhbet eýýäm işleýändir.",
            ["err_connect_timeout"] = "{0}:{1} bilen birikmek başartmady — server jogap bermedi.",
            ["err_connect_failed"] = "{0}:{1} bilen birikmek başartmady — {2}",
            ["err_connection_lost"] = "Server bilen baglanyşyk üzüldi.",
            ["err_rejected"] = "Server birikmäni ret etdi.",
            ["default_room_name"] = "Umumy söhbet",
            ["status_hosting"] = "Siz: {0}  ·  bu kompýuter söhbedi paýlaýar",
            ["status_connected"] = "Siz: {0}  ·  birikdirildi",
            ["clear_button"] = "Arassala",
            ["leave_button"] = "Çyk",
            ["dialogs_label"] = "SÖHBETLER",
            ["dm_title"] = "{0} bilen hususy ýazyşma",
            ["online"] = "onlaýn",
            ["offline_hint"] = "häzir oflaýn — birikende habarlar ýeter",
            ["edit_menu"] = "Üýtget",
            ["delete_menu"] = "Poz",
            ["editing_label"] = "Habar üýtgedilýär",
            ["cancel_edit"] = "Bes et",
            ["attach_tooltip"] = "Faýl ibermek",
            ["emoji_tooltip"] = "Emoji",
            ["input_placeholder"] = "Habar ýazyň…  (Enter — ibermek, Shift+Enter — täze setir)",
            ["send_button"] = "Ibermek",
            ["drop_hint"] = "Faýly ibermek üçin goýberiň",
            ["deleted_text"] = "Habar pozuldy",
            ["edited_mark"] = "üýtgedildi · ",
            ["folder_not_file"] = "«{0}» — bu bukja, bukjalary ibermek bolmaýar.",
            ["send_file_failed"] = "«{0}» iberilmedi.",
            ["save_file_failed"] = "«{0}» faýlyny ýatda saklamak başartmady: {1}",
            ["clear_confirm_title"] = "Söhbeti arassala",
            ["clear_confirm_text"] = "{0} taryhyny ähli gatnaşyjylar üçin pozmalymy? Bu yzyna gaýtarylmaýar.",
            ["clear_group_label"] = "umumy söhbeti",
            ["clear_peer_label"] = "«{0}» bilen ýazyşmany",
            ["window_title"] = "LAN Chat — {0}",
            ["tray_open"] = "Söhbedi aç",
            ["tray_exit"] = "Çykmak",
            ["tray_hidden_msg"] = "Söhbet trega ýygnaldy we işlemegini dowam etdirýär. Täze habar ýene penjiräni açar.",
            ["tray_title"] = "LAN Chat",
            ["sys_joined"] = "{0} söhbete goşuldy",
            ["sys_left"] = "{0} söhbetden çykdy",
            ["info_offline"] = "{0} häzir oflaýn — habar ýatda saklandy, ýöne iberilmedi.",
            ["err_file_too_big"] = "Faýl {0} MB-dan uly — LAN-söhbet üçin çäk şol.",
            ["err_file_not_found"] = "Faýl tapylmady.",
            ["err_file_read"] = "Faýly okap bolmady: {0}",
            ["typing_one"] = "{0} ýazýar…",
            ["typing_many"] = "{0} ýazýarlar…",
            ["unit_b"] = "B",
            ["unit_kb"] = "KB",
            ["unit_mb"] = "MB",
            ["server_subtitle"] = "{0}:{1}  ·  gatnaşyjy: {2}",
            ["about_title"] = "Programma barada",
            ["about_role"] = "Programmist · Software Engineer",
            ["about_tagline"] = "LAN Chat — Anvar Sharipowyň programmasy",
            ["about_mobile"] = "Mobil",
            ["about_home"] = "Öý",
            ["autostart_label"] = "Kompýuter açylanda işe başlasyn",
        },
    };
}
