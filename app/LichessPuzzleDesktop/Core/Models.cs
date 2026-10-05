using LichessPuzzleDesktop.Interop;

namespace LichessPuzzleDesktop.Core;

public enum PuzzleStatus { Empty = 0, InProgress = 1, Solved = 2 }

public enum MoveResult { Illegal = 0, Wrong = 1, Correct = 2, Solved = 3 }

/// <summary>One row of the Lichess puzzle CSV.</summary>
public sealed class PuzzleInfo
{
    internal LpdPuzzleInfo Raw;

    internal unsafe PuzzleInfo(in LpdPuzzleInfo raw)
    {
        Raw = raw;
        fixed (LpdPuzzleInfo* p = &Raw)
        {
            Id = Native.Text(p->Id, 16);
            Fen = Native.Text(p->Fen, 96);
            Moves = Native.Text(p->Moves, 512);
            Themes = Native.Text(p->Themes, 512).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            GameUrl = Native.Text(p->GameUrl, 128);
            OpeningTags = Native.Text(p->OpeningTags, 256).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    public string Id { get; }
    public string Fen { get; }
    public string Moves { get; }
    public IReadOnlyList<string> Themes { get; }
    public string GameUrl { get; }
    public IReadOnlyList<string> OpeningTags { get; }
    public int Rating => Raw.Rating;
    public int RatingDeviation => Raw.RatingDeviation;
    public int Popularity => Raw.Popularity;
    public int NbPlays => Raw.NbPlays;
    public DateTimeOffset? DailyDate => Raw.DailyDate > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Raw.DailyDate) : null;
}

public sealed record PlyInfo(int Index, string San, string Uci, int From, int To, int MoveNumber, bool IsWhite, bool IsPlayerMove);

public sealed record EngineInfo(bool Running, int Depth, int ScoreCp, int Mate, long Nodes, int TimeMs, int Generation,
    string BestUci, string Pv);

public sealed record HistoryEntry(string Id, DateTimeOffset Time, int Rating, bool Solved, int Mistakes, int Hints, int Seconds);

public sealed record NamedCount(string Name, int Count);

public sealed record PuzzleFilter(int RatingMin, int RatingMax, int PopularityMin, int PlaysMin,
    IReadOnlyList<string> Themes, string? Opening, bool SkipPlayed);

public sealed class CoreException(string message) : Exception(message);
