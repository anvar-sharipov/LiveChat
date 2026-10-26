using Microsoft.Win32;

namespace LanChat.Services;

/// <summary>
/// Запуск вместе с Windows — через стандартный ключ Run в реестре текущего пользователя.
/// Не требует прав администратора и не трогает Планировщик задач.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanChat";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;
        key.SetValue(ValueName, $"\"{exePath}\"");
    }
}
