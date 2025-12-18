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

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PuzzleId, Fen, Moves, Rating, GameUrl FROM Puzzles";

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
            var gameUrl = reader.IsDBNull(4) ? null : reader.GetString(4);

            buffer.Clear();

            if (format == ExportFormat.Pgn)
            {
                WritePgnEntry(buffer, puzzleId, fen, moves, rating, gameUrl);
            }
            else
            {
                WriteEpdEntry(buffer, puzzleId, fen, moves, rating);
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

    private static void WriteEpdEntry(StringBuilder buffer, string puzzleId, string fen, string moves, int rating)
    {
        var fenParts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var epdFen = fenParts.Length >= 4 ? string.Join(' ', fenParts.Take(4)) : fen;
        var sanMoves = BuildSanMoves(fen, moves, out _, out _);

        buffer.Append(epdFen);
        buffer.Append($" ; id \"{puzzleId}\"");
        buffer.Append($" ; rtg {rating}");

        if (sanMoves.Count > 0)
        {
            buffer.Append(" ; pm ");
            buffer.Append(string.Join(' ', sanMoves));
        }

        buffer.AppendLine();
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
