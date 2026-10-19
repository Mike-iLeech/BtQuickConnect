using System.Text.Json;
using Microsoft.Win32;

namespace BtQuickConnect;

internal sealed class Settings
{
    public string? Address { get; set; }
    public string? Name { get; set; }
    public HotkeyBinding? Hotkey { get; set; }
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Версия, которая однажды не запустилась: автоматически её больше не ставим.</summary>
    public string? SkippedUpdate { get; set; }

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BtQuickConnect");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* битый файл — начинаем с чистых настроек */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        // через временный файл: оборванная запись (убитый процесс, сбой питания) не портит настройки
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BtQuickConnect";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(k?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(ValueName, Command);
            else k.DeleteValue(ValueName, false);
        }
    }
}
