using Jupiter.LichessPuzzles.App.Controls;
using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.App.ViewModels;
using Jupiter.LichessPuzzles.App.Views;
using Jupiter.LichessPuzzles.Messages;
using Player = Jupiter.LichessPuzzles.Messages.Player;
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
    private string _playersMenuKey = "";

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
        _puzzlePane = new PuzzlePane(app.Puzzle, app.Player, app.History, app.Evaluation, app.Host);
        _puzzlePane.PlayerCommandRequested += command => _ = PlayerCommandAsync(command);
        PuzzleHost.Child = _puzzlePane;
        app.Player.Changed += RebuildPlayersMenu;
        app.Evaluation.PropertyChanged += (_, _) => UpdateEvaluationBar();

        var ui = app.Ui.Value;
        FilterColumn.Width = new GridLength(Math.Clamp(ui.FilterPaneWidth, 240, 480));
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

    public bool ShowingHistory => _puzzlePane.ShowingHistory;

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

    public Brush SeverityBrush(InfoBarSeverity severity) => Jupiter.UI.JupiterTheme.Brush(severity switch
    {
        InfoBarSeverity.Success => "JupiterSuccessBrush",
        InfoBarSeverity.Warning => "JupiterWarningBrush",
        InfoBarSeverity.Error => "JupiterErrorBrush",
        _ => "JupiterInfoBrush",
    }, _app.Theme.Resolved);

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
        EvaluationItem.IsChecked = settings.EvaluationEnabled;
        EvaluationSolvingItem.IsChecked = settings.EvaluationWhileSolving;
        EvaluationSolvingItem.IsEnabled = settings.EvaluationEnabled;
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
        ImportItem.IsEnabled = !running;
        NextItem.IsEnabled = Puzzle.CanNext;
        HintItem.IsEnabled = Puzzle.CanHint;
        SolutionItem.IsEnabled = Puzzle.CanShowSolution;
        RetryItem.IsEnabled = Puzzle.CanRetry;
        GoToItem.IsEnabled = hasStore;
        FirstItem.IsEnabled = BackItem.IsEnabled = Puzzle.CanGoBack;
        ForwardItem.IsEnabled = LatestItem.IsEnabled = Puzzle.CanGoForward;
        CopyFenItem.IsEnabled = CopyLinkItem.IsEnabled = CopyPgnItem.IsEnabled = ExportPuzzlePgnItem.IsEnabled = OpenLichessItem.IsEnabled = view is not null;
        ExportMatchingItem.IsEnabled = hasStore && !running && _app.Filter.HasMatches;
        ExportPlayedItem.IsEnabled = hasStore && !running;
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

    private static bool IsTextInput(object? element) => element is TextBox or AutoSuggestBox or PasswordBox or RichEditBox
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

    private async void OnGetDatabase(object sender, RoutedEventArgs e)
    {
        if (!await Launcher.LaunchUriAsync(new Uri(PuzzleFiles.DownloadPage)))
        {
            Status.Report("The Lichess download page could not be opened. Visit database.lichess.org to get the puzzle file, then import it here.");
        }
    }

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
        if (PuzzleFiles.Find(AppContext.BaseDirectory, App.DataDirectory) is { } input)
        {
            await _app.Host.Try(c => c.StartBuild(input, keepDownload: true), "The local puzzle file could not be imported");
        }
        else
        {
            OnImport(sender, e);
        }
    }

    private async void OnPause(object sender, RoutedEventArgs e) => await _app.Host.Try(c => c.PauseDownload(), "Pausing failed");

    private async void OnCancelOperation(object sender, RoutedEventArgs e) => await _app.Host.Try(c => c.CancelOperation(), "Cancelling failed");

    private async void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        var action = Status.NoticeAction;
        var path = Status.NoticePath;
        Status.DismissNotice();
        switch (action)
        {
            case EventAction.RetryDownload:
                OnGetDatabase(sender, e);
                break;
            case EventAction.RetryBuild:
                OnEmptyPrimary(sender, e);
                break;
            case EventAction.RetryCheck:
                OnGetDatabase(sender, e);
                break;
            case EventAction.SwitchStore:
                await _app.Host.Try(c => c.SwitchStore(), "Switching to the new puzzles failed");
                break;
            case EventAction.ShowFile when path.Length > 0:
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { $"/select,{path}" } });
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Status.Report($"The file could not be shown: {ex.Message}");
                }
                break;
        }
    }

    private void OnDismissNotice(object sender, RoutedEventArgs e) => Status.DismissNotice();

    private async void OnDatabaseDetails(object sender, RoutedEventArgs e) => await Dialogs.ShowDatabaseAsync(Root.XamlRoot, _app, this);

    public async Task StartRebuildAsync()
    {
        if (PuzzleFiles.Find(AppContext.BaseDirectory, App.DataDirectory) is { } input)
        {
            await _app.Host.Try(c => c.StartBuild(input, keepDownload: true), "The rebuild could not start");
        }
        else
        {
            StartImport();
        }
    }

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

    private void OnShowPuzzle(object sender, RoutedEventArgs e) => _puzzlePane.ShowPuzzle();

    private void OnShowHistory(object sender, RoutedEventArgs e) => _puzzlePane.ShowHistory();

    private void OnToggleEvaluation(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.EvaluationEnabled = EvaluationItem.IsChecked;
        _app.Settings.Save();
        ApplyEvaluationSettings();
    }

    private void OnToggleEvaluationSolving(object sender, RoutedEventArgs e)
    {
        _app.Settings.Value.EvaluationWhileSolving = EvaluationSolvingItem.IsChecked;
        _app.Settings.Save();
        ApplyEvaluationSettings();
    }

    /// <summary>Shows or hides the evaluation for the current position after a setting changes.</summary>
    public void ApplyEvaluationSettings()
    {
        ApplyBoardSettings();
        if (Puzzle.View is { } view)
        {
            _app.Evaluation.Hide();
            _app.Evaluation.Show(view.Fen, view.State is SessionState.Solved or SessionState.SolutionShown);
        }
    }

    private void UpdateEvaluationBar()
    {
        var evaluation = _app.Evaluation;
        EvaluationBar.Visibility = ShowIf(evaluation.IsVisible);
        var white = Math.Clamp(evaluation.WhiteShare, 0.02, 0.98);
        WhiteShareRow.Height = new GridLength(white, GridUnitType.Star);
        BlackShareRow.Height = new GridLength(1 - white, GridUnitType.Star);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(EvaluationBar, evaluation.BarName);
    }

    // Export ------------------------------------------------------------------------------------

    private async void OnExportMatching(object sender, RoutedEventArgs e)
    {
        var count = _app.Filter.MatchCount;
        var choice = await Dialogs.ShowExportAsync(Root.XamlRoot, this, "Export matching puzzles",
            $"{Format.Count(count)} puzzles match the current filters.", "lichess-puzzles", _app.Settings);
        if (choice is not null)
        {
            await _app.Host.Try(c => c.StartExport(ExportSource.Matching, choice.Format, choice.PlayerStart, choice.Path), "The export could not start");
        }
    }

    private async void OnExportPlayed(object sender, RoutedEventArgs e)
    {
        var name = _app.Player.Name;
        var choice = await Dialogs.ShowExportAsync(Root.XamlRoot, this, "Export played puzzles",
            $"The puzzles {name} has played, newest first.", $"played-{name}", _app.Settings);
        if (choice is not null)
        {
            await _app.Host.Try(c => c.StartExport(ExportSource.Played, choice.Format, choice.PlayerStart, choice.Path), "The export could not start");
        }
    }

    private async void OnExportPuzzlePgn(object sender, RoutedEventArgs e)
    {
        if (Puzzle.View is null)
        {
            return;
        }
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"lichess-puzzle-{Puzzle.PuzzleId}" };
        picker.FileTypeChoices.Add("PGN", [".pgn"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is not { } file)
        {
            return;
        }
        var playerStart = _app.Settings.Value.ExportPlayerStart;
        string? pgn = null;
        await _app.Host.Try(c => pgn = c.GetPuzzlePgn(playerStart), "The PGN could not be made");
        if (pgn is null)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(file.Path, pgn);
            Status.AddEvent(new Event { Level = EventLevel.Success, Message = $"Saved puzzle {Puzzle.PuzzleId} to {file.Path}.", Action = EventAction.ShowFile, Path = file.Path });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Report($"The PGN could not be saved: {ex.Message}");
        }
    }

    private async void OnCopyPgn(object sender, RoutedEventArgs e)
    {
        var playerStart = _app.Settings.Value.ExportPlayerStart;
        string? pgn = null;
        await _app.Host.Try(c => pgn = c.GetPuzzlePgn(playerStart), "The PGN could not be made");
        if (pgn is not null)
        {
            Copy(pgn);
        }
    }

    // Players -----------------------------------------------------------------------------------

    private void RebuildPlayersMenu()
    {
        var player = _app.Player;
        var key = System.Text.Json.JsonSerializer.Serialize(new
        {
            Current = player.Current?.Id,
            player.Rated,
            Players = player.Players.Select(p => new { p.Id, p.Name }).ToArray(),
        });
        if (key == _playersMenuKey)
        {
            return;
        }
        _playersMenuKey = key;
        // MenuBarItem mirrors its items into a flyout. Remove individually: a vector Reset
        // from Clear leaves old entries in that flyout, duplicating the entire menu on refresh.
        while (PlayersMenu.Items.Count > 0)
        {
            PlayersMenu.Items.RemoveAt(PlayersMenu.Items.Count - 1);
        }
        foreach (var p in player.Players)
        {
            var item = new RadioMenuFlyoutItem { Text = p.Name, GroupName = "players", IsChecked = p.Id == player.Current?.Id, Tag = p.Id };
            item.Click += async (s, _) => await player.SwitchToAsync((long)((FrameworkElement)s).Tag);
            PlayersMenu.Items.Add(item);
        }
        PlayersMenu.Items.Add(new MenuFlyoutSeparator());
        var rated = new ToggleMenuFlyoutItem { Text = "Rated", IsChecked = player.Rated };
        rated.Click += (_, _) => player.Rated = rated.IsChecked;
        PlayersMenu.Items.Add(rated);
        PlayersMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (text, command) in new[]
        {
            ("New player…", PlayerCommand.New),
            ("Rename player…", PlayerCommand.Rename),
            ("Delete player…", PlayerCommand.Delete),
            ("Clear history…", PlayerCommand.ClearHistory),
        })
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = command != PlayerCommand.Delete || player.Players.Count > 1 };
            item.Click += (_, _) => _ = PlayerCommandAsync(command);
            PlayersMenu.Items.Add(item);
        }
    }

    private async Task PlayerCommandAsync(PlayerCommand command)
    {
        var player = _app.Player;
        var root = Root.XamlRoot;
        bool NameTaken(string name, long except) => player.Players.Any(p => p.Id != except && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        switch (command)
        {
            case PlayerCommand.New:
            {
                var name = await Dialogs.PromptAsync(root, "New player", "Name", "", "Add player",
                    n => NameTaken(n, 0) ? "Another player already has this name." : null, blockOnWarning: true);
                if (name is null)
                {
                    return;
                }
                Player? added = null;
                if (await _app.Host.Try(c => added = c.AddPlayer(name), "The player could not be added") && added is not null)
                {
                    await player.SwitchToAsync(added.Id);
                }
                break;
            }
            case PlayerCommand.Rename when player.Current is { } current:
            {
                var name = await Dialogs.PromptAsync(root, "Rename player", "Name", current.Name, "Rename",
                    n => NameTaken(n, current.Id) ? "Another player already has this name." : null, blockOnWarning: true);
                if (name is not null)
                {
                    await _app.Host.Try(c => c.RenamePlayer(current.Id, name), "The player could not be renamed");
                    await player.RefreshAsync();
                }
                break;
            }
            case PlayerCommand.Delete when player.Current is { } current:
            {
                if (await Dialogs.ConfirmAsync(root, "Delete player?",
                        $"Delete {current.Name}, with a rating of {current.Rating:0} and their played puzzles? This cannot be undone.", "Delete player"))
                {
                    await _app.Host.Try(c => c.DeletePlayer(current.Id), "The player could not be deleted");
                    await player.RefreshAsync();
                }
                break;
            }
            case PlayerCommand.ClearHistory when player.Current is { } current:
            {
                if (await Dialogs.ConfirmAsync(root, "Clear history?",
                        $"Forget every puzzle {current.Name} has played? The rating is kept. This cannot be undone.", "Clear history"))
                {
                    await _app.Host.Try(c => c.ClearHistory(current.Id), "The history could not be cleared");
                    await player.RefreshAsync();
                    await _app.History.RefreshAsync();
                    await _app.Filter.ApplyAsync();
                }
                break;
            }
        }
    }

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
        // Standard 6.1: a workspace opens at 1200 × 800 with a minimum of 800 × 560.
        var width = Math.Min(ui.WindowWidth > 0 ? ui.WindowWidth : (int)(1200 * scale), area.Width);
        var height = Math.Min(ui.WindowHeight > 0 ? ui.WindowHeight : (int)(800 * scale), area.Height);
        var saved = new RectInt32(ui.WindowX, ui.WindowY, width, height);
        var visible = ui.WindowX != int.MinValue
            && saved.X >= area.X - 32 && saved.Y >= area.Y - 8
            && saved.X + 64 <= area.X + area.Width && saved.Y + 64 <= area.Y + area.Height;
        AppWindow.MoveAndResize(visible ? saved : new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(800 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
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
