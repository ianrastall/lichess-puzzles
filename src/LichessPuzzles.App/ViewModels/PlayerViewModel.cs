using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.Interop;
using Jupiter.LichessPuzzles.Messages;

namespace Jupiter.LichessPuzzles.App.ViewModels;

/// <summary>The current player, their rating and today's totals; the list of players.</summary>
public sealed partial class PlayerViewModel(CoreHost host, JsonStore<AppSettings> settings) : ObservableObject
{
    public IReadOnlyList<Player> Players { get; private set; } = [];

    public Player? Current { get; private set; }

    /// <summary>Raised after the current player or the player list changes.</summary>
    public event Action? Changed;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string RatingText { get; set; } = "";
    [ObservableProperty] public partial string RatingStatus { get; set; } = "";
    [ObservableProperty] public partial string RatingTooltip { get; set; } = "";
    [ObservableProperty] public partial string ChangeText { get; set; } = "";
    [ObservableProperty] public partial string ChangeName { get; set; } = "";
    [ObservableProperty] public partial string TodayText { get; set; } = "";
    [ObservableProperty] public partial bool HasPlayer { get; set; }

    [ObservableProperty]
    public partial bool Rated { get; set; } = settings.Value.Rated;

    partial void OnRatedChanged(bool value)
    {
        settings.Value.Rated = value;
        settings.Save();
        _ = host.Try(core => core.SetRated(value), "The rated setting could not be applied");
    }

    public double Rating => Current?.Rating ?? 1500;

    /// <summary>Reads the players and today's totals from the core.</summary>
    public async Task RefreshAsync()
    {
        PlayerList? list;
        try
        {
            list = await host.Run(core => core.GetPlayers(new DateTimeOffset(DateTime.Today)));
        }
        catch (PuzzleCoreException e)
        {
            host.ReportProblem?.Invoke($"The players could not be read: {Format.Sentence(e.Message)}");
            return;
        }
        if (list is null)
        {
            return;
        }
        var previous = Current?.Id;
        Players = list.Players;
        Current = list.Players.FirstOrDefault(p => p.Id == list.Current);
        HasPlayer = Current is not null;
        if (Current is { } p)
        {
            Name = p.Name;
            RatingText = $"{Format.Count((long)Math.Round(p.Rating))}{(p.Provisional ? "?" : "")}";
            RatingStatus = $"{(p.Provisional ? "Provisional" : "Established")} · rating deviation {p.Deviation:0} · {Format.Count(p.Games)} rated puzzles";
            RatingTooltip = $"Local puzzle rating {p.Rating:0} (Glicko-2 deviation {p.Deviation:0})" + (p.Provisional ? ", provisional while the deviation is above 110" : "")
                + $" · {Format.Count(p.Games)} rated puzzles, {Format.Count(p.Wins)} solved. Based on this player’s selected puzzles; not a Lichess account or over-the-board rating.";
        }
        var s = list.Stats;
        TodayText = s.TodayPlayed == 0
            ? (s.Played == 0 ? "No puzzles played yet." : $"Streak {s.Streak} · best {s.BestStreak}")
            : $"Today: {s.TodaySolved} solved of {s.TodayPlayed} · streak {s.Streak}";
        if (previous != Current?.Id)
        {
            ChangeText = ChangeName = "";
        }
        Changed?.Invoke();
    }

    /// <summary>Shows the change from the latest settled rated puzzle.</summary>
    public void ShowChange(double before, double after)
    {
        var change = (int)Math.Round(after - before);
        ChangeText = change > 0 ? $"+{change}" : change.ToString();
        ChangeName = change >= 0 ? $"rating up {change}" : $"rating down {-change}";
    }

    public Task<bool> SwitchToAsync(long id) => Run(core => core.SetCurrentPlayer(id), "Switching player failed");

    private async Task<bool> Run(Action<PuzzleCore> action, string what)
    {
        var ok = await host.Try(action, what);
        await RefreshAsync();
        return ok;
    }
}

public sealed class HistoryRow(HistoryEntry e)
{
    public HistoryEntry Entry { get; } = e;
    public string PuzzleId { get; } = e.PuzzleId;
    public string Played { get; } = DateTimeOffset.FromUnixTimeMilliseconds(e.PlayedMs).ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    public string Rating { get; } = e.PuzzleRating.ToString();
    public string Result { get; } = e.Result switch
    {
        PuzzleResult.Solved => "Solved",
        PuzzleResult.SolvedWithHelp => "Solved with help",
        _ => "Failed",
    };
    public string Mistakes { get; } = e.Mistakes.ToString();
    public string Hints { get; } = e.Hints.ToString();
    public string Time { get; } = e.Seconds < 60 ? $"{e.Seconds:0} s" : $"{(int)(e.Seconds / 60)}:{(int)e.Seconds % 60:00}";
    public string Change { get; } = e.Rated ? FormatChange(e.RatingAfter - e.RatingBefore) : "";

    private static string FormatChange(double change)
    {
        var n = (int)Math.Round(change);
        return n > 0 ? $"+{n}" : n.ToString();
    }

    public string AutomationName => $"{PuzzleId}, {Played}, rating {Rating}, {Result}" + (Change.Length > 0 ? $", rating change {Change}" : "");
}

/// <summary>The current player's played puzzles, with a result filter and sorting.</summary>
public sealed partial class HistoryViewModel(CoreHost host) : ObservableObject
{
    private IReadOnlyList<HistoryEntry> _entries = [];

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] public partial string TotalsText { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string EmptyText { get; set; } = "No puzzles played yet by this player.";

    /// <summary>0 all, 1 solved, 2 solved with help, 3 failed.</summary>
    [ObservableProperty]
    public partial int ResultFilter { get; set; }

    partial void OnResultFilterChanged(int value) => Rebuild();

    public string SortKey { get; private set; } = "Played";
    public bool SortDescending { get; private set; } = true;

    public int Count => _entries.Count;

    public async Task RefreshAsync()
    {
        try
        {
            _entries = await host.Run(core => core.GetHistory()) ?? [];
        }
        catch (PuzzleCoreException e)
        {
            host.ReportProblem?.Invoke($"The history could not be read: {Format.Sentence(e.Message)}");
            _entries = [];
        }
        Rebuild();
    }

    public void SortBy(string key)
    {
        SortDescending = SortKey == key ? !SortDescending : key is "Played" or "Rating" or "Change";
        SortKey = key;
        Rebuild();
    }

    private void Rebuild()
    {
        IEnumerable<HistoryEntry> rows = _entries;
        rows = ResultFilter switch
        {
            1 => rows.Where(e => e.Result == PuzzleResult.Solved),
            2 => rows.Where(e => e.Result == PuzzleResult.SolvedWithHelp),
            3 => rows.Where(e => e.Result == PuzzleResult.Failed),
            _ => rows,
        };
        Func<HistoryEntry, IComparable> key = SortKey switch
        {
            "Puzzle" => e => e.PuzzleId,
            "Rating" => e => e.PuzzleRating,
            "Result" => e => (int)e.Result,
            "Mistakes" => e => e.Mistakes,
            "Hints" => e => e.Hints,
            "Time" => e => e.Seconds,
            "Change" => e => e.Rated ? e.RatingAfter - e.RatingBefore : double.MinValue,
            _ => e => e.PlayedMs,
        };
        var sorted = SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        Rows.Clear();
        foreach (var e in sorted)
        {
            Rows.Add(new HistoryRow(e));
        }
        var played = _entries.Count;
        var clean = _entries.Count(e => e.Result == PuzzleResult.Solved);
        var best = 0;
        var run = 0;
        foreach (var e in _entries.OrderBy(e => e.PlayedMs))
        {
            run = e.Result == PuzzleResult.Solved ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        TotalsText = played == 0 ? "" : $"{Format.Count(played)} played · {100.0 * clean / played:0}% solved cleanly · best streak {best}";
        IsEmpty = Rows.Count == 0;
        EmptyText = played == 0 ? "No puzzles played yet by this player." : "No played puzzles have this result.";
    }
}

/// <summary>The engine's evaluation of the shown position.</summary>
public sealed partial class EvaluationViewModel(CoreHost host, JsonStore<AppSettings> settings) : ObservableObject
{
    private string _fen = "";

    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial string ScoreText { get; set; } = "";
    [ObservableProperty] public partial string LineText { get; set; } = "";
    [ObservableProperty] public partial string DetailText { get; set; } = "";
    /// <summary>White's share of the evaluation bar, 0 to 1.</summary>
    [ObservableProperty] public partial double WhiteShare { get; set; } = 0.5;
    [ObservableProperty] public partial string BarName { get; set; } = "";

    public bool Running { get; private set; }

    /// <summary>Analyses <paramref name="fen"/> when evaluation is on and may be shown now.</summary>
    public void Show(string fen, bool finished)
    {
        var s = settings.Value;
        var visible = s.EvaluationEnabled && (finished || s.EvaluationWhileSolving) && fen.Length > 0;
        if (!visible)
        {
            Hide();
            return;
        }
        IsVisible = true;
        if (fen == _fen)
        {
            return;
        }
        _fen = fen;
        ScoreText = "…";
        LineText = DetailText = "";
        WhiteShare = 0.5;
        BarName = "Evaluation in progress";
        Running = true;
        var limit = TimeSpan.FromSeconds(Math.Clamp(s.EvaluationSeconds, 0.5, 5));
        _ = host.Try(core => core.StartAnalysis(fen, limit), "The evaluation could not start");
    }

    public void Hide()
    {
        if (IsVisible || Running)
        {
            _ = host.Try(core => core.StopAnalysis(), "Stopping the evaluation failed");
        }
        IsVisible = false;
        Running = false;
        _fen = "";
    }

    /// <summary>Reads the latest result while an analysis runs.</summary>
    public async Task PollAsync()
    {
        if (!Running)
        {
            return;
        }
        Analysis? a;
        try
        {
            a = await host.Run(core => core.GetAnalysis());
        }
        catch (PuzzleCoreException)
        {
            Running = false;
            return;
        }
        if (a is null || a.Fen != _fen)
        {
            return;
        }
        Running = a.Running;
        if (a.TerminalOutcome != AnalysisOutcome.Unspecified)
        {
            ScoreText = a.TerminalOutcome switch
            {
                AnalysisOutcome.WhiteWins => "Checkmate — White wins",
                AnalysisOutcome.BlackWins => "Checkmate — Black wins",
                _ => "Drawn position",
            };
            WhiteShare = a.TerminalOutcome switch { AnalysisOutcome.WhiteWins => 1, AnalysisOutcome.BlackWins => 0, _ => 0.5 };
            BarName = $"Evaluation: {ScoreText}";
            LineText = "";
            DetailText = "The game has ended.";
            return;
        }
        if (!a.HasResult)
        {
            if (!Running)
            {
                ScoreText = "No evaluation";
                BarName = "No evaluation available";
                DetailText = "The search ended before completing a depth.";
            }
            return;
        }
        var whiteToMove = _fen.Split(' ').ElementAtOrDefault(1) != "b";
        if (a.Mate != 0)
        {
            var side = a.Mate > 0 ? "White" : "Black";
            ScoreText = $"Mate in {Math.Abs(a.Mate)} for {side}";
            WhiteShare = a.Mate > 0 ? 1 : 0;
        }
        else
        {
            ScoreText = $"{a.Centipawns / 100.0:+0.00;-0.00;0.00}";
            WhiteShare = 1 / (1 + Math.Exp(-a.Centipawns / 250.0));
        }
        BarName = $"Evaluation {ScoreText}";
        var number = int.TryParse(_fen.Split(' ').LastOrDefault(), out var n) ? n : 1;
        LineText = MoveText(a.PvSan, number, whiteToMove);
        DetailText = $"Depth {a.Depth} · {a.ElapsedMs / 1000.0:0.0} s · {Format.Count((long)a.Nodes)} positions" + (a.Running ? "" : " · finished");
    }

    private static string MoveText(IEnumerable<string> moves, int number, bool whiteToMove)
    {
        var parts = new List<string>();
        var white = whiteToMove;
        var first = true;
        foreach (var san in moves)
        {
            if (white)
            {
                parts.Add($"{number}. {san}");
            }
            else
            {
                parts.Add(first ? $"{number}... {san}" : san);
                number++;
            }
            white = !white;
            first = false;
        }
        return string.Join(" ", parts);
    }
}
