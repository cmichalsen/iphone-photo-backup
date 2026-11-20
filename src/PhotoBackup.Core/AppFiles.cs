using System.Text.Json;

namespace PhotoBackup.Core;

public sealed class Settings
{
    public string Destination { get; set; } = @"\\10.0.0.41\iphone";
    public bool RehashBeforeDelete { get; set; }

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppFiles.SettingsPath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppFiles.Folder);
        File.WriteAllText(AppFiles.SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class AppFiles
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoBackup");
    public static string SettingsPath => Path.Combine(Folder, "settings.json");
    public static string StatePath => Path.Combine(Folder, "state.jsonl");
    public static string LogFolder => Path.Combine(Folder, "logs");
    public static string TodayLogPath => Path.Combine(LogFolder, $"{DateTime.Now:yyyy-MM-dd}.log");

    private static readonly Lock LogGate = new();

    public static void AppendLog(string message)
    {
        lock (LogGate)
        {
            Directory.CreateDirectory(LogFolder);
            File.AppendAllText(TodayLogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
    }
}
