using Jupiter.LichessPuzzles.Interop;
using Jupiter.LichessPuzzles.Messages;

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {what}");
    if (!ok) failures++;
}

T Expect<T>(Func<T> action, PuzzleErrorKind kind, string what) where T : class?
{
    try
    {
        action();
        Check(false, what);
    }
    catch (PuzzleCoreException e)
    {
        Check(e.Kind == kind, $"{what} ({e.Message})");
    }
    return null!;
}

Status WaitForOperation(PuzzleCore core)
{
    var deadline = DateTime.UtcNow.AddSeconds(30);
    var status = core.GetStatus();
    while (status.Operation is not null && DateTime.UtcNow < deadline)
    {
        Thread.Sleep(20);
        status = core.GetStatus();
    }
    return status;
}

var root = Path.Combine(Path.GetTempPath(), $"lpz-interop-{Environment.ProcessId}");
var sample = Path.Combine(AppContext.BaseDirectory, "sample_puzzles.csv");
try
{
    using (var core = PuzzleCore.Create(root, "http://127.0.0.1:9/unused"))
    {
        var status = core.GetStatus();
        Check(status.Store is null && status.StoreProblem == "No puzzles are installed.", "empty data folder reports no store");
        Expect(() => core.NextPuzzle(), PuzzleErrorKind.NoStore, "next puzzle without a store fails");

        core.StartBuild(sample, keepDownload: true);
        Expect<object?>(() => { core.StartBuild(sample, true); return null; }, PuzzleErrorKind.Busy, "a second build is refused while one runs");
        status = WaitForOperation(core);
        Check(status.Store?.PuzzleCount == 13, "sample builds and installs");
        var events = core.PollEvents();
        Check(events.Count == 1 && events[0].Level == EventLevel.Success, $"one success event ({string.Join("; ", events.Select(e => e.Message))})");

        var catalogue = core.GetCatalogue();
        Check(catalogue.Themes.Count == 19 && catalogue.Groups[0] == "Goal", "catalogue themes and groups");
        Check(catalogue.RatingMin == 998 && catalogue.RatingMax == 2873, "catalogue rating range");

        var counts = core.SetFilter(new Filter { RatingMin = 1500, RatingMax = 1800 });
        Check(counts.Total == 5, "rating filter counts");
        counts = core.SetFilter(new Filter { HasMinPopularity = true, MinPopularity = 95, MinPlays = 1000 });
        Check(counts.Total == 3, "popularity and plays filter counts");
        Expect(() => core.SetFilter(new Filter { ThemesAny = { "nonsense" } }), PuzzleErrorKind.InvalidFilter, "unknown theme rejected");
        counts = core.SetFilter(new Filter { ThemesAny = { "mate" } });
        Check(counts.Total == 2 && counts.Themes.Count == 19, "theme filter counts");

        var result = core.NextPuzzle();
        Check(result.View.State == SessionState.OpponentToMove && result.View.Board.Length == 64, "next puzzle opens before the opponent's move");
        var line = result.View.Puzzle.Moves.ToList();
        result = core.PlayOpponent();
        Check(result.View.State == SessionState.Solving && result.View.LegalMoves.Count > 0, "solving with legal moves");
        var ply = 1;
        while (true)
        {
            result = core.MakeMove(line[ply]);
            ply += 2;
            if (result.Outcome == MoveOutcome.Solved) break;
            if (result.Outcome != MoveOutcome.Correct) { Check(false, $"move {line[ply - 2]} was {result.Outcome}"); break; }
            core.PlayOpponent();
        }
        Check(result.View.State == SessionState.Solved && result.View.Moves.Count == line.Count, "mate puzzle solved through the ABI");

        result = core.LoadPuzzle("https://lichess.org/training/00sJ9");
        Check(result.View.Puzzle.Id == "00sJ9" && result.View.Puzzle.OpeningVariation == "French_Defense_Exchange_Variation", "load by link");
        result = core.ShowSolution();
        Check(result.View.State == SessionState.SolutionShown && result.View.View == 2, "show solution");
        result = core.Navigate(0);
        Check(result.View.View == 0 && result.View.LastFrom == -1, "navigate to the start");
        Expect(() => core.LoadPuzzle("zzzzz"), PuzzleErrorKind.NotFound, "unknown puzzle id");
    }

    // A second core reopens the installed store.
    using (var core = PuzzleCore.Create(root))
    {
        Check(core.GetStatus().Store?.PuzzleCount == 13, "store reopens");
    }
}
catch (PuzzleCoreException e)
{
    Check(false, $"unexpected core error: {e.Kind}: {e.Message}");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

Console.WriteLine(failures == 0 ? "All interop checks passed." : $"{failures} interop check(s) failed.");
return failures == 0 ? 0 : 1;
