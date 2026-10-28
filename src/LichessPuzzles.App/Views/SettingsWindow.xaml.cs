using Jupiter.LichessPuzzles.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Jupiter.LichessPuzzles.App.Views;

/// <summary>Settings apply as they change.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly MainWindow _main;
    private readonly bool _loading = true;

    private static readonly double[] DelayChoices = [0.3, 0.5, 0.6, 0.8, 1, 1.5, 2];
    private static readonly double[] EvaluationChoices = [0.5, 1, 2, 3, 5];

    private static void FillSeconds(ComboBox box, double[] choices, double current)
    {
        foreach (var seconds in choices)
        {
            box.Items.Add($"{seconds.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} s");
        }
        var nearest = choices.Select((v, i) => (Distance: Math.Abs(v - current), i)).MinBy(t => t.Distance).i;
        box.SelectedIndex = nearest;
    }

    public SettingsWindow(App app, MainWindow main)
    {
        _app = app;
        _main = main;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Title = "Settings - Lichess Puzzles";
        // Standard 6.1: settings windows are 720 × 720, minimum 560 × 480.
        var scale = Win32Dpi.Scale(Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(main.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary));
        var parent = main.AppWindow.Position;
        AppWindow.MoveAndResize(new RectInt32(parent.X + 80, parent.Y + 60, (int)(720 * scale), (int)(720 * scale)));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(480 * scale);
        }
        _app.Accent.Attach(Root);
        _app.Theme.Attach(this);
        ThemeSetting.Service = _app.Theme;
        AccentSetting.Service = _app.Accent;

        var settings = _app.Settings.Value;

        foreach (var theme in BoardThemes.All)
        {
            BoardThemeBox.Items.Add(theme);
        }
        BoardThemeBox.SelectedItem = BoardThemes.All.Contains(settings.BoardTheme) ? settings.BoardTheme : BoardThemes.Brown;
        CoordinatesSwitch.IsOn = settings.ShowCoordinates;
        AnimationChoice.SelectedIndex = settings.MoveAnimation switch { "Off" => 0, "Fast" => 1, _ => 2 };
        ThemesSwitch.IsOn = settings.ShowThemesWhileSolving;
        FillSeconds(DelayBox, DelayChoices, settings.OpponentDelayMs / 1000.0);
        AutoNextSwitch.IsOn = settings.NextAfterCleanSolve;
        DataFolderText.Text = App.DataDirectory;
        EvaluationSwitch.IsOn = settings.EvaluationEnabled;
        EvaluationSolvingSwitch.IsOn = settings.EvaluationWhileSolving;
        EvaluationSolvingSwitch.IsEnabled = settings.EvaluationEnabled;
        FillSeconds(EvaluationTimeBox, EvaluationChoices, settings.EvaluationSeconds);
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

    private void OnDelayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DelayBox.SelectedIndex >= 0)
        {
            _app.Settings.Value.OpponentDelayMs = (int)Math.Round(DelayChoices[DelayBox.SelectedIndex] * 1000);
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

    private void OnEvaluationTimeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EvaluationTimeBox.SelectedIndex >= 0)
        {
            _app.Settings.Value.EvaluationSeconds = EvaluationChoices[EvaluationTimeBox.SelectedIndex];
            Save();
        }
    }

    private void OnShowFolder(object sender, RoutedEventArgs e) => _app.Host.OpenFolder(App.DataDirectory);
}
