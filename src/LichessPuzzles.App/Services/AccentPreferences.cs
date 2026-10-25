using Jupiter.UI;

namespace Jupiter.LichessPuzzles.App.Services;

/// <summary>Keeps the Jupiter accent choice in settings.json.</summary>
public sealed class AccentPreferences(JsonStore<AppSettings> settings) : IAccentPreferences
{
    // Versions before style standard 1.0 stored this brass color as the custom accent's default.
    private const string RetiredDefault = "#C8A15A";

    public bool UseCustomAccent
    {
        get => settings.Value.UseCustomAccent;
        set => settings.Value.UseCustomAccent = value;
    }

    /// <summary>A custom color never chosen starts from the Windows accent (standard section 3.3).</summary>
    public string? CustomAccent
    {
        get => !settings.Value.UseCustomAccent && settings.Value.CustomAccent == RetiredDefault ? null : settings.Value.CustomAccent;
        set => settings.Value.CustomAccent = value;
    }

    public void Save() => settings.Save();
}

/// <summary>Keeps the shared dark, light or Windows theme choice in settings.json.</summary>
public sealed class ThemePreferences(JsonStore<AppSettings> settings) : IThemePreferences
{
    public string? Theme
    {
        get => settings.Value.Theme;
        set => settings.Value.Theme = value;
    }

    public void Save() => settings.Save();
}
