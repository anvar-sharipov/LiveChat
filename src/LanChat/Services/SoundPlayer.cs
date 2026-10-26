using System.IO;
using System.Reflection;
using System.Windows.Media;

namespace LanChat.Services;

/// <summary>
/// Звуки отправки/получения. Файлы встроены в exe как embedded resource —
/// работают на любом компьютере без установки .NET и без привязки к диску
/// разработчика. MediaPlayer не умеет открывать pack://application напрямую
/// (NotSupportedException), поэтому при старте распаковываем их во временный
/// файл и открываем оттуда обычным file:// URI.
/// </summary>
public sealed class SoundPlayer
{
    private readonly MediaPlayer _sent = new();
    private readonly MediaPlayer _received = new();

    public SoundPlayer()
    {
        _sent.Open(new Uri(ExtractToTemp("sent.mp3")));
        _received.Open(new Uri(ExtractToTemp("received.mp3")));
        _sent.Volume = 1.0;
        _received.Volume = 1.0;
    }

    public void PlaySent() => Play(_sent);
    public void PlayReceived() => Play(_received);

    private static void Play(MediaPlayer player)
    {
        // MediaPlayer не умеет сам себя перемотать перед повтором — делаем это руками,
        // иначе быстрая серия сообщений молчит после первого звука.
        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    private static string ExtractToTemp(string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), "LanChatSounds", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"{asm.GetName().Name}.Assets.Sounds.{fileName}";

        using var resource = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded-ресурс не найден: {resourceName}");

        // Файл неизменный (имя = содержимое), перезаписывать каждый раз незачем.
        if (!File.Exists(path) || new FileInfo(path).Length != resource.Length)
        {
            using var file = File.Create(path);
            resource.CopyTo(file);
        }

        return path;
    }
}
