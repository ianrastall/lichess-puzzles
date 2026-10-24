using Jupiter.LichessPuzzles.App.Controls;
using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.App.ViewModels;
using Jupiter.LichessPuzzles.App.Views;
using Jupiter.LichessPuzzles.Messages;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Jupiter.LichessPuzzles.App;

public sealed partial class MainWindow : Window, IBoardView
{
    private const double NarrowWidth = 1180;

    private readonly App _app;
    private readonly FilterPane _filterPane;
    private readonly PuzzlePane _puzzlePane;
    private bool _narrow;
    private bool _overlayOpen;
    private SettingsWindow? _settingsWindow;
    private bool _closePending;

    /// <summary>Set once the core has been released, so the window may close.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(App app)
    {
        _app = app;
        Puzzle = app.Puzzle;
        Status = app.Status;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Title = "Lichess Puzzles";

        _filterPane = new FilterPane(app.Filter);
        _filterPane.CloseRequested += () => SetFiltersOpen(false);
        _puzzlePane = new PuzzlePane(app.Puzzle, app.Host);
        PuzzleHost.Child = _puzzlePane;

        var ui = app.Ui.Value;
        FilterColumn.Width = new GridLength(Math.Clamp(ui.FilterPaneWidth, 260, 480));
        PuzzleColumn.Width = new GridLength(Math.Clamp(ui.PuzzlePaneWidth, 300, 520));
        FilterOverlay.Width = FilterColumn.Width.Value;
        FiltersItem.IsChecked = ui.FilterPaneVisible;

        SettingsItem.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = (VirtualKey)188, Modifiers = VirtualKeyModifiers.Control });

        Board.MoveRequested += uci => _ = Puzzle.MoveAsync(uci);
        Board.NavigateRequested += step => _ = Puzzle.StepAsync(step);
        app.Puzzle.AttachBoard(this);
        ApplyBoardSettings();

        RestoreBounds();
        Workspace.SizeChanged += (_, _) => UpdatePaneMode();
        AppWindow.Closing += OnClosing;
        UpdatePaneMode();
        UpdateCommands();
    }

    public PuzzleViewModel Puzzle { get; }

    public StatusViewModel Status { get; }

    public FrameworkElement RootElement => Root;

    public Visibility ShowIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public string SeverityGlyph(InfoBarSeverity severity) => severity switch
    {
        InfoBarSeverity.Success => ((char)0xE73E).ToString(),
        InfoBarSeverity.Warning => ((char)0xE7BA).ToString(),
        InfoBarSeverity.Error => ((char)0xEA39).ToString(),
        _ => ((char)0xE946).ToString(),
    };

    public Brush SeverityBrush(InfoBarSeverity severity) => (Brush)Application.Current.Resources[severity switch
    {
        InfoBarSeverity.Success => "JupiterSuccessBrush",
        InfoBarSeverity.Warning => "JupiterWarningBrush",
        InfoBarSeverity.Error => "JupiterErrorBrush",
        _ => "JupiterInfoBrush",
    }];

    // IBoardView ----------------------------------------------------------------------------------

    public bool WhiteAtBottom => Board.WhiteAtBottom;

    public void Show(SessionView view, bool animate)
    {
        var interactive = view.State == SessionState.Solving && view.View == view.Played;
        Board.SetPosition(view.Board, view.LastFrom, view.LastTo, view.CheckSquare, view.HintSquare, view.LegalMoves, interactive, animate);
    }

    public void ShowAttempt(int from, int to) => Board.ShowAttempt(from, to);

    public void ShowMarker(int square, BoardMarker marker) => Board.ShowMarker(square, marker);

    public void SetOrientation(bool whiteAtBottom) => Board.SetOrientation(whiteAtBottom);

    public void Announce(string text) => Board.Announce(text);

    // Settings and appearance ---------------------------------------------------------------------

    public void ApplyBoardSettings()
    {
        var settings = _app.Settings.Value;
        var (light, dark) = BoardThemes.Colors(settings.BoardTheme);
        Board.SetColors(light, dark);
        Board.ShowCoordinates = settings.ShowCoordinates;
        CoordinatesItem.IsChecked = settings.ShowCoordinates;
        var animations = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        Board.AnimationDuration = !animations || settings.MoveAnimation == "Off" ? TimeSpan.Zero
            : settings.MoveAnimation == "Fast" ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromMilliseconds(180);
        Puzzle.Refresh();
    }

    public void OnAccentChanged() => Board.RefreshAccent();

    /// <summary>Enables menu items to match what is possible now.</summary>
    public void UpdateCommands()
    {
        var hasStore = Status.HasStore;
        var running = Status.IsRunning;
        var view = Puzzle.View;
        DownloadItem.Text = Status.Status?.UpdateAvailable is not null ? "Download update…" : "Download puzzles…";
        DownloadItem.IsEnabled = !running;
        CheckUpdateItem.IsEnabled = !running;
        ImportItem.IsEnabled = !running;
        NextItem.IsEnabled = Puzzle.CanNext;
        HintItem.IsEnabled = Puzzle.CanHint;
        SolutionItem.IsEnabled = Puzzle.CanShowSolution;
        RetryItem.IsEnabled = Puzzle.CanRetry;
        GoToItem.IsEnabled = hasStore;
        FirstItem.IsEnabled = BackItem.IsEnabled = Puzzle.CanGoBack;
        ForwardItem.IsEnabled = LatestItem.IsEnabled = Puzzle.CanGoForward;
        CopyFenItem.IsEnabled = CopyLinkItem.IsEnabled = OpenLichessItem.IsEnabled = view is not null;
        OpenGameItem.IsEnabled = view is not null && Puzzle.GameUrl.Length > 0;
    }

    // Panes ---------------------------------------------------------------------------------------

    private void UpdatePaneMode()
    {
        var narrow = Workspace.ActualWidth > 0 && Workspace.ActualWidth < NarrowWidth;
        if (narrow != _narrow || (FilterHost.Child is null && FilterOverlay.Child is null))
        {
            _narrow = narrow;
            _overlayOpen = false;
            PlaceFilterPane();
        }
    }

    private void PlaceFilterPane()
    {
        FilterHost.Child = null;
        FilterOverlay.Child = null;
        var visible = _app.Ui.Value.FilterPaneVisible;
        if (_narrow)
        {
            FilterColumn.Width = new GridLength(0);
            FilterSplitter.Visibility = Visibility.Collapsed;
            _filterPane.IsOverlay = true;
            FilterOverlay.Child = _filterPane;
            FilterOverlay.Visibility = ShowIf(_overlayOpen);
            FiltersItem.IsChecked = _overlayOpen;
        }
        else
        {
            _filterPane.IsOverlay = false;
            FilterOverlay.Visibility = Visibility.Collapsed;
            FilterHost.Child = _filterPane;
            FilterColumn.Width = new GridLength(visible ? Math.Clamp(_app.Ui.Value.FilterPaneWidth, 260, 480) : 0);
            FilterSplitter.Visibility = ShowIf(visible);
            FiltersItem.IsChecked = visible;
        }
    }

    private void SetFiltersOpen(bool open)
    {
        if (_narrow)
        {
            _overlayOpen = open;
            FilterOverlay.Visibility = ShowIf(open);
            FiltersItem.IsChecked = open;
        }
        else
        {
            if (!open)
            {
                SaveLayout();
            }
            _app.Ui.Value.FilterPaneVisible = open;
            PlaceFilterPane();
        }
        if (open)
        {
            _filterPane.FocusFirst();
        }
        else
        {
            Board.Focus(FocusState.Programmatic);
        }
    }

    private void OnToggleFilters(object sender, RoutedEventArgs e)
    {
        var open = _narrow ? !_overlayOpen : !_app.Ui.Value.FilterPaneVisible;
        SetFiltersOpen(open);
    }

    // Keyboard ------------------------------------------------------------------------------------

    private static bool IsTextInput(object? element) => element is TextBox or AutoSuggestBox or NumberBox or PasswordBox or RichEditBox
        || (element is FrameworkElement fe && FindParent<NumberBox>(fe) is not null)
        || (element is FrameworkElement f2 && FindParent<AutoSuggestBox>(f2) is not null);

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(child); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
            {
                return match;
            }
        }
        return null;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.F6)
        {
            CycleRegion(shift ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Escape && _narrow && _overlayOpen)
        {
            SetFiltersOpen(false);
            e.Handled = true;
            return;
        }
        if (ctrl || IsTextInput(focused))
        {
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Left:
                _ = Puzzle.StepAsync(-1);
                break;
            case VirtualKey.Right:
                _ = Puzzle.StepAsync(1);
                break;
            case VirtualKey.Home:
                _ = Puzzle.StepAsync(int.MinValue);
                break;
            case VirtualKey.End:
                _ = Puzzle.StepAsync(int.MaxValue);
                break;
            case VirtualKey.F:
                Puzzle.Flip();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private int _region;

    /// <summary>F6: filter pane, board, puzzle pane, status bar.</summary>
    private void CycleRegion(int step)
    {
        for (var tries = 0; tries < 4; tries++)
        {
            _region = (_region + step + 4) % 4;
            var moved = _region switch
            {
                0 when _filterPane.Parent is not null && (_narrow ? _overlayOpen : _app.Ui.Value.FilterPaneVisible) => Try(_filterPane.FocusFirst),
                1 => Board.Focus(FocusState.Keyboard),
                2 => Try(_puzzlePane.FocusFirst),
                3 => DatabaseButton.Focus(FocusState.Keyboard),
                _ => false,
            };
            if (moved)
            {
                return;
            }
        }

        static bool Try(Action focus)
        {
            focus();
            return true;
        }
    }

    // Menu commands -------------------------------------------------------------------------------

    private async void OnDownload(object sender, RoutedEventArgs e) =>
        await _app.Host.Try(c => c.StartDownload(_app.Settings.Value.KeepDownload), "The download could not start");

    private async void OnCheckUpdate(object sender, RoutedEventArgs e) =>
        await _app.Host.Try(c => c.CheckForUpdate(), "Checking for an update failed");

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.List };
        picker.FileTypeFilter.Add(".zst");
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await _app.Host.Try(c => c.StartBuild(file.Path, keepDownload: true), "The import could not start");
        }
    }

    private async void OnEmptyPrimary(object sender, RoutedEventArgs e)
    {
        if (Status.EmptyPrimaryText == "Rebuild store")
        {
            await _app.Host.Try(c => c.StartBuild("", _app.Settings.Value.KeepDownload), "The rebuild could not start");
        }
        else
        {
            await _app.Host.Try(c => c.StartDownload(_app.Settings.Value.KeepDownload), "The download could not start");
        }
    }

    private async void OnPause(object sender, RoutedEventArgs e) => await _app.Host.Try(c => c.PauseDownload(), "Pausing failed");

    private async void OnCancelOperation(object sender, RoutedEventArgs e) => await _app.Host.Try(c => c.CancelOperation(), "Cancelling failed");

    private async void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        var keep = _app.Settings.Value.KeepDownload;
        var action = Status.NoticeAction;
        Status.DismissNotice();
        switch (action)
        {
            case EventAction.RetryDownload:
                await _app.Host.Try(c => c.StartDownload(keep), "The download could not start");
                break;
            case EventAction.RetryBuild:
                await _app.Host.Try(c => c.StartBuild("", keep), "The build could not start");
                break;
            case EventAction.RetryCheck:
                await _app.Host.Try(c => c.CheckForUpdate(), "Checking for an update failed");
                break;
            case EventAction.SwitchStore:
                await _app.Host.Try(c => c.SwitchStore(), "Switching to the new puzzles failed");
                break;
        }
    }

    private void OnDismissNotice(object sender, RoutedEventArgs e) => Status.DismissNotice();

    private async void OnDatabaseDetails(object sender, RoutedEventArgs e) => await Dialogs.ShowDatabaseAsync(Root.XamlRoot, _app, this);

    public async Task StartRebuildAsync() =>
        await _app.Host.Try(c => c.StartBuild("", _app.Settings.Value.KeepDownload), "The rebuild could not start");

    public void StartImport() => OnImport(this, new RoutedEventArgs());

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_app, this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private async void OnExit(object sender, RoutedEventArgs e) => await CloseAsync();

    private void OnCopyFen(object sender, RoutedEventArgs e) => Copy(Puzzle.Fen);

    private void OnCopyLink(object sender, RoutedEventArgs e) => Copy(Puzzle.PuzzleLink);

    private static void Copy(string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void OnFlip(object sender, RoutedEventArgs e) => Puzzle.Flip();

    private void OnToggleCoordinates(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.ShowCoordinates = CoordinatesItem.IsChecked;
        _app.Settings.Save();
        ApplyBoardSettings();
    }

    private async void OnNext(object sender, RoutedEventArgs e) => await Puzzle.NextAsync();

    private async void OnHint(object sender, RoutedEventArgs e) => await Puzzle.HintAsync();

    private async void OnShowSolution(object sender, RoutedEventArgs e) => await Puzzle.ShowSolutionAsync();

    private async void OnRetry(object sender, RoutedEventArgs e) => await Puzzle.RetryAsync();

    private async void OnGoTo(object sender, RoutedEventArgs e) => await Dialogs.ShowGoToPuzzleAsync(Root.XamlRoot, Puzzle);

    private async void OnFirst(object sender, RoutedEventArgs e) => await Puzzle.StepAsync(int.MinValue);

    private async void OnBack(object sender, RoutedEventArgs e) => await Puzzle.StepAsync(-1);

    private async void OnForward(object sender, RoutedEventArgs e) => await Puzzle.StepAsync(1);

    private async void OnLatest(object sender, RoutedEventArgs e) => await Puzzle.StepAsync(int.MaxValue);

    private void OnOpenLichess(object sender, RoutedEventArgs e) => _app.Host.OpenLink(Puzzle.PuzzleLink);

    private void OnOpenGame(object sender, RoutedEventArgs e) => _app.Host.OpenLink(Puzzle.GameUrl);

    private async void OnShortcuts(object sender, RoutedEventArgs e) => await Dialogs.ShowShortcutsAsync(Root.XamlRoot);

    private async void OnAbout(object sender, RoutedEventArgs e) => await Dialogs.ShowAboutAsync(Root.XamlRoot);

    // Window ----------------------------------------------------------------------------------------

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p)
        {
            p.Restore();
        }
        AppWindow.Show();
        Activate();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (AllowClose)
        {
            return;
        }
        args.Cancel = true;
        await CloseAsync();
    }

    /// <summary>Asks before stopping a running download or build, then exits.</summary>
    private async Task CloseAsync()
    {
        if (_closePending)
        {
            return;
        }
        _closePending = true;
        try
        {
            if (Status.IsRunning && Status.RunningKind != OperationKind.CheckUpdate)
            {
                var download = Status.RunningKind == OperationKind.Download;
                var confirmed = await Dialogs.ConfirmAsync(Root.XamlRoot,
                    download ? "Pause the download and exit?" : "Cancel building the puzzle store and exit?",
                    download ? "What has been downloaded is kept, and the download resumes from there next time."
                             : "The installed puzzles are unchanged; the store can be built again later.",
                    download ? "Pause and exit" : "Cancel and exit");
                if (!confirmed)
                {
                    return;
                }
            }
            _settingsWindow?.Close();
            await _app.ExitAsync();
        }
        finally
        {
            _closePending = false;
        }
    }

    private void RestoreBounds()
    {
        var ui = _app.Ui.Value;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Win32Dpi.Scale(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
        var width = Math.Min(ui.WindowWidth > 0 ? ui.WindowWidth : (int)(1280 * scale), area.Width);
        var height = Math.Min(ui.WindowHeight > 0 ? ui.WindowHeight : (int)(840 * scale), area.Height);
        var saved = new RectInt32(ui.WindowX, ui.WindowY, width, height);
        var visible = ui.WindowX != int.MinValue
            && saved.X >= area.X - 32 && saved.Y >= area.Y - 8
            && saved.X + 64 <= area.X + area.Width && saved.Y + 64 <= area.Y + area.Height;
        AppWindow.MoveAndResize(visible ? saved : new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(960 * scale);
            presenter.PreferredMinimumHeight = (int)(640 * scale);
            if (ui.WindowMaximized)
            {
                presenter.Maximize();
            }
        }
    }

    /// <summary>Records window bounds and pane widths.</summary>
    public void SaveLayout()
    {
        var ui = _app.Ui.Value;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            ui.WindowMaximized = presenter.State == OverlappedPresenterState.Maximized;
            if (presenter.State == OverlappedPresenterState.Restored)
            {
                ui.WindowX = AppWindow.Position.X;
                ui.WindowY = AppWindow.Position.Y;
                ui.WindowWidth = AppWindow.Size.Width;
                ui.WindowHeight = AppWindow.Size.Height;
            }
        }
        if (!_narrow && ui.FilterPaneVisible && FilterColumn.ActualWidth > 0)
        {
            ui.FilterPaneWidth = FilterColumn.ActualWidth;
        }
        if (PuzzleColumn.ActualWidth > 0)
        {
            ui.PuzzlePaneWidth = PuzzleColumn.ActualWidth;
        }
    }
}

internal static partial class Win32Dpi
{
    /// <summary>The display's scale factor; a new window reports 96 DPI until it is shown.</summary>
    public static double Scale(DisplayArea area)
    {
        var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
    }

    [System.Runtime.InteropServices.LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
