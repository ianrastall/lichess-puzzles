using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jupiter.LichessPuzzles.App.Controls;
using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.Interop;
using Jupiter.LichessPuzzles.Messages;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Jupiter.LichessPuzzles.App.ViewModels;

/// <summary>What the puzzle view model asks of the board.</summary>
public interface IBoardView
{
    void Show(SessionView view, bool animate);
    void ShowAttempt(int from, int to);
    void ShowMarker(int square, BoardMarker marker);
    void SetOrientation(bool whiteAtBottom);
    bool WhiteAtBottom { get; }
    void Announce(string text);
}

public sealed partial class MoveCell(string san, int ply, bool isMove, Action<int> select) : ObservableObject
{
    public string San { get; } = san;
    public int Ply { get; } = ply;
    public bool IsMove { get; } = isMove;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [RelayCommand]
    private void Select() => select(Ply);
}

/// <summary>A row of the move list: a numbered pair of moves, or the "Puzzle starts" marker.</summary>
public sealed class MoveRow
{
    public string Number { get; init; } = "";
    public MoveCell? White { get; init; }
    public MoveCell? Black { get; init; }
    public bool IsMarker { get; init; }
}

/// <summary>The puzzle being solved: drives the session in the core and the board's timing.</summary>
public sealed partial class PuzzleViewModel : ObservableObject
{
    private enum LastAction { None, Correct, Wrong, Hint }

    private readonly CoreHost _host;
    private readonly JsonStore<AppSettings> _settings;
    private readonly UiState _ui;
    private readonly DispatcherQueue _dispatcher;
    private IBoardView? _board;
    private SessionView? _view;
    private int _serial;
    private LastAction _last;
    private bool _busy;
    private bool _rated;
    private (double Before, double After)? _change;

    public PuzzleViewModel(CoreHost host, JsonStore<AppSettings> settings, UiState ui, DispatcherQueue dispatcher)
    {
        _host = host;
        _settings = settings;
        _ui = ui;
        _dispatcher = dispatcher;
    }

    public void AttachBoard(IBoardView board) => _board = board;

    /// <summary>Raised with the rating before and after when a rated puzzle is settled.</summary>
    public event Action<double, double>? RatingSettled;

    /// <summary>Raised when the history may have changed: a mistake, hint, solve, solution or a new puzzle.</summary>
    public event Action? Recorded;

    /// <summary>Raised with the shown position and whether the puzzle has ended, for the evaluation.</summary>
    public event Action<string, bool>? PositionShown;

    /// <summary>Theme key → display name, and opening tag → display name, from the catalogue.</summary>
    public Dictionary<string, string> ThemeNames { get; set; } = [];
    public Dictionary<string, string> OpeningNames { get; set; } = [];

    /// <summary>Whether the filters currently match any puzzle.</summary>
    public bool FilterHasMatches { get; set { field = value; UpdateCommands(); } } = true;

    public bool StoreAvailable { get; set { field = value; UpdateCommands(); } }

    public ObservableCollection<MoveRow> Moves { get; } = [];

    [ObservableProperty] public partial bool HasPuzzle { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "No puzzle";
    [ObservableProperty] public partial string Facts { get; set; } = "";
    [ObservableProperty] public partial string Opening { get; set; } = "";
    [ObservableProperty] public partial string Themes { get; set; } = "";
    [ObservableProperty] public partial string ThemesNote { get; set; } = "";
    [ObservableProperty] public partial string PromptTitle { get; set; } = "";
    [ObservableProperty] public partial string PromptMessage { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity PromptSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty] public partial bool CanHint { get; set; }
    [ObservableProperty] public partial bool CanShowSolution { get; set; }
    [ObservableProperty] public partial bool CanRetry { get; set; }
    [ObservableProperty] public partial bool CanNext { get; set; }
    [ObservableProperty] public partial string NextTooltip { get; set; } = "Next puzzle (Ctrl+N)";
    [ObservableProperty] public partial bool CanGoBack { get; set; }
    [ObservableProperty] public partial bool CanGoForward { get; set; }
    [ObservableProperty] public partial string Fen { get; set; } = "";

    public string PuzzleId => _view?.Puzzle.Id ?? "";
    public string PuzzleLink => _view is null ? "" : $"https://lichess.org/training/{_view.Puzzle.Id}";
    public string GameUrl => _view?.Puzzle.GameUrl ?? "";
    public SessionView? View => _view;

    private bool Finished => _view?.State is SessionState.Solved or SessionState.SolutionShown;

    // Commands ------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task NextAsync()
    {
        if (!CanNext || _busy)
        {
            return;
        }
        var filter = _settings.Value.Filter;
        var near = filter.NearRating;
        var offset = filter.DifficultyOffset;
        await Run(core => core.NextPuzzle(near, offset), result => Start(result.View), "A puzzle could not be opened");
        Recorded?.Invoke();
    }

    /// <summary>Opens a puzzle by id or link; a replay is neither rated nor recorded.</summary>
    public async Task<string?> LoadAsync(string id, bool replay = false)
    {
        try
        {
            var result = await _host.Run(core => core.LoadPuzzle(id, replay));
            if (result is not null)
            {
                Start(result.View);
                Track(result);
                Recorded?.Invoke();
            }
            return null;
        }
        catch (PuzzleCoreException e)
        {
            return Format.Sentence(e.Message);
        }
    }

    /// <summary>Reopens the puzzle that was on the board when the app closed, with the moves played.</summary>
    public async Task<bool> RestoreAsync()
    {
        var id = _ui.CurrentPuzzleId;
        if (id.Length == 0 || _ui.CurrentPuzzleFinished)
        {
            return false;
        }
        var moves = _ui.CurrentPuzzleSolverMoves.ToList();
        try
        {
            var restored = await _host.Run(core =>
            {
                var result = core.LoadPuzzle(id);
                if (moves.Count == 0)
                {
                    return result;
                }
                result = core.PlayOpponent();
                foreach (var move in moves)
                {
                    result = core.MakeMove(move);
                    if (result.Outcome == MoveOutcome.Correct)
                    {
                        result = core.PlayOpponent();
                    }
                    else if (result.Outcome != MoveOutcome.Solved)
                    {
                        break;
                    }
                }
                return result;
            });
            if (restored is null)
            {
                return false;
            }
            Start(restored.View, keepMoves: true);
            Track(restored);
            return true;
        }
        catch (PuzzleCoreException)
        {
            return false;
        }
    }

    public async Task MoveAsync(string uci)
    {
        if (_view?.State != SessionState.Solving || _busy)
        {
            if (_view is not null)
            {
                _board?.Show(_view, animate: false);
            }
            return;
        }
        var serial = _serial;
        SessionResult? result;
        try
        {
            _busy = true;
            result = await _host.Run(core => core.MakeMove(uci));
        }
        catch (PuzzleCoreException e)
        {
            _host.ReportProblem?.Invoke($"The move could not be checked: {Format.Sentence(e.Message)}");
            return;
        }
        finally
        {
            _busy = false;
        }
        if (result is null || serial != _serial)
        {
            return;
        }
        var from = SquareIndex(uci[..2]);
        var to = SquareIndex(uci.Substring(2, 2));
        Track(result);
        switch (result.Outcome)
        {
            case MoveOutcome.Wrong:
                _last = LastAction.Wrong;
                _board?.ShowAttempt(from, to);
                _board?.ShowMarker(to, BoardMarker.Wrong);
                _view = result.View;
                UpdateText();
                Recorded?.Invoke();
                _board?.Announce("Not the move. Try again.");
                After(600, serial, () => _board?.Show(_view, animate: false));
                break;
            case MoveOutcome.Correct:
                _last = LastAction.Correct;
                RememberMove(uci, finished: false);
                Apply(result.View, animate: true);
                _board?.ShowMarker(to, BoardMarker.Correct);
                ScheduleOpponent(serial);
                break;
            case MoveOutcome.Solved:
                RememberMove(uci, finished: true);
                Apply(result.View, animate: true);
                _board?.ShowMarker(LastTo(result.View), BoardMarker.Correct);
                Recorded?.Invoke();
                var clean = result.View.Mistakes == 0 && result.View.Hints == 0;
                if (clean && _settings.Value.NextAfterCleanSolve)
                {
                    After(1200, serial, () => _ = NextAsync());
                }
                break;
            default:
                _board?.Show(_view!, animate: false);
                break;
        }
    }

    [RelayCommand]
    public async Task HintAsync()
    {
        if (!CanHint)
        {
            return;
        }
        await Run(core => core.Hint(), result =>
        {
            _last = LastAction.Hint;
            Apply(result.View, animate: false);
            _board?.Announce("Hint: move the highlighted piece.");
        }, "The hint could not be shown");
        Recorded?.Invoke();
    }

    [RelayCommand]
    public async Task ShowSolutionAsync()
    {
        if (!CanShowSolution || _busy)
        {
            return;
        }
        _serial++;
        await Run(core => core.ShowSolution(), result =>
        {
            _ui.CurrentPuzzleFinished = true;
            Apply(result.View, animate: true);
        }, "The solution could not be shown");
        Recorded?.Invoke();
    }

    [RelayCommand]
    public async Task RetryAsync()
    {
        if (!CanRetry)
        {
            return;
        }
        await Run(core => core.Retry(), result => Start(result.View, keepOrientation: true), "The puzzle could not be restarted");
        Recorded?.Invoke();
    }

    /// <summary>Steps through the line: -1/+1, or int.MinValue/int.MaxValue for the ends.</summary>
    public async Task StepAsync(int step)
    {
        if (_view is null)
        {
            return;
        }
        var current = (int)_view.View;
        var target = step switch
        {
            int.MinValue => 0,
            int.MaxValue => (int)_view.Played,
            _ => Math.Clamp(current + step, 0, (int)_view.Played),
        };
        await GoToPlyAsync(target);
    }

    public async Task GoToPlyAsync(int ply)
    {
        if (_view is null || ply == _view.View || _view.State is SessionState.OpponentToMove or SessionState.ReplyPending)
        {
            return;
        }
        var forwardOne = ply == _view.View + 1;
        await Run(core => core.Navigate(ply), result => Apply(result.View, animate: forwardOne), "The move could not be shown");
    }

    [RelayCommand]
    private Task Back() => StepAsync(-1);

    [RelayCommand]
    private Task Forward() => StepAsync(1);

    [RelayCommand]
    private Task First() => StepAsync(int.MinValue);

    [RelayCommand]
    private Task Latest() => StepAsync(int.MaxValue);

    public void Flip()
    {
        if (_board is not null && _view is not null)
        {
            _board.SetOrientation(!_board.WhiteAtBottom);
            _board.Show(_view, animate: false);
        }
    }

    /// <summary>Forgets the puzzle when the store is closed.</summary>
    public void Clear()
    {
        _serial++;
        _view = null;
        Moves.Clear();
        HasPuzzle = false;
        Title = "No puzzle";
        Facts = Opening = Themes = ThemesNote = PromptTitle = PromptMessage = Fen = "";
        UpdateCommands();
    }

    /// <summary>Re-applies text that depends on settings, such as theme visibility.</summary>
    public void Refresh()
    {
        if (_view is not null)
        {
            UpdateText();
        }
    }

    // Flow ----------------------------------------------------------------------------------------

    private async Task Run(Func<PuzzleCore, SessionResult> call, Action<SessionResult> apply, string what)
    {
        if (_busy)
        {
            return;
        }
        var serial = _serial;
        try
        {
            _busy = true;
            var result = await _host.Run(call);
            if (result is not null && serial == _serial)
            {
                apply(result);
                Track(result);
            }
        }
        catch (PuzzleCoreException e)
        {
            if (e.Kind == PuzzleErrorKind.NoMatches)
            {
                FilterHasMatches = false;
            }
            _host.ReportProblem?.Invoke($"{what}: {Format.Sentence(e.Message)}");
        }
        finally
        {
            _busy = false;
            UpdateCommands();
        }
    }

    private void Start(SessionView view, bool keepOrientation = false, bool keepMoves = false)
    {
        var serial = ++_serial;
        _last = LastAction.None;
        _change = null;
        _view = view;
        if (!keepOrientation)
        {
            _board?.SetOrientation(view.Puzzle.Solver != Side.Black);
        }
        if (!keepMoves)
        {
            _ui.CurrentPuzzleId = view.Puzzle.Id;
            _ui.CurrentPuzzleSolverMoves.Clear();
            _ui.CurrentPuzzleFinished = false;
        }
        _board?.Show(view, animate: false);
        UpdateText();
        _board?.Announce($"Puzzle {view.Puzzle.Id}. {PromptMessage}");
        if (view.State == SessionState.OpponentToMove)
        {
            ScheduleOpponent(serial);
        }
    }

    private void ScheduleOpponent(int serial)
    {
        After(_settings.Value.OpponentDelayMs, serial, async () =>
        {
            SessionResult? result;
            try
            {
                result = await _host.Run(core => core.PlayOpponent());
            }
            catch (PuzzleCoreException)
            {
                return;
            }
            if (result is null || serial != _serial)
            {
                return;
            }
            _last = LastAction.None;
            Apply(result.View, animate: true);
            Track(result);
            var move = result.View.Moves.LastOrDefault();
            if (move is not null)
            {
                _board?.Announce($"{(move.White ? "White" : "Black")}: {SpokenMove(move.San)}. {PromptMessage}");
            }
        });
    }

    private void After(int milliseconds, int serial, Action action)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (serial == _serial)
            {
                action();
            }
        };
        timer.Start();
    }

    private void Apply(SessionView view, bool animate)
    {
        _view = view;
        _board?.Show(view, animate);
        UpdateText();
    }

    /// <summary>Takes the rating state from a session result.</summary>
    private void Track(SessionResult result)
    {
        _rated = result.Rated;
        if (result.RatingSettled && _change is null)
        {
            _change = (result.RatingBefore, result.RatingAfter);
            RatingSettled?.Invoke(result.RatingBefore, result.RatingAfter);
        }
        if (_view is not null)
        {
            UpdateText();
        }
    }

    private string RatingNote(SessionView view)
    {
        if (_change is { } c)
        {
            var delta = (int)Math.Round(c.After - c.Before);
            return $" Rating {Format.Count((long)Math.Round(c.After))} ({(delta >= 0 ? "+" : "")}{delta}).";
        }
        return view.Replay ? " Replay; not rated." : _rated ? "" : " Not rated.";
    }

    private void RememberMove(string uci, bool finished)
    {
        _ui.CurrentPuzzleSolverMoves.Add(uci);
        _ui.CurrentPuzzleFinished = finished;
    }

    // Text ----------------------------------------------------------------------------------------

    private void UpdateText()
    {
        var view = _view!;
        var p = view.Puzzle;
        HasPuzzle = true;
        Title = $"Puzzle {p.Id}";
        Facts = $"Rating {p.Rating} ± {p.Deviation} · popularity {p.Popularity} · {Format.Count((long)p.Plays)} plays";
        var opening = new[] { p.OpeningFamily, p.OpeningVariation }.Where(t => t.Length > 0).Select(t => OpeningNames.GetValueOrDefault(t, t.Replace('_', ' ')));
        Opening = string.Join(" / ", opening);
        var showThemes = Finished || _settings.Value.ShowThemesWhileSolving;
        Themes = showThemes ? string.Join(" · ", p.Themes.Select(t => ThemeNames.GetValueOrDefault(t, t))) : "";
        ThemesNote = showThemes ? "" : "Themes are shown when the puzzle ends";
        Fen = view.Fen;

        var side = p.Solver == Side.Black ? "Black" : "White";
        var rating = RatingNote(view);
        var failedNow = _rated && _change is { } c && c.After < c.Before ? " This puzzle now counts as failed." : "";
        (PromptTitle, PromptMessage, PromptSeverity) = view.State switch
        {
            SessionState.OpponentToMove => ("Opponent's move", "Watch the move that sets up the puzzle.", InfoBarSeverity.Informational),
            SessionState.ReplyPending => ("Best move", "Keep going.", InfoBarSeverity.Success),
            SessionState.Solving when _last == LastAction.Wrong => ("Not the move", "Try again." + failedNow, InfoBarSeverity.Warning),
            SessionState.Solving when _last == LastAction.Hint && view.HintSquare >= 0 => ("Hint", "Move the highlighted piece.", InfoBarSeverity.Informational),
            SessionState.Solving when view.View != view.Played => ("Your turn", "Return to the latest position to move.", InfoBarSeverity.Informational),
            SessionState.Solving => ("Your turn", $"Find the best move for {side}.", InfoBarSeverity.Informational),
            SessionState.Solved when view.Mistakes == 0 && view.Hints == 0 => ("Solved", "Clean solve." + rating, InfoBarSeverity.Success),
            SessionState.Solved => ("Solved with help", Help(view) + rating, InfoBarSeverity.Warning),
            SessionState.SolutionShown => ("Solution", "Step through the moves with the arrow keys." + rating, InfoBarSeverity.Informational),
            _ => ("", "", InfoBarSeverity.Informational),
        };

        BuildMoves(view);
        UpdateCommands();
        PositionShown?.Invoke(view.Fen, Finished);
    }

    private static string Help(SessionView view)
    {
        var parts = new List<string>();
        if (view.Mistakes > 0)
        {
            parts.Add(view.Mistakes == 1 ? "1 mistake" : $"{view.Mistakes} mistakes");
        }
        if (view.Hints > 0)
        {
            parts.Add(view.Hints == 1 ? "1 hint" : $"{view.Hints} hints");
        }
        return string.Join(", ", parts) + ".";
    }

    private void BuildMoves(SessionView view)
    {
        Moves.Clear();
        (string Number, MoveCell White, MoveCell? Black)? pending = null;
        void Add()
        {
            if (pending is { } p)
            {
                Moves.Add(new MoveRow { Number = p.Number, White = p.White, Black = p.Black });
                pending = null;
            }
        }
        for (var i = 0; i < view.Moves.Count; i++)
        {
            var m = view.Moves[i];
            var cell = new MoveCell(m.San, i + 1, true, ply => _ = GoToPlyAsync(ply)) { IsCurrent = view.View == i + 1 };
            if (m.White)
            {
                Add();
                pending = ($"{m.Number}.", cell, null);
            }
            else
            {
                var white = pending?.White ?? new MoveCell("…", 0, false, _ => { });
                pending = ($"{m.Number}.", white, cell);
                Add();
            }
            if (i == 0)
            {
                // The opponent's first move sets up the puzzle; the solver's moves follow the marker.
                Add();
                Moves.Add(new MoveRow { IsMarker = true });
            }
        }
        Add();
    }

    private void UpdateCommands()
    {
        var view = _view;
        var solvingAtLatest = view?.State == SessionState.Solving && view.View == view.Played;
        CanHint = solvingAtLatest && !_busy;
        CanShowSolution = view is not null && !Finished;
        CanRetry = view is not null;
        CanNext = StoreAvailable && FilterHasMatches;
        NextTooltip = !StoreAvailable ? "Available once puzzles are installed"
            : !FilterHasMatches ? "No puzzles match the filters"
            : "Next puzzle (Ctrl+N)";
        var navigable = view is not null && view.State is not (SessionState.OpponentToMove or SessionState.ReplyPending);
        CanGoBack = navigable && view!.View > 0;
        CanGoForward = navigable && view!.View < view.Played;
    }

    private static int SquareIndex(string square) => (square[1] - '1') * 8 + (square[0] - 'a');

    private static int LastTo(SessionView view) => view.LastTo;

    /// <summary>"Nxe4+" → "knight takes e4, check".</summary>
    public static string SpokenMove(string san)
    {
        if (san.StartsWith("O-O-O", StringComparison.Ordinal)) return "castles queenside" + (san.EndsWith('#') ? ", checkmate" : san.EndsWith('+') ? ", check" : "");
        if (san.StartsWith("O-O", StringComparison.Ordinal)) return "castles kingside" + (san.EndsWith('#') ? ", checkmate" : san.EndsWith('+') ? ", check" : "");
        var suffix = san.EndsWith('#') ? ", checkmate" : san.EndsWith('+') ? ", check" : "";
        var core = san.TrimEnd('+', '#');
        var piece = core[0] switch { 'N' => "knight", 'B' => "bishop", 'R' => "rook", 'Q' => "queen", 'K' => "king", _ => "pawn" };
        var promotion = "";
        var eq = core.IndexOf('=');
        if (eq >= 0)
        {
            promotion = " promotes to " + (core[eq + 1] switch { 'N' => "knight", 'B' => "bishop", 'R' => "rook", _ => "queen" });
            core = core[..eq];
        }
        var target = core[^2..];
        var verb = core.Contains('x') ? " takes " : " to ";
        return piece + verb + target + promotion + suffix;
    }
}
