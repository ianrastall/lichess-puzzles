using System.IO;
using System.Linq;
using System.Text;
using ChessDotNet;
using Microsoft.Data.Sqlite;
using Lichess_Puzzles;

namespace Lichess_Puzzles.Services;

public enum ExportFormat
{
    Pgn,
    Epd
}

public class PuzzleExportService
{
    private readonly string _connectionString;

    public PuzzleExportService()
    {
        var dbPath = PuzzleDatabaseService.GetDatabasePath();
        _connectionString = $"Data Source={dbPath};Mode=ReadOnly";
    }

    public async Task ExportAsync(
        ExportFormat format,
        string outputPath,
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var total = await GetPuzzleCountAsync(connection, cancellationToken);
        status?.Report($"Found {total:N0} puzzles...");

        // Fetch themes once for EPD export (keyed by PuzzleId)
        Dictionary<string, List<string>>? puzzleThemes = null;
        if (format == ExportFormat.Epd)
        {
            status?.Report("Loading themes...");
            puzzleThemes = await LoadAllPuzzleThemesAsync(connection, cancellationToken);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = format == ExportFormat.Epd
            ? "SELECT PuzzleId, Fen, Moves, Rating, RatingDeviation, Popularity, NbPlays, GameUrl, OpeningTags FROM Puzzles"
            : "SELECT PuzzleId, Fen, Moves, Rating, GameUrl FROM Puzzles";

        await using var reader = await cmd.ExecuteReaderAsync(
            System.Data.CommandBehavior.SequentialAccess,
            cancellationToken);

        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        long processed = 0;
        var buffer = new StringBuilder(4096);

        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var puzzleId = reader.GetString(0);
            var fen = reader.GetString(1);
            var moves = reader.GetString(2);
            var rating = reader.GetInt32(3);

            buffer.Clear();

            if (format == ExportFormat.Pgn)
            {
                var gameUrl = reader.IsDBNull(4) ? null : reader.GetString(4);
                WritePgnEntry(buffer, puzzleId, fen, moves, rating, gameUrl);
            }
            else
            {
                var ratingDeviation = reader.GetInt32(4);
                var popularity = reader.GetInt32(5);
                var nbPlays = reader.GetInt32(6);
                var gameUrl = reader.IsDBNull(7) ? null : reader.GetString(7);
                var openingTags = reader.IsDBNull(8) ? null : reader.GetString(8);
                var themes = puzzleThemes?.GetValueOrDefault(puzzleId);
                
                WriteEpdEntry(buffer, puzzleId, fen, moves, rating, popularity, nbPlays, gameUrl, openingTags, themes);
            }

            await writer.WriteAsync(buffer.ToString());

            processed++;
            if (processed % 1000 == 0 || processed == total)
            {
                var percent = total > 0 ? (double)processed / total * 100.0 : 0;
                progress?.Report(percent);
                status?.Report($"Exported {processed:N0} of {total:N0} puzzles...");
            }
        }

        progress?.Report(100);
        status?.Report("Export complete.");
    }

    private static async Task<long> GetPuzzleCountAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Puzzles";
        var scalar = await cmd.ExecuteScalarAsync(token);
        return scalar is long l ? l : Convert.ToInt64(scalar);
    }

    private static async Task<Dictionary<string, List<string>>> LoadAllPuzzleThemesAsync(
        SqliteConnection connection, 
        CancellationToken token)
    {
        var result = new Dictionary<string, List<string>>();
        
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PuzzleId, ThemeId FROM PuzzleThemes ORDER BY PuzzleId";
        
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var puzzleId = reader.GetString(0);
            var themeId = reader.GetString(1);
            
            if (!result.TryGetValue(puzzleId, out var themes))
            {
                themes = [];
                result[puzzleId] = themes;
            }
            themes.Add(themeId);
        }
        
        return result;
    }

    private static void WritePgnEntry(StringBuilder buffer, string puzzleId, string fen, string moves, int rating, string? gameUrl)
    {
        buffer.AppendLine($"[Event \"Lichess Puzzle {puzzleId}\"]");
        buffer.AppendLine($"[FEN \"{fen}\"]");
        buffer.AppendLine("[SetUp \"1\"]");
        buffer.AppendLine($"[PuzzleId \"{puzzleId}\"]");
        buffer.AppendLine($"[Rating \"{rating}\"]");
        if (!string.IsNullOrEmpty(gameUrl))
            buffer.AppendLine($"[Site \"{gameUrl}\"]");
        buffer.AppendLine();

        var sanMoves = BuildSanMoves(fen, moves, out bool whiteToMove, out int fullmoveNumber);
        AppendMoves(buffer, sanMoves, whiteToMove, fullmoveNumber);
        buffer.AppendLine();
    }

    private static void WriteEpdEntry(
        StringBuilder buffer, 
        string puzzleId, 
        string fen, 
        string moves, 
        int rating,
        int popularity,
        int nbPlays,
        string? gameUrl,
        string? openingTags,
        List<string>? themes)
    {
        // EPD format matching the reference:
        // position id "xxx" bm Move pv Move1 Move2... c0 "Rating/Popularity/Plays" c1 "Themes" c2 "Game" [c3 "Opening"];
        
        var fenParts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var epdFen = fenParts.Length >= 4 ? string.Join(' ', fenParts.Take(4)) : fen;
        var sanMoves = BuildSanMoves(fen, moves, out _, out _);

        // Position
        buffer.Append(epdFen);
        
        // Puzzle ID
        buffer.Append($" id \"{puzzleId}\"");
        
        // Best move (player's first move, after opponent's setup move)
        if (sanMoves.Count > 1)
        {
            buffer.Append($" bm {sanMoves[1]}");
        }
        
        // Principal variation (all moves)
        if (sanMoves.Count > 0)
        {
            buffer.Append($" pv {string.Join(" ", sanMoves)}");
        }
        
        // c0: Rating, Popularity, Plays
        buffer.Append($" c0 \"Rating: {rating}, Popularity: {popularity}, Plays: {nbPlays}\"");
        
        // c1: Themes
        if (themes is { Count: > 0 })
        {
            buffer.Append($" c1 \"Themes: {string.Join(" ", themes)}\"");
        }
        
        // c2: Game URL
        if (!string.IsNullOrEmpty(gameUrl))
        {
            buffer.Append($" c2 \"Game: {gameUrl}\"");
        }
        
        // c3: Opening tags
        if (!string.IsNullOrEmpty(openingTags))
        {
            var openingFormatted = openingTags.Replace(" ", "_");
            buffer.Append($" c3 \"Opening: {openingFormatted}\"");
        }
        
        buffer.AppendLine(";");
    }

    private static List<string> BuildSanMoves(string fen, string moves, out bool whiteToMove, out int fullmoveNumber)
    {
        var sanMoves = new List<string>();
        var moveList = moves.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var game = new ChessGame(fen);

        var fenParts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        whiteToMove = fenParts.Length > 1 && fenParts[1] == "w";
        fullmoveNumber = GetFullmoveFromFen(fen);

        foreach (var uci in moveList)
        {
            if (uci.Length < 4) break;

            var move = CreateMoveFromUci(game, uci);
            if (move == null || !game.IsValidMove(move))
                break;

            var san = MainWindow.GetSanNotation(game, move);
            game.MakeMove(move, true);

            if (game.IsInCheck(game.WhoseTurn))
                san += game.IsCheckmated(game.WhoseTurn) ? "#" : "+";

            sanMoves.Add(san);
        }

        return sanMoves;
    }

    private static Move? CreateMoveFromUci(ChessGame game, string uci)
    {
        try
        {
            var from = MainWindow.ParsePosition(uci[..2]);
            var to = MainWindow.ParsePosition(uci[2..4]);
            char? promotion = uci.Length == 5 ? char.ToUpper(uci[4]) : null;

            return promotion.HasValue
                ? new Move(from, to, game.WhoseTurn, promotion.Value)
                : new Move(from, to, game.WhoseTurn);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendMoves(StringBuilder buffer, List<string> sanMoves, bool whiteToMove, int startingFullmove)
    {
        for (int i = 0; i < sanMoves.Count; i++)
        {
            bool isWhiteMove = whiteToMove ? i % 2 == 0 : i % 2 == 1;
            int moveNumber = whiteToMove
                ? startingFullmove + (i / 2)
                : startingFullmove + ((i + 1) / 2);

            if (isWhiteMove)
            {
                if (i > 0) buffer.Append(' ');
                buffer.Append($"{moveNumber}. {sanMoves[i]}");
            }
            else
            {
                if (i == 0)
                    buffer.Append($"{moveNumber}... {sanMoves[i]}");
                else
                    buffer.Append($" {sanMoves[i]}");
            }
        }

        buffer.AppendLine();
    }

    private static int GetFullmoveFromFen(string fen)
    {
        var parts = fen.Split(' ');
        if (parts.Length >= 6 && int.TryParse(parts[5], out int fullmove))
            return fullmove;
        return 1;
    }
}
