using Jupiter.LichessPuzzles.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Jupiter.LichessPuzzles.App.Views;

/// <summary>Settings apply as they change.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly MainWindow _main;
    private readonly bool _loading = true;

    public SettingsWindow(App app, MainWindow main)
    {
        _app = app;
        _main = main;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Title = "Settings · Lichess Puzzles";
        var scale = Win32Dpi.Scale(Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(main.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary));
        var parent = main.AppWindow.Position;
        AppWindow.MoveAndResize(new RectInt32(parent.X + 80, parent.Y + 60, (int)(620 * scale), (int)(760 * scale)));

        var settings = _app.Settings.Value;
        AccentChoice.SelectedIndex = _app.Accent.UseCustomAccent ? 1 : 0;
        UpdateSwatch();
        _app.Accent.Changed += UpdateSwatch;
        Closed += (_, _) => _app.Accent.Changed -= UpdateSwatch;

        foreach (var theme in BoardThemes.All)
        {
            BoardThemeBox.Items.Add(theme);
        }
        BoardThemeBox.SelectedItem = BoardThemes.All.Contains(settings.BoardTheme) ? settings.BoardTheme : BoardThemes.Brown;
        CoordinatesSwitch.IsOn = settings.ShowCoordinates;
        AnimationChoice.SelectedIndex = settings.MoveAnimation switch { "Off" => 0, "Fast" => 1, _ => 2 };
        ThemesSwitch.IsOn = settings.ShowThemesWhileSolving;
        DelayBox.Value = Math.Clamp(settings.OpponentDelayMs / 1000.0, 0.3, 2);
        AutoNextSwitch.IsOn = settings.NextAfterCleanSolve;
        DataFolderText.Text = App.DataDirectory;
        EvaluationSwitch.IsOn = settings.EvaluationEnabled;
        EvaluationSolvingSwitch.IsOn = settings.EvaluationWhileSolving;
        EvaluationSolvingSwitch.IsEnabled = settings.EvaluationEnabled;
        EvaluationTimeBox.Value = Math.Clamp(settings.EvaluationSeconds, 0.5, 5);
        KeepDownloadSwitch.IsOn = settings.KeepDownload;
        CheckUpdatesSwitch.IsOn = settings.CheckForUpdatesAtStart;
        _loading = false;
    }

    private void Save()
    {
        if (_loading)
        {
            return;
        }
        _app.Settings.Save();
        _main.ApplyBoardSettings();
    }

    private void UpdateSwatch()
    {
        AccentSwatch.Background = new SolidColorBrush(_app.Accent.Current);
        AccentPicker.Color = _app.Accent.CustomColor;
        AccentSwatchButton.IsEnabled = AccentChoice.SelectedIndex == 1;
    }

    private void OnAccentChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        if (AccentChoice.SelectedIndex == 0)
        {
            _app.Accent.UseWindows();
        }
        else
        {
            _app.Accent.UseCustom(_app.Accent.CustomColor);
        }
        UpdateSwatch();
    }

    private void OnApplyCustomAccent(object sender, RoutedEventArgs e)
    {
        _app.Accent.UseCustom(AccentPicker.Color);
        AccentChoice.SelectedIndex = 1;
        AccentSwatchButton.Flyout?.Hide();
    }

    private void OnResetAccent(object sender, RoutedEventArgs e)
    {
        _app.Accent.UseWindows();
        AccentChoice.SelectedIndex = 0;
    }

    private void OnBoardThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BoardThemeBox.SelectedItem is string theme)
        {
            _app.Settings.Value.BoardTheme = theme;
            Save();
        }
    }

    private void OnCoordinatesToggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.ShowCoordinates = CoordinatesSwitch.IsOn;
        Save();
    }

    private void OnAnimationChanged(object sender, SelectionChangedEventArgs e)
    {
        _app.Settings.Value.MoveAnimation = AnimationChoice.SelectedIndex switch { 0 => "Off", 1 => "Fast", _ => "Normal" };
        Save();
    }

    private void OnThemesToggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.ShowThemesWhileSolving = ThemesSwitch.IsOn;
        Save();
    }

    private void OnDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue))
        {
            _app.Settings.Value.OpponentDelayMs = (int)Math.Round(Math.Clamp(args.NewValue, 0.3, 2) * 1000);
            Save();
        }
    }

    private void OnAutoNextToggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.NextAfterCleanSolve = AutoNextSwitch.IsOn;
        Save();
    }

    private void OnEvaluationToggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.EvaluationEnabled = EvaluationSwitch.IsOn;
        EvaluationSolvingSwitch.IsEnabled = EvaluationSwitch.IsOn;
        if (!_loading)
        {
            _app.Settings.Save();
            _main.ApplyEvaluationSettings();
        }
    }

    private void OnEvaluationSolvingToggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.EvaluationWhileSolving = EvaluationSolvingSwitch.IsOn;
        if (!_loading)
        {
            _app.Settings.Save();
            _main.ApplyEvaluationSettings();
        }
    }

    private void OnEvaluationTimeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue))
        {
            _app.Settings.Value.EvaluationSeconds = Math.Clamp(args.NewValue, 0.5, 5);
            Save();
        }
    }

    private void OnKeepDownloadToggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.KeepDownload = KeepDownloadSwitch.IsOn;
        Save();
    }

    private void OnCheckUpdatesToggled(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.CheckForUpdatesAtStart = CheckUpdatesSwitch.IsOn;
        Save();
    }

    private void OnShowFolder(object sender, RoutedEventArgs e) => _app.Host.OpenFolder(App.DataDirectory);
}
