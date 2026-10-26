using System.Threading;
using System.Windows;
using LanChat.Services;
using LanChat.Storage;

namespace LanChat;

public partial class App : Application
{
    // Фиксированные имена — один и тот же чат-процесс должен узнавать себя между запусками.
    private const string MutexName = "LanChat-SingleInstance-7f0c9b3e-9b5e-4a7a-8e1e-2a3c6a9b1d44";
    private const string ShowEventName = "LanChat-ShowRequest-7f0c9b3e-9b5e-4a7a-8e1e-2a3c6a9b1d44";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private ResourceDictionary? _paletteDict;

    /// <summary>Подмешивает палитру нужной темы вместо текущей — переключение без перезапуска.</summary>
    public void ApplyTheme(string theme)
    {
        var uri = new Uri($"Themes/{(theme == "light" ? "Light" : "Dark")}.xaml", UriKind.Relative);
        var next = new ResourceDictionary { Source = uri };

        if (_paletteDict is not null) Resources.MergedDictionaries.Remove(_paletteDict);
        Resources.MergedDictionaries.Add(next);
        _paletteDict = next;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Чат уже запущен (возможно, свёрнут в трей) — просим ту копию показаться
            // и сразу выходим сами, не создавая окно и не занимая порты второй раз.
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Та копия ещё не успела создать обработчик — не страшно, просто не покажется сама.
            }

            Shutdown();
            Environment.Exit(0);
            return;
        }

        var settings = AppSettings.Load();
        ApplyTheme(settings.Theme);
        Strings.SetLanguage(settings.Language);
        Strings.PushAllToResources(Resources); // SetLanguage выше не поднимет Changed, если язык уже ru — пушим явно
        Strings.Changed += () => Strings.PushAllToResources(Resources);

        base.OnStartup(e);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.Invoke(() => (MainWindow as MainWindow)?.RestoreFromTray());
            }
        })
        { IsBackground = true };
        listener.Start();

        // Необработанное исключение в UI-потоке не должно ронять чат молча.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* не наш — не беда */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
