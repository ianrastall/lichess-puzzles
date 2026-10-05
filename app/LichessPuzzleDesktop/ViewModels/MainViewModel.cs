using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LichessPuzzleDesktop.Core;
using LichessPuzzleDesktop.Interop;
using LichessPuzzleDesktop.Services;

namespace LichessPuzzleDesktop.ViewModels;

public enum StatusKind { Info, Success, Warning, Error }

public sealed partial class MainViewModel : ObservableObject, IBoardInteraction, IDisposable
{
    private const int EngineMoveTimeMs = 6000;
    private static readonly TimeSpan ReplyDelay = TimeSpan.FromMilliseconds(350);

    private readonly IShellService _shell;
    private readonly AppSettings _settings;
    private readonly PuzzleDatabase _database = new();
    private readonly PuzzleSession _session = new();
    private readonly AnalysisEngine _engine = new();
    private readonly PuzzleHistory _history;
    private readonly PeriodicTimer _engineTimer = new(TimeSpan.FromMilliseconds(150));

    private PuzzleInfo? _puzzle;
    private CancellationTokenSource? _busyCancel;
    private int _puzzleSerial;      // bumps whenever the session is reloaded, to drop stale delayed work
    private bool _recorded;         // the current attempt is already in the history
    private bool _userFlipped;
    private DateTimeOffset _startedAt;
    private string? _analysedFen;
    private bool _loadingSettings;

    public MainViewModel(IShellService shell, AppSettings settings)
    {
        _shell = shell;
        _settings = settings;
        _history = new PuzzleHistory(AppSettings.HistoryPath);

        _loadingSettings = true;
        RatingMin = settings.RatingMin;
        RatingMax = settings.RatingMax;
        PopularityMin = settings.PopularityMin;
        PlaysMin = settings.PlaysMin;
        SkipPlayed = settings.SkipPlayed;
        IsEngineEnabled = settings.EngineEnabled;
        EvalWhileSolving = settings.EvalWhileSolving;
        PgnFromPlayerMove = settings.PgnFromPlayerMove;
        _loadingSettings = false;

        Openings.Add(new OpeningOption(null, "Any opening"));
        SelectedOpening = Openings[0];
        UpdateStats();
        _ = PollEngineAsync();
    }

    // ------------------------------------------------------------------ state

    public ObservableCollection<MoveRowViewModel> Moves { get; } = [];
    public ObservableCollection<ThemeOption> Themes { get; } = [];
    public ObservableCollection<OpeningOption> Openings { get; } = [];

    [ObservableProperty] public partial BoardSnapshot Board { get; set; } = BoardSnapshot.Empty;

    [ObservableProperty] public partial bool HasDatabase { get; set; }
    [ObservableProperty] public partial bool IsSampleDatabase { get; set; }
    [ObservableProperty] public partial string DatabaseName { get; set; } = "No database loaded";
    [ObservableProperty] public partial string DatabaseSummary { get; set; } = "";
    [ObservableProperty] public partial string FilterSummary { get; set; } = "";

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string BusyText { get; set; } = "";
    [ObservableProperty] public partial double BusyProgress { get; set; }

    [ObservableProperty] public partial bool HasPuzzle { get; set; }
    [ObservableProperty] public partial bool IsInProgress { get; set; }
    [ObservableProperty] public partial bool CanStepBack { get; set; }
    [ObservableProperty] public partial bool CanStepForward { get; set; }
    [ObservableProperty] public partial string PuzzleTitle { get; set; } = "No puzzle";
    [ObservableProperty] public partial string PuzzleFacts { get; set; } = "";
    [ObservableProperty] public partial string PuzzleThemes { get; set; } = "";
    [ObservableProperty] public partial string PuzzleOpening { get; set; } = "";
    [ObservableProperty] public partial bool HasGameUrl { get; set; }

    [ObservableProperty] public partial StatusKind StatusKind { get; set; }
    [ObservableProperty] public partial string StatusTitle { get; set; } = "Welcome";
    [ObservableProperty] public partial string StatusText { get; set; } = "Open a Lichess puzzle database to begin.";

    [ObservableProperty] public partial double RatingMin { get; set; }
    [ObservableProperty] public partial double RatingMax { get; set; }
    [ObservableProperty] public partial double PopularityMin { get; set; }
    [ObservableProperty] public partial double PlaysMin { get; set; }
    [ObservableProperty] public partial bool SkipPlayed { get; set; }
    [ObservableProperty] public partial OpeningOption? SelectedOpening { get; set; }
    [ObservableProperty] public partial string ThemeSummary { get; set; } = "Any theme";
    [ObservableProperty] public partial string PuzzleIdInput { get; set; } = "";

    [ObservableProperty] public partial bool IsEngineEnabled { get; set; }
    [ObservableProperty] public partial bool EvalWhileSolving { get; set; }
    [ObservableProperty] public partial bool IsEvalVisible { get; set; }
    [ObservableProperty] public partial string EvalText { get; set; } = "";
    [ObservableProperty] public partial string EvalDetail { get; set; } = "";
    [ObservableProperty] public partial string EvalLine { get; set; } = "";
    [ObservableProperty] public partial double EvalWhiteFraction { get; set; } = 0.5;

    [ObservableProperty] public partial bool PgnFromPlayerMove { get; set; }
    [ObservableProperty] public partial string StatsText { get; set; } = "";
    [ObservableProperty] public partial bool HasHistory { get; set; }

    // -------------------------------------------------------------- database

    /// <summary>Opens the last used database, or the bundled sample on first run.</summary>
    public async Task InitializeAsync()
    {
        if (Native.lpd_abi_version() != Native.AbiVersion)
        {
            await _shell.ShowMessageAsync("Native core mismatch", "lpd_core.dll does not match this build of the app. Rebuild the solution.");
            return;
        }

        string sample = Path.Combine(AppContext.BaseDirectory, "samples", "sample_puzzles.csv");
        if (_settings.DatabasePath is { } saved && File.Exists(saved)) await LoadDatabaseAsync(saved, isSample: false);
        else if (File.Exists(sample)) await LoadDatabaseAsync(sample, isSample: true);
    }

    [RelayCommand]
    private async Task OpenDatabaseAsync()
    {
        if (IsBusy) return;
        if (await _shell.PickOpenFileAsync(".csv") is { } path) await LoadDatabaseAsync(path, isSample: false);
    }

    private async Task LoadDatabaseAsync(string path, bool isSample)
    {
        RecordAttemptIfAbandoned();
        _engine.Stop();
        bool loaded = await RunBusyAsync($"Indexing {Path.GetFileName(path)}…", (progress, cancel) => _database.LoadAsync(path, progress, cancel));

        Themes.Clear();
        while (Openings.Count > 1) Openings.RemoveAt(1);
        SelectedOpening = Openings[0];
        UpdateThemeSummary();
        HasDatabase = loaded && _database.Count > 0;
        IsSampleDatabase = HasDatabase && isSample;
        if (!HasDatabase)
        {
            DatabaseName = "No database loaded";
            DatabaseSummary = FilterSummary = "";
            ClearPuzzle();
            return;
        }

        foreach (var theme in _database.Themes().OrderBy(t => ThemeNames.Humanize(t.Name), StringComparer.OrdinalIgnoreCase))
            Themes.Add(new ThemeOption(theme.Name, theme.Count, UpdateThemeSummary));
        foreach (var opening in _database.Openings().OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            Openings.Add(new OpeningOption(opening.Name, $"{ThemeNames.Humanize(opening.Name)}  ({opening.Count:N0})"));

        DatabaseName = Path.GetFileName(path);
        DatabaseSummary = $"{_database.Count:N0} puzzles";
        if (!isSample)
        {
            _settings.DatabasePath = path;
            _settings.Save();
        }

        await ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        if (!HasDatabase || IsBusy) return;
        var filter = new PuzzleFilter(ToInt(RatingMin), ToInt(RatingMax), ToInt(PopularityMin), ToInt(PlaysMin),
            [.. Themes.Where(t => t.IsSelected).Select(t => t.Key)], SelectedOpening?.Key, SkipPlayed);
        SaveSettings();

        IsBusy = true;
        BusyText = "Filtering…";
        BusyProgress = 0;
        int matches;
        try
        {
            matches = await _database.FilterAsync(filter, _history);
        }
        finally
        {
            IsBusy = false;
        }

        FilterSummary = matches == _database.Count ? "All puzzles match" : $"{matches:N0} puzzles match";
        if (matches > 0) NextPuzzle();
        else SetStatus(StatusKind.Warning, "No puzzles match", "Loosen the filters and apply them again.");
    }

    [RelayCommand]
    private void ResetFilters()
    {
        RatingMin = 400;
        RatingMax = 3500;
        PopularityMin = -100;
        PlaysMin = 0;
        SelectedOpening = Openings[0];
        foreach (var theme in Themes) theme.IsSelected = false;
    }

    private void UpdateThemeSummary()
    {
        var selected = Themes.Where(t => t.IsSelected).Select(t => ThemeNames.Humanize(t.Key)).ToList();
        ThemeSummary = selected.Count switch
        {
            0 => "Any theme",
            <= 2 => string.Join(" + ", selected),
            _ => $"{selected.Count} themes",
        };
    }

    // ---------------------------------------------------------------- puzzle

    [RelayCommand]
    private void NextPuzzle()
    {
        if (!HasDatabase || IsBusy) return;
        int row = _database.RandomRow();
        if (row < 0)
        {
            SetStatus(StatusKind.Warning, "No puzzles match", "Loosen the filters and apply them again.");
            return;
        }
        LoadPuzzle(row);
    }

    [RelayCommand]
    private void GoToPuzzle()
    {
        string id = PuzzleIdInput.Trim().TrimStart('#');
        // Accept a pasted training URL as well as a bare id.
        int slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        if (id.Length == 0 || !HasDatabase || IsBusy) return;

        int row = _database.FindId(id);
        if (row < 0) SetStatus(StatusKind.Warning, "Puzzle not found", $"There is no puzzle \"{id}\" in this database.");
        else LoadPuzzle(row);
    }

    private void LoadPuzzle(int row)
    {
        RecordAttemptIfAbandoned();
        try
        {
            var puzzle = _database.Get(row);
            _session.Load(puzzle);
            _puzzle = puzzle;
        }
        catch (CoreException e)
        {
            SetStatus(StatusKind.Error, "Unreadable puzzle", e.Message);
            return;
        }

        _puzzleSerial++;
        _recorded = false;
        _userFlipped = false;
        _startedAt = DateTimeOffset.Now;

        PuzzleTitle = $"Puzzle #{_puzzle.Id}";
        PuzzleFacts = $"Rating {_puzzle.Rating} ±{_puzzle.RatingDeviation}  ·  {_puzzle.NbPlays:N0} plays  ·  popularity {_puzzle.Popularity}";
        PuzzleOpening = _puzzle.OpeningTags.Count > 0 ? ThemeNames.Humanize(_puzzle.OpeningTags[^1]) : "";
        HasGameUrl = IsLichessUrl(_puzzle.GameUrl);
        Refresh();
    }

    private void ClearPuzzle()
    {
        _puzzle = null;
        _puzzleSerial++;
        Moves.Clear();
        Board = BoardSnapshot.Empty;
        HasPuzzle = IsInProgress = CanStepBack = CanStepForward = HasGameUrl = false;
        PuzzleTitle = "No puzzle";
        PuzzleFacts = PuzzleThemes = PuzzleOpening = "";
        SetStatus(StatusKind.Info, "Welcome", "Open a Lichess puzzle database to begin.");
        UpdateEngine();
    }

    [RelayCommand]
    private void Retry()
    {
        if (_puzzle is null) return;
        _session.Retry();
        _puzzleSerial++;
        Refresh();
    }

    [RelayCommand]
    private void Hint()
    {
        int square = _session.Hint();
        if (square < 0) return;
        Refresh(hintSquare: square);
        SetStatus(StatusKind.Info, "Hint", "Move the highlighted piece.");
    }

    [RelayCommand]
    private void ShowSolution()
    {
        if (!IsInProgress) return;
        _session.RevealAll();
        _puzzleSerial++;
        RecordAttempt();
        Refresh();
    }

    [RelayCommand] private void StepFirst() => GoToView(0);
    [RelayCommand] private void StepBack() => GoToView(_session.State.View - 1);
    [RelayCommand] private void StepForward() => GoToView(_session.State.View + 1);
    [RelayCommand] private void StepLast() => GoToView(int.MaxValue);

    private void GoToView(int view)
    {
        if (_puzzle is null) return;
        _puzzleSerial++; // cancels a pending automatic reply step
        _session.GoTo(view);
        Refresh();
    }

    [RelayCommand]
    private void FlipBoard()
    {
        _userFlipped = !_userFlipped;
        Refresh();
    }

    // -------------------------------------------------------- board callbacks

    public ulong GetLegalTargets(int from) => _puzzle is null ? 0 : _session.LegalTargets(from);

    public bool IsPromotion(int from, int to) => _puzzle is not null && _session.IsPromotion(from, to);

    public void PlayMove(int from, int to, char promotion)
    {
        if (_puzzle is null) return;
        switch (_session.TryMove(from, to, promotion))
        {
            case MoveResult.Wrong:
                Refresh();
                SetStatus(StatusKind.Error, "Not the move", "That move doesn't work. Try something else.");
                break;
            case MoveResult.Correct:
                Refresh();
                SetStatus(StatusKind.Success, "Best move!", "Keep going…");
                _ = ShowReplyAsync(_puzzleSerial);
                break;
            case MoveResult.Solved:
                RecordAttempt();
                Refresh();
                break;
            default:
                Refresh();
                break;
        }
    }

    // The core has already appended the opponent's reply; reveal it after a short pause.
    private async Task ShowReplyAsync(int serial)
    {
        await Task.Delay(ReplyDelay);
        if (serial != _puzzleSerial) return;
        _session.GoTo(int.MaxValue);
        Refresh();
    }

    // --------------------------------------------------------------- refresh

    private void Refresh(int hintSquare = -1)
    {
        SessionState state = _session.State;
        var status = (PuzzleStatus)state.Status;
        HasPuzzle = status != PuzzleStatus.Empty;
        if (!HasPuzzle) return;

        bool flipped = (state.PlayerColor == 1) ^ _userFlipped;
        Board = new BoardSnapshot(_session.Board(), flipped, state.LastFrom, state.LastTo, state.CheckSquare,
            hintSquare, state.CanMove != 0, state.SideToMove == 0);

        IsInProgress = status == PuzzleStatus.InProgress;
        CanStepBack = state.View > 0;
        CanStepForward = state.View < state.PlyCount;
        PuzzleThemes = IsInProgress
            ? "Hidden until the puzzle is finished"
            : string.Join(", ", _puzzle!.Themes.Select(ThemeNames.Humanize));

        RebuildMoves(state);
        UpdateStatus(state);
        UpdateEngine();
    }

    private void RebuildMoves(SessionState state)
    {
        var plies = _session.Plies();
        var placeholder = new MoveCellViewModel("…", -1, null);
        var rows = new List<MoveRowViewModel>();
        MoveCellViewModel? pendingWhite = null;
        string number = "";

        foreach (var ply in plies)
        {
            var cell = new MoveCellViewModel(ply.San, ply.Index + 1, GoToView) { IsCurrent = ply.Index + 1 == state.View };
            if (ply.IsWhite)
            {
                pendingWhite = cell;
                number = $"{ply.MoveNumber}.";
            }
            else
            {
                rows.Add(new MoveRowViewModel($"{ply.MoveNumber}.", pendingWhite ?? placeholder, cell));
                pendingWhite = null;
            }
        }
        if (pendingWhite is not null) rows.Add(new MoveRowViewModel(number, pendingWhite, new MoveCellViewModel("", -1, null)));

        Moves.Clear();
        foreach (var row in rows) Moves.Add(row);
    }

    private void UpdateStatus(SessionState state)
    {
        string player = state.PlayerColor == 0 ? "White" : "Black";
        if (state.Status == (int)PuzzleStatus.Solved)
        {
            if (state.SolutionShown != 0)
                SetStatus(StatusKind.Warning, "Solution shown", "Step through the moves, then try the next puzzle.");
            else if (state.Mistakes > 0 || state.Hints > 0)
                SetStatus(StatusKind.Warning, "Puzzle complete", Describe(state.Mistakes, state.Hints));
            else
                SetStatus(StatusKind.Success, "Solved!", "Clean solve — on to the next one.");
        }
        else if (state.CanMove != 0)
        {
            SetStatus(StatusKind.Info, $"{player} to move", "Find the best move.");
        }
        else if (state.View < state.PlyCount)
        {
            SetStatus(StatusKind.Info, "Reviewing", "Return to the latest position to continue.");
        }

        static string Describe(int mistakes, int hints)
        {
            var parts = new List<string>();
            if (mistakes > 0) parts.Add(mistakes == 1 ? "1 mistake" : $"{mistakes} mistakes");
            if (hints > 0) parts.Add(hints == 1 ? "1 hint" : $"{hints} hints");
            return $"Finished with {string.Join(" and ", parts)}.";
        }
    }

    private void SetStatus(StatusKind kind, string title, string text)
    {
        StatusKind = kind;
        StatusTitle = title;
        StatusText = text;
    }

    // --------------------------------------------------------------- history

    private void RecordAttempt()
    {
        if (_recorded || _puzzle is null) return;
        _recorded = true;
        SessionState state = _session.State;
        bool clean = state.Status == (int)PuzzleStatus.Solved && state.Mistakes == 0 && state.Hints == 0 && state.SolutionShown == 0;
        int seconds = (int)Math.Clamp((DateTimeOffset.Now - _startedAt).TotalSeconds, 0, 86400);
        _history.Add(new HistoryEntry(_puzzle.Id, DateTimeOffset.Now, _puzzle.Rating, clean, state.Mistakes, state.Hints, seconds));
        UpdateStats();
    }

    // A puzzle that was attempted (a wrong move or a hint) and then skipped counts as failed.
    private void RecordAttemptIfAbandoned()
    {
        if (_puzzle is null || _recorded) return;
        SessionState state = _session.State;
        if (state.Mistakes > 0 || state.Hints > 0 || state.PlyCount > 1) RecordAttempt();
    }

    private void UpdateStats()
    {
        var entries = _history.Entries();
        HasHistory = entries.Count > 0;
        if (entries.Count == 0)
        {
            StatsText = "No puzzles played yet";
            return;
        }
        int solved = entries.Count(e => e.Solved);
        int streak = entries.Reverse().TakeWhile(e => e.Solved).Count();
        StatsText = $"{solved:N0} of {entries.Count:N0} solved cleanly ({100.0 * solved / entries.Count:0}%)  ·  streak {streak}";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _history.Clear();
        UpdateStats();
    }

    // ---------------------------------------------------------------- engine

    private void UpdateEngine()
    {
        bool show = IsEngineEnabled && HasPuzzle && (!IsInProgress || EvalWhileSolving);
        IsEvalVisible = show;
        if (!show)
        {
            _engine.Stop();
            _analysedFen = null;
            EvalText = EvalDetail = EvalLine = "";
            EvalWhiteFraction = 0.5;
            return;
        }

        string fen = _session.Fen();
        if (fen == _analysedFen) return;
        _analysedFen = fen;
        EvalText = "…";
        EvalDetail = EvalLine = "";
        _engine.Start(fen, 0, EngineMoveTimeMs);
    }

    private async Task PollEngineAsync()
    {
        try
        {
            while (await _engineTimer.WaitForNextTickAsync())
            {
                if (!IsEvalVisible || _analysedFen is null) continue;
                EngineInfo info = _engine.Poll();
                if (info.Depth == 0 && info.Pv.Length == 0) continue;
                ShowEvaluation(info);
            }
        }
        catch (OperationCanceledException)
        {
            // Timer disposed on shutdown.
        }
    }

    private void ShowEvaluation(EngineInfo info)
    {
        if (info.Mate != 0)
        {
            EvalText = (info.Mate > 0 ? "#" : "#-") + Math.Abs(info.Mate).ToString(CultureInfo.InvariantCulture);
            EvalWhiteFraction = info.Mate > 0 ? 1 : 0;
        }
        else if (Math.Abs(info.ScoreCp) >= 31000)
        {
            EvalText = "#";
            EvalWhiteFraction = info.ScoreCp > 0 ? 1 : 0;
        }
        else
        {
            EvalText = (info.ScoreCp / 100.0).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
            // Same squashing Lichess uses for its bar.
            EvalWhiteFraction = 1.0 / (1.0 + Math.Exp(-0.00368208 * info.ScoreCp));
        }
        EvalDetail = info.Depth > 0
            ? $"depth {info.Depth}  ·  {info.Nodes / 1000.0:N0}k nodes{(info.Running ? "" : "  ·  done")}"
            : "";
        EvalLine = info.Pv;
    }

    partial void OnIsEngineEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSettings();
        UpdateEngine();
    }

    partial void OnEvalWhileSolvingChanged(bool value)
    {
        if (_loadingSettings) return;
        SaveSettings();
        UpdateEngine();
    }

    partial void OnPgnFromPlayerMoveChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

    // ---------------------------------------------------------------- export

    [RelayCommand]
    private void CopyFen()
    {
        if (_puzzle is not null) _shell.CopyText(_session.Fen());
    }

    [RelayCommand]
    private void CopyPgn()
    {
        if (_puzzle is not null) _shell.CopyText(_session.Pgn(PgnFromPlayerMove));
    }

    [RelayCommand]
    private async Task SavePgnAsync()
    {
        if (_puzzle is null) return;
        string pgn = _session.Pgn(PgnFromPlayerMove);
        if (await _shell.PickSaveFileAsync($"lichess-puzzle-{_puzzle.Id}", "PGN file", ".pgn") is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, pgn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _shell.ShowMessageAsync("Export failed", e.Message);
        }
    }

    [RelayCommand]
    private async Task ExportFilteredPgnAsync()
    {
        if (!HasDatabase || IsBusy) return;
        if (await _shell.PickSaveFileAsync("lichess-puzzles", "PGN file", ".pgn") is not { } path) return;
        await ExportAsync("Exporting PGN…", path, (progress, cancel) => _database.ExportPgnAsync(path, null, PgnFromPlayerMove, progress, cancel));
    }

    [RelayCommand]
    private async Task ExportFilteredCsvAsync()
    {
        if (!HasDatabase || IsBusy) return;
        if (await _shell.PickSaveFileAsync("lichess-puzzles-filtered", "CSV file", ".csv") is not { } path) return;
        await ExportAsync("Exporting CSV…", path, (progress, cancel) => _database.ExportCsvAsync(path, null, progress, cancel));
    }

    [RelayCommand]
    private async Task ExportHistoryPgnAsync()
    {
        if (!HasDatabase || IsBusy) return;
        int[] rows = [.. _history.Entries().Select(e => e.Id).Distinct().Select(_database.FindId).Where(row => row >= 0)];
        if (rows.Length == 0)
        {
            await _shell.ShowMessageAsync("Nothing to export", "None of the puzzles you have played are in this database.");
            return;
        }
        if (await _shell.PickSaveFileAsync("lichess-puzzles-played", "PGN file", ".pgn") is not { } path) return;
        await ExportAsync("Exporting PGN…", path, (progress, cancel) => _database.ExportPgnAsync(path, rows, PgnFromPlayerMove, progress, cancel));
    }

    private async Task ExportAsync(string busyText, string path, Func<IProgress<double>, CancellationToken, Task<int>> export)
    {
        int written = 0;
        bool done = await RunBusyAsync(busyText, async (progress, cancel) => written = await export(progress, cancel));
        if (done) await _shell.ShowMessageAsync("Export complete", $"Wrote {written:N0} puzzles to\n{path}");
    }

    [RelayCommand]
    private async Task OpenOnLichessAsync()
    {
        if (_puzzle is not null) await _shell.OpenUriAsync(new Uri($"https://lichess.org/training/{Uri.EscapeDataString(_puzzle.Id)}"));
    }

    [RelayCommand]
    private async Task OpenGameAsync()
    {
        if (_puzzle is not null && IsLichessUrl(_puzzle.GameUrl)) await _shell.OpenUriAsync(new Uri(_puzzle.GameUrl));
    }

    // The URL comes from the data file, so only ever hand lichess.org links to the browser.
    private static bool IsLichessUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "lichess.org";

    // --------------------------------------------------------------- plumbing

    [RelayCommand]
    private void CancelBusy() => _busyCancel?.Cancel();

    /// <summary>Runs a long operation with progress and cancellation. Returns false if it failed or was cancelled.</summary>
    private async Task<bool> RunBusyAsync(string text, Func<IProgress<double>, CancellationToken, Task> work)
    {
        using var cancel = new CancellationTokenSource();
        _busyCancel = cancel;
        IsBusy = true;
        BusyText = text;
        BusyProgress = 0;
        try
        {
            await work(new Progress<double>(value => BusyProgress = value * 100), cancel.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is CoreException or IOException or UnauthorizedAccessException)
        {
            IsBusy = false;
            await _shell.ShowMessageAsync("Something went wrong", e.Message);
            return false;
        }
        finally
        {
            _busyCancel = null;
            IsBusy = false;
        }
    }

    private void SaveSettings()
    {
        _settings.RatingMin = ToInt(RatingMin);
        _settings.RatingMax = ToInt(RatingMax);
        _settings.PopularityMin = ToInt(PopularityMin);
        _settings.PlaysMin = ToInt(PlaysMin);
        _settings.SkipPlayed = SkipPlayed;
        _settings.EngineEnabled = IsEngineEnabled;
        _settings.EvalWhileSolving = EvalWhileSolving;
        _settings.PgnFromPlayerMove = PgnFromPlayerMove;
        _settings.Save();
    }

    // NumberBox reports NaN when its text is cleared.
    private static int ToInt(double value) => double.IsNaN(value) ? 0 : (int)Math.Clamp(value, -1_000_000, 1_000_000);

    public void Dispose()
    {
        RecordAttemptIfAbandoned();
        _engineTimer.Dispose();
        _engine.Dispose();
        _session.Dispose();
        _database.Dispose();
        _history.Dispose();
    }
}
