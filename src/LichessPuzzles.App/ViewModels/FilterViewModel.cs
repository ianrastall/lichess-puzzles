using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.Interop;
using Jupiter.LichessPuzzles.Messages;
using Microsoft.UI.Dispatching;

namespace Jupiter.LichessPuzzles.App.ViewModels;

public enum ThemeState
{
    NotUsed,
    Include,
    Exclude,
}

public sealed partial class ThemeGroupRow(string name, bool collapsed) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool Collapsed { get; set; } = collapsed;

    public string Chevron => Collapsed ? ((char)0xE76C).ToString() : ((char)0xE70D).ToString();
}

public sealed partial class ThemeRow(CatalogueTheme theme) : ObservableObject
{
    public string Key { get; } = theme.Key;
    public string Name { get; } = theme.Name;
    public string Group { get; } = theme.Group;
    public string Description { get; } = theme.Description.Length > 0 ? theme.Description : theme.Name;
    public int Bit { get; } = (int)theme.Bit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(StateLabel), nameof(AutomationName), nameof(IsIncluded), nameof(IsExcluded))]
    public partial ThemeState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string CountText { get; set; } = "";

    public bool IsIncluded => State == ThemeState.Include;

    public bool IsExcluded => State == ThemeState.Exclude;

    public string Glyph => State switch
    {
        ThemeState.Include => ((char)0xE73E).ToString(),
        ThemeState.Exclude => ((char)0xE738).ToString(),
        _ => "",
    };

    public string StateLabel => State switch
    {
        ThemeState.Include => "Include",
        ThemeState.Exclude => "Exclude",
        _ => "",
    };

    public string AutomationName => $"{Name}, {CountText} puzzles, {(State == ThemeState.NotUsed ? "not used" : StateLabel.ToLowerInvariant())}";

    public void Cycle() => State = State switch
    {
        ThemeState.NotUsed => ThemeState.Include,
        ThemeState.Include => ThemeState.Exclude,
        _ => ThemeState.NotUsed,
    };
}

public sealed record OpeningSuggestion(string Tag, string Name, string Detail)
{
    public override string ToString() => Name;
}

public sealed partial class OpeningToken(string tag, string name) : ObservableObject
{
    public string Tag { get; } = tag;
    public string Name { get; } = name;
    public string RemoveName => $"Remove {Name}";
}

/// <summary>The filter pane. Changes apply after a short pause and update the counts.</summary>
public sealed partial class FilterViewModel : ObservableObject
{
    private readonly CoreHost _host;
    private readonly JsonStore<AppSettings> _settings;
    private readonly UiState _ui;
    private readonly DispatcherQueueTimer _debounce;
    private readonly List<ThemeRow> _themes = [];
    private readonly List<ThemeGroupRow> _groups = [];
    private readonly Dictionary<string, (string Name, string Detail, uint FamilyId, bool IsFamily, uint Id)> _openings = [];
    private Counts? _counts;
    private int _generation;
    private bool _loading;

    public FilterViewModel(CoreHost host, JsonStore<AppSettings> settings, UiState ui, DispatcherQueue dispatcher)
    {
        _host = host;
        _settings = settings;
        _ui = ui;
        _debounce = dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(150);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => _ = ApplyAsync();
        Openings.CollectionChanged += (_, _) => Changed();
    }

    /// <summary>Raised when the match count changes.</summary>
    public event Action? CountsChanged;

    /// <summary>Theme groups and the visible themes of expanded groups, in display order.</summary>
    public ObservableCollection<object> ThemeRows { get; } = [];

    public ObservableCollection<OpeningToken> Openings { get; } = [];

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial string MatchText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasMatches { get; set; } = true;

    public ulong MatchCount => _counts?.Total ?? 0;

    /// <summary>"Ratings in this database: 399 to 3323."</summary>
    [ObservableProperty]
    public partial string RatingRange { get; set; } = "";

    [ObservableProperty]
    public partial double RatingMin { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double RatingMax { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double MaxDeviation { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double MinPopularity { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double MinPlays { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double LengthMin { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double LengthMax { get; set; } = double.NaN;

    /// <summary>0 either, 1 White, 2 Black.</summary>
    [ObservableProperty]
    public partial int SideIndex { get; set; }

    /// <summary>0: included themes match any; 1: all.</summary>
    [ObservableProperty]
    public partial int MatchModeIndex { get; set; }

    [ObservableProperty]
    public partial string ThemeSearch { get; set; } = "";

    [ObservableProperty]
    public partial bool QualityExpanded { get; set; }

    [ObservableProperty]
    public partial string RatingNote { get; set; } = "";

    /// <summary>0: at random; 1: near the player's rating.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNearRating), nameof(TargetText))]
    public partial int ChooseIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    public partial double DifficultyOffset { get; set; }

    [ObservableProperty]
    public partial bool SkipPlayed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    public partial int RatingWindowIndex { get; set; } = 1;

    [ObservableProperty]
    public partial int ScreeningIndex { get; set; }

    private int RatingWindow => RatingWindowIndex switch { 0 => 75, 2 => 300, _ => 150 };
    partial void OnRatingWindowIndexChanged(int value) => Changed();
    partial void OnScreeningIndexChanged(int value) => Changed();

    public bool IsNearRating => ChooseIndex == 1;

    /// <summary>The current player's rating, for the target shown with "Near my rating".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    public partial double PlayerRating { get; set; } = 1500;

    public string TargetText => $"Target {Format.Count((long)Math.Round(PlayerRating + (double.IsNaN(DifficultyOffset) ? 0 : DifficultyOffset)))} ± {RatingWindow}. The range stays fixed; other filters still apply.";

    /// <summary>Preset names, with an empty first entry for "no preset".</summary>
    public ObservableCollection<string> PresetNames { get; } = [];

    /// <summary>The preset the current filters came from; cleared when they are edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetButtonText))]
    public partial string? SelectedPreset { get; set; }

    public string PresetButtonText => SelectedPreset is { } name ? $"Preset: {name}" : "Presets";

    /// <summary>Applies a saved preset, also when it is already the selected one.</summary>
    public void ApplyPreset(string name)
    {
        var preset = _settings.Value.Presets.FirstOrDefault(p => p.Name == name);
        if (preset is null)
        {
            return;
        }
        _loading = true;
        SelectedPreset = name;
        _loading = false;
        ApplyState(preset.Filter.Copy());
    }

    partial void OnChooseIndexChanged(int value) => Changed();
    partial void OnDifficultyOffsetChanged(double value) => Changed();
    partial void OnSkipPlayedChanged(bool value) => Changed();

    private void RefreshPresetNames(string? select)
    {
        _loading = true;
        PresetNames.Clear();
        foreach (var name in _settings.Value.Presets.Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            PresetNames.Add(name);
        }
        SelectedPreset = select is not null && PresetNames.Contains(select) ? select : null;
        _loading = false;
    }

    public bool PresetExists(string name) => _settings.Value.Presets.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Saves the current filters under a name, replacing a preset of the same name.</summary>
    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }
        Save();
        _settings.Value.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        _settings.Value.Presets.Add(new FilterPreset { Name = name, Filter = _settings.Value.Filter.Copy() });
        _settings.Save();
        RefreshPresetNames(name);
    }

    public void RenamePreset(string oldName, string newName)
    {
        newName = newName.Trim();
        var preset = _settings.Value.Presets.FirstOrDefault(p => p.Name == oldName);
        if (preset is null || newName.Length == 0)
        {
            return;
        }
        preset.Name = newName;
        _settings.Save();
        RefreshPresetNames(newName);
    }

    public void DeletePreset(string name)
    {
        _settings.Value.Presets.RemoveAll(p => p.Name == name);
        _settings.Save();
        RefreshPresetNames(null);
    }

    partial void OnRatingMinChanged(double value) => Changed();
    partial void OnRatingMaxChanged(double value) => Changed();
    partial void OnMaxDeviationChanged(double value) => Changed();
    partial void OnMinPopularityChanged(double value) => Changed();
    partial void OnMinPlaysChanged(double value) => Changed();
    partial void OnLengthMinChanged(double value) => Changed();
    partial void OnLengthMaxChanged(double value) => Changed();
    partial void OnSideIndexChanged(int value) => Changed();
    partial void OnMatchModeIndexChanged(int value) => Changed();
    partial void OnThemeSearchChanged(string value) => RebuildRows();
    partial void OnQualityExpandedChanged(bool value) => _ui.QualityExpanded = value;

    /// <summary>Fills the pane from a store's catalogue and the saved filter, then applies it.</summary>
    public void Load(Catalogue catalogue)
    {
        _loading = true;
        _themes.Clear();
        _groups.Clear();
        foreach (var group in catalogue.Groups)
        {
            _groups.Add(new ThemeGroupRow(group, _ui.CollapsedThemeGroups.Contains(group)));
        }
        foreach (var theme in catalogue.Themes)
        {
            var row = new ThemeRow(theme) { CountText = Format.Count(theme.Count) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThemeRow.State))
                {
                    Changed();
                }
            };
            _themes.Add(row);
        }
        _openings.Clear();
        foreach (var family in catalogue.Families)
        {
            _openings[family.Tag] = (family.Name, "Opening family", family.Id, true, family.Id);
        }
        var familyNames = catalogue.Families.ToDictionary(f => f.Id, f => f.Name);
        foreach (var variation in catalogue.Variations)
        {
            var family = familyNames.GetValueOrDefault(variation.Family, "");
            _openings[variation.Tag] = ($"{family}: {variation.Name}", "Variation", variation.Family, false, variation.Id);
        }
        RatingRange = $"Ratings in this database: {catalogue.RatingMin} to {catalogue.RatingMax}.";
        QualityExpanded = _ui.QualityExpanded;
        RefreshPresetNames(null);
        _loading = true;
        ApplyStateCore(_settings.Value.Filter);
        _loading = false;
        IsAvailable = true;
        RebuildRows();
        _ = ApplyAsync();
    }

    /// <summary>Shows a saved filter state and applies it.</summary>
    private void ApplyState(FilterState state)
    {
        _loading = true;
        ApplyStateCore(state);
        _loading = false;
        Changed();
    }

    private void ApplyStateCore(FilterState saved)
    {
        RatingMin = saved.RatingMin ?? double.NaN;
        RatingMax = saved.RatingMax ?? double.NaN;
        MaxDeviation = saved.MaxDeviation ?? double.NaN;
        MinPopularity = saved.MinPopularity ?? double.NaN;
        MinPlays = saved.MinPlays ?? double.NaN;
        LengthMin = saved.LengthMin ?? double.NaN;
        LengthMax = saved.LengthMax ?? double.NaN;
        SideIndex = saved.Side switch { "white" => 1, "black" => 2, _ => 0 };
        MatchModeIndex = saved.IncludedMustAllMatch ? 1 : 0;
        foreach (var row in _themes)
        {
            row.State = saved.ThemesIncluded.Contains(row.Key) ? ThemeState.Include
                : saved.ThemesExcluded.Contains(row.Key) ? ThemeState.Exclude
                : ThemeState.NotUsed;
        }
        Openings.Clear();
        foreach (var tag in saved.Openings.Where(_openings.ContainsKey))
        {
            Openings.Add(new OpeningToken(tag, _openings[tag].Name));
        }
        ChooseIndex = saved.NearRating ? 1 : 0;
        DifficultyOffset = saved.DifficultyOffset;
        RatingWindowIndex = saved.RatingWindow switch { 75 => 0, 300 => 2, _ => 1 };
        ScreeningIndex = Math.Clamp(saved.ExcludeShortMates, 0, 2);
        SkipPlayed = saved.SkipPlayed;
    }

    public void Unload()
    {
        IsAvailable = false;
        MatchText = "";
    }

    public void ToggleGroup(ThemeGroupRow group)
    {
        group.Collapsed = !group.Collapsed;
        _ui.CollapsedThemeGroups.Remove(group.Name);
        if (group.Collapsed)
        {
            _ui.CollapsedThemeGroups.Add(group.Name);
        }
        RebuildRows();
    }

    private void RebuildRows()
    {
        ThemeRows.Clear();
        var search = ThemeSearch.Trim();
        foreach (var group in _groups)
        {
            var members = _themes.Where(t => t.Group == group.Name
                && (search.Length == 0 || t.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || t.Key.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
            if (members.Count == 0)
            {
                continue;
            }
            ThemeRows.Add(group);
            if (!group.Collapsed || search.Length > 0)
            {
                foreach (var member in members)
                {
                    ThemeRows.Add(member);
                }
            }
        }
    }

    public IReadOnlyList<OpeningSuggestion> Suggest(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return [];
        }
        return _openings
            .Where(o => o.Value.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) && Openings.All(t => t.Tag != o.Key))
            .OrderBy(o => o.Value.IsFamily ? 0 : 1)
            .ThenBy(o => o.Value.Name, StringComparer.CurrentCulture)
            .Take(40)
            .Select(o => new OpeningSuggestion(o.Key, o.Value.Name, $"{o.Value.Detail} · {Format.Count(OpeningCount(o.Value.IsFamily, o.Value.Id))} puzzles"))
            .ToList();
    }

    private ulong OpeningCount(bool family, uint id)
    {
        var counts = family ? _counts?.Families : _counts?.Variations;
        return counts is not null && id < counts.Count ? counts[(int)id] : 0;
    }

    public void AddOpening(string tag)
    {
        if (_openings.TryGetValue(tag, out var opening) && Openings.All(t => t.Tag != tag))
        {
            Openings.Add(new OpeningToken(tag, opening.Name));
        }
    }

    [RelayCommand]
    private void RemoveOpening(OpeningToken token) => Openings.Remove(token);

    [RelayCommand]
    private void Reset()
    {
        _loading = true;
        RatingMin = RatingMax = MaxDeviation = MinPopularity = MinPlays = LengthMin = LengthMax = double.NaN;
        SideIndex = 0;
        MatchModeIndex = 0;
        foreach (var row in _themes)
        {
            row.State = ThemeState.NotUsed;
        }
        Openings.Clear();
        ChooseIndex = 1;
        DifficultyOffset = 0;
        RatingWindowIndex = 1;
        ScreeningIndex = 0;
        SkipPlayed = true;
        SelectedPreset = null;
        _loading = false;
        Changed();
    }

    private void Changed()
    {
        if (_loading || !IsAvailable)
        {
            return;
        }
        Save();
        // Edited filters no longer match the preset they came from.
        if (SelectedPreset is { } name && _settings.Value.Presets.FirstOrDefault(p => p.Name == name) is { } preset
            && System.Text.Json.JsonSerializer.Serialize(preset.Filter, PreferencesJson.Default.FilterState)
               != System.Text.Json.JsonSerializer.Serialize(_settings.Value.Filter, PreferencesJson.Default.FilterState))
        {
            _loading = true;
            SelectedPreset = null;
            _loading = false;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private static int? Value(double value) => double.IsNaN(value) ? null : (int)Math.Round(value);

    private void Save()
    {
        var f = _settings.Value.Filter;
        f.RatingMin = Value(RatingMin);
        f.RatingMax = Value(RatingMax);
        f.MaxDeviation = Value(MaxDeviation);
        f.MinPopularity = Value(MinPopularity);
        f.MinPlays = Value(MinPlays);
        f.LengthMin = Value(LengthMin);
        f.LengthMax = Value(LengthMax);
        f.Side = SideIndex switch { 1 => "white", 2 => "black", _ => "either" };
        f.IncludedMustAllMatch = MatchModeIndex == 1;
        f.ThemesIncluded = _themes.Where(t => t.State == ThemeState.Include).Select(t => t.Key).ToList();
        f.ThemesExcluded = _themes.Where(t => t.State == ThemeState.Exclude).Select(t => t.Key).ToList();
        f.Openings = Openings.Select(o => o.Tag).ToList();
        f.NearRating = ChooseIndex == 1;
        f.DifficultyOffset = double.IsNaN(DifficultyOffset) ? 0 : (int)Math.Round(Math.Clamp(DifficultyOffset, -600, 600));
        f.SkipPlayed = SkipPlayed;
        f.RatingWindow = RatingWindow;
        f.ExcludeShortMates = Math.Clamp(ScreeningIndex, 0, 2);
        _settings.Save();
    }

    public Filter ToMessage()
    {
        var f = _settings.Value.Filter;
        var message = new Filter
        {
            RatingMin = (uint)Math.Max(0, f.RatingMin ?? 0),
            RatingMax = (uint)Math.Max(0, f.RatingMax ?? 0),
            MaxDeviation = (uint)Math.Max(0, f.MaxDeviation ?? 0),
            HasMinPopularity = f.MinPopularity.HasValue,
            MinPopularity = f.MinPopularity ?? 0,
            MinPlays = (uint)Math.Max(0, f.MinPlays ?? 0),
            Solver = f.Side switch { "white" => Side.White, "black" => Side.Black, _ => Side.Unspecified },
            MinSolutionMoves = (uint)Math.Max(0, f.LengthMin ?? 0),
            MaxSolutionMoves = (uint)Math.Max(0, f.LengthMax ?? 0),
        };
        (f.IncludedMustAllMatch ? message.ThemesAll : message.ThemesAny).Add(f.ThemesIncluded);
        message.ThemesNone.Add(f.ThemesExcluded);
        message.Openings.Add(f.Openings);
        return message;
    }

    /// <summary>Sends the current filter to the core and shows its counts.</summary>
    public async Task ApplyAsync()
    {
        if (!IsAvailable)
        {
            return;
        }
        var generation = ++_generation;
        var filter = ToMessage();
        Counts? counts;
        try
        {
            var skip = _settings.Value.Filter.SkipPlayed;
            counts = await _host.Run(core => core.SetFilter(filter, skip));
        }
        catch (PuzzleCoreException e)
        {
            if (generation == _generation)
            {
                MatchText = Format.Sentence(e.Message);
                HasMatches = false;
                CountsChanged?.Invoke();
            }
            return;
        }
        if (generation != _generation || counts is null)
        {
            return;
        }
        _counts = counts;
        foreach (var row in _themes)
        {
            row.CountText = row.Bit < counts.Themes.Count ? Format.Count(counts.Themes[row.Bit]) : "0";
        }
        HasMatches = counts.Total > 0;
        MatchText = counts.Total switch
        {
            0 => "No puzzles match these filters",
            1 => "1 matching puzzle",
            _ => $"{Format.Count(counts.Total)} matching puzzles",
        };
        RatingNote = !double.IsNaN(RatingMin) && !double.IsNaN(RatingMax) && RatingMin > RatingMax
            ? "The lower rating is above the upper one, so nothing matches."
            : "";
        OnPropertyChanged(nameof(MatchCount));
        CountsChanged?.Invoke();
    }
}
