using System.IO;
using System.Text.Json;

namespace LanChat.Storage;

/// <summary>Мелкие пользовательские настройки — чтобы не вводить имя при каждом запуске.</summary>
public sealed class AppSettings
{
    public string Nick { get; set; } = "";

    /// <summary>"dark" или "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>"ru", "en" или "tk".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>
    /// Автозапуск уже настраивали хоть раз (включая автоматику при первом запуске).
    /// Пока false — при старте сами включаем автозапуск по умолчанию. Как только
    /// пользователь сам тронул чекбокс (в любую сторону) — больше не перетираем его выбор.
    /// </summary>
    public bool AutoStartInitialized { get; set; }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanChat", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Повреждённый или недоступный файл настроек — не повод не запускаться.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
