using System.IO;
using System.Text.Json;

namespace Lichess_Puzzles;

public enum BoardThemeOption
{
    Brown,
    Blue,
    Green,
    Purple,
    Red
}

public enum SanDisplayOption
{
    Symbols,
    Letters
}

public class AppSettings
{
    public BoardThemeOption BoardTheme { get; set; } = BoardThemeOption.Brown;
    public SanDisplayOption SanDisplay { get; set; } = SanDisplayOption.Symbols;
    public Guid SelectedUserId { get; set; } = Guid.Empty;
}

public static class AppSettingsService
{
    private const string SettingsFileName = "settings.json";

    public static AppSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return new AppSettings();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Persisting settings should never crash the app
        }
    }

    private static string GetSettingsPath()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LichessPuzzles");

        Directory.CreateDirectory(appDataPath);
        return Path.Combine(appDataPath, SettingsFileName);
    }
}
