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
    public int PlayerId { get; set; }
    public bool Rated { get; set; } = true;
    public bool MatchRating { get; set; } = true;
    public int Difficulty { get; set; } = 2;

    /// <summary>Where settings, players and histories live. LPD_DATA_DIR overrides it (portable use, testing).</summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("LPD_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LichessPuzzleDesktop");

    public static string PlayersPath => Path.Combine(DataFolder, "players.csv");
    public static string HistoryPathFor(int playerId) => Path.Combine(DataFolder, $"history-{playerId}.csv");

    /// <summary>Hands the single history file of versions before players existed to the first player.</summary>
    public static void AdoptLegacyHistory(int playerId)
    {
        string legacy = Path.Combine(DataFolder, "history.csv");
        try
        {
            if (File.Exists(legacy) && !File.Exists(HistoryPathFor(playerId))) File.Move(legacy, HistoryPathFor(playerId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The old history stays where it is; the player simply starts with an empty one.
        }
    }

    public static void DeleteHistory(int playerId)
    {
        try
        {
            File.Delete(HistoryPathFor(playerId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover file is harmless; it is overwritten if the id is ever handed out again.
        }
    }

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
