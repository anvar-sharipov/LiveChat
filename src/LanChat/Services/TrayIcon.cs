using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace LanChat.Services;

/// <summary>
/// Значок в системном трее: окно сворачивается сюда вместо закрытия,
/// отсюда же разворачивается обратно и отсюда происходит настоящий выход.
/// Иконка — тот же app.ico, что и у самого exe (embedded resource, см. csproj).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _exitItem;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _openItem = new ToolStripMenuItem("", null, (_, _) => OpenRequested?.Invoke());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _appIcon = LoadEmbeddedIcon();
        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        ApplyLanguage();
    }

    /// <summary>Перечитывает подписи из Strings.Current — зовётся при смене языка в UI-потоке.</summary>
    public void ApplyLanguage()
    {
        _openItem.Text = Strings.Get("tray_open");
        _exitItem.Text = Strings.Get("tray_exit");
        _icon.Text = Strings.Get("tray_title");
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    private static Icon LoadEmbeddedIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{asm.GetName().Name}.Assets.Icon.app.ico";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded-ресурс не найден: {name}");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
