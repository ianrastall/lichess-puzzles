using System.Text.Json;

namespace LichessPuzzleDesktop.Services;

/// <summary>User settings, stored as JSON under %LOCALAPPDATA%\LichessPuzzleDesktop.</summary>
public sealed class AppSettings
{
    public string? DatabasePath { get; set; }
    public int RatingMin { get; set; } = 400;
    public int RatingMax { get; set; } = 3500;
    public int PopularityMin { get; set; } = -100;
    public int PlaysMin { get; set; }
    public bool SkipPlayed { get; set; } = true;
    public bool EngineEnabled { get; set; } = true;
    public bool EvalWhileSolving { get; set; }
    public bool PgnFromPlayerMove { get; set; }

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LichessPuzzleDesktop");

    public static string HistoryPath => Path.Combine(DataFolder, "history.csv");
    private static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings fall back to the defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to persist them is not worth interrupting the user.
        }
    }
}
