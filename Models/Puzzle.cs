namespace Lichess_Puzzles.Models;

public record Puzzle
{
    public required string PuzzleId { get; init; }
    public required string Fen { get; init; }
    public required string Moves { get; init; }
    public required int Rating { get; init; }
    public required int RatingDeviation { get; init; }
    public required int Popularity { get; init; }
    public required int NbPlays { get; init; }
    public string? GameUrl { get; init; }
    public string? OpeningTags { get; init; }
    public List<Theme> Themes { get; init; } = [];

    /// <summary>
    /// Gets the moves as a list of UCI move strings (e.g., "e2e4").
    /// </summary>
    public string[] GetMoveList() => Moves.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Gets the first move (opponent's move that sets up the puzzle).
    /// </summary>
    public string? GetSetupMove()
    {
        var moves = GetMoveList();
        return moves.Length > 0 ? moves[0] : null;
    }

    /// <summary>
    /// Gets the solution moves (player's moves to solve the puzzle).
    /// </summary>
    public string[] GetSolutionMoves()
    {
        var moves = GetMoveList();
        return moves.Length > 1 ? moves[1..] : [];
    }
}
