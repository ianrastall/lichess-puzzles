using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jupiter.LichessPuzzles.App.Services;

/// <summary>Settings chosen in the Settings window, and the current filters (settings.json).</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // Appearance
    public bool UseCustomAccent { get; set; }
    public string CustomAccent { get; set; } = "#C8A15A";
    public string BoardTheme { get; set; } = BoardThemes.Brown;
    public bool ShowCoordinates { get; set; } = true;
    public string MoveAnimation { get; set; } = "Normal";

    // Puzzles
    public bool ShowThemesWhileSolving { get; set; }
    public int OpponentDelayMs { get; set; } = 600;
    public bool NextAfterCleanSolve { get; set; }

    // Storage
    public bool KeepDownload { get; set; } = true;
    public bool CheckForUpdatesAtStart { get; set; } = true;

    public FilterState Filter { get; set; } = new();
}

/// <summary>The filter pane's values. Empty bounds are null.</summary>
public sealed class FilterState
{
    public int? RatingMin { get; set; }
    public int? RatingMax { get; set; }
    public int? MaxDeviation { get; set; }
    public int? MinPopularity { get; set; }
    public int? MinPlays { get; set; }
    public List<string> ThemesIncluded { get; set; } = [];
    public List<string> ThemesExcluded { get; set; } = [];
    public bool IncludedMustAllMatch { get; set; }
    public List<string> Openings { get; set; } = [];
    /// <summary>"either", "white" or "black".</summary>
    public string Side { get; set; } = "either";
    public int? LengthMin { get; set; }
    public int? LengthMax { get; set; }
}

/// <summary>Window and pane layout and the puzzle in progress (ui.json).</summary>
public sealed class UiState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double FilterPaneWidth { get; set; } = 300;
    public double PuzzlePaneWidth { get; set; } = 340;
    public bool FilterPaneVisible { get; set; } = true;
    public List<string> CollapsedThemeGroups { get; set; } = [];
    public bool QualityExpanded { get; set; }

    /// <summary>The puzzle on the board, restored at the next launch while unfinished.</summary>
    public string CurrentPuzzleId { get; set; } = "";
    public List<string> CurrentPuzzleSolverMoves { get; set; } = [];
    public bool CurrentPuzzleFinished { get; set; }
}

public static class BoardThemes
{
    public const string Brown = "Brown";
    public const string Blue = "Blue";
    public const string Green = "Green";
    public const string Grey = "Grey";

    public static readonly string[] All = [Brown, Blue, Green, Grey];

    /// <summary>Light and dark square colors.</summary>
    public static (Windows.UI.Color Light, Windows.UI.Color Dark) Colors(string theme) => theme switch
    {
        Blue => (Rgb(0xDC, 0xE4, 0xEA), Rgb(0x7F, 0x9A, 0xAE)),
        Green => (Rgb(0xE6, 0xE9, 0xC8), Rgb(0x7E, 0x9A, 0x61)),
        Grey => (Rgb(0xD6, 0xD3, 0xCE), Rgb(0x9A, 0x95, 0x8E)),
        _ => (Rgb(0xEA, 0xD7, 0xB7), Rgb(0xB0, 0x8A, 0x62)),
    };

    private static Windows.UI.Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
}

/// <summary>Loads and saves a JSON file atomically; a file from a newer version is left alone.</summary>
public sealed class JsonStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> _type;
    private readonly bool _readOnly;

    public T Value { get; }

    public JsonStore(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, Func<T, int> version, int currentVersion)
    {
        _path = path;
        _type = type;
        T? value = null;
        try
        {
            if (File.Exists(path))
            {
                value = JsonSerializer.Deserialize(File.ReadAllText(path), type);
                if (value is not null && version(value) > currentVersion)
                {
                    // Written by a newer version: use defaults and keep the file.
                    value = null;
                    _readOnly = true;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            value = null;
        }
        Value = value ?? new T();
    }

    public void Save()
    {
        if (_readOnly)
        {
            return;
        }
        try
        {
            var tmp = _path + ".partial";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Value, _type));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences are not worth interrupting work for.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(UiState))]
internal sealed partial class PreferencesJson : JsonSerializerContext;
