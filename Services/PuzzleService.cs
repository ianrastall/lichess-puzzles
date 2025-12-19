using System.IO;
using Lichess_Puzzles.Models;
using Microsoft.Data.Sqlite;

namespace Lichess_Puzzles.Services;

public class PuzzleService : IDisposable
{
    private readonly string _connectionString;

    public PuzzleService()
    {
        var dbPath = PuzzleDatabaseService.GetDatabasePath();
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("Puzzle database not found. Please download the database first.", dbPath);
            
        _connectionString = $"Data Source={dbPath};Mode=ReadOnly";
        
        // Validate the connection works
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public Puzzle? GetRandomPuzzle(
        int? minRating = null, 
        int? maxRating = null, 
        IReadOnlyCollection<string>? requiredThemeIds = null,
        bool excludeMateThemes = false)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();

        var requiredThemes = requiredThemeIds?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        
        var whereClauses = new List<string>();
        if (minRating.HasValue)
        {
            whereClauses.Add("p.Rating >= @minRating");
            cmd.Parameters.AddWithValue("@minRating", minRating.Value);
        }
        if (maxRating.HasValue)
        {
            whereClauses.Add("p.Rating <= @maxRating");
            cmd.Parameters.AddWithValue("@maxRating", maxRating.Value);
        }

        if (requiredThemes is { Count: > 0 })
        {
            var paramNames = new List<string>();
            var valueRows = new List<string>();
            for (int i = 0; i < requiredThemes.Count; i++)
            {
                var paramName = $"@theme{i}";
                paramNames.Add(paramName);
                valueRows.Add($"({paramName})");
                cmd.Parameters.AddWithValue(paramName, requiredThemes[i]);
            }

            whereClauses.Add($@"
                p.PuzzleId IN (
                    SELECT pt.PuzzleId
                    FROM PuzzleThemes pt
                    INNER JOIN (VALUES {string.Join(", ", valueRows)}) AS required(ThemeId)
                        ON pt.ThemeId = required.ThemeId
                    GROUP BY pt.PuzzleId
                    HAVING COUNT(DISTINCT pt.ThemeId) = {requiredThemes.Count}
                )");
        }

        if (excludeMateThemes)
        {
            // Only exclude simple mate puzzles (mate-in-1 and mate-in-2)
            // Longer mating sequences are still allowed
            whereClauses.Add("""
                p.PuzzleId NOT IN (
                    SELECT PuzzleId 
                    FROM PuzzleThemes 
                    WHERE ThemeId IN ('mateIn1', 'mateIn2')
                )
                """);
        }

        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        cmd.CommandText = $@"
            SELECT p.PuzzleId, p.Fen, p.Moves, p.Rating, p.RatingDeviation, p.Popularity, p.NbPlays, p.GameUrl, p.OpeningTags
            FROM Puzzles p
            {whereClause}
            ORDER BY RANDOM()
            LIMIT 1";

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var puzzle = ReadPuzzle(reader);
            reader.Close();
            puzzle = puzzle with { Themes = GetPuzzleThemes(connection, puzzle.PuzzleId) };
            return puzzle;
        }

        return null;
    }

    public Puzzle? GetPuzzleById(string puzzleId)
    {
        if (string.IsNullOrWhiteSpace(puzzleId))
            throw new ArgumentException("Puzzle ID cannot be empty", nameof(puzzleId));
            
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT PuzzleId, Fen, Moves, Rating, RatingDeviation, Popularity, NbPlays, GameUrl, OpeningTags
            FROM Puzzles 
            WHERE PuzzleId = @puzzleId";
        cmd.Parameters.AddWithValue("@puzzleId", puzzleId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var puzzle = ReadPuzzle(reader);
            reader.Close();
            puzzle = puzzle with { Themes = GetPuzzleThemes(connection, puzzle.PuzzleId) };
            return puzzle;
        }

        return null;
    }

    public Puzzle? GetRandomPuzzleByTheme(string themeId, int? minRating = null, int? maxRating = null)
    {
        if (string.IsNullOrWhiteSpace(themeId))
            throw new ArgumentException("Theme ID cannot be empty", nameof(themeId));

        return GetRandomPuzzle(minRating, maxRating, [themeId]);
    }

    public List<Theme> GetAllThemes()
    {
        var themes = new List<Theme>();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ThemeId, DisplayName, Description FROM Themes ORDER BY DisplayName";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            themes.Add(new Theme
            {
                ThemeId = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return themes;
    }

    private static List<Theme> GetPuzzleThemes(SqliteConnection connection, string puzzleId)
    {
        var themes = new List<Theme>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT t.ThemeId, t.DisplayName, t.Description
            FROM PuzzleThemes pt 
            INNER JOIN Themes t ON pt.ThemeId = t.ThemeId 
            WHERE pt.PuzzleId = @puzzleId
            ORDER BY t.DisplayName";
        cmd.Parameters.AddWithValue("@puzzleId", puzzleId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            themes.Add(new Theme
            {
                ThemeId = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return themes;
    }

    private static Puzzle ReadPuzzle(SqliteDataReader reader)
    {
        return new Puzzle
        {
            PuzzleId = reader.GetString(0),
            Fen = reader.GetString(1),
            Moves = reader.GetString(2),
            Rating = reader.GetInt32(3),
            RatingDeviation = reader.GetInt32(4),
            Popularity = reader.GetInt32(5),
            NbPlays = reader.GetInt32(6),
            GameUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
            OpeningTags = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    public int GetPuzzleCount()
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Puzzles";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        // Clear connection pool to release file locks
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        GC.SuppressFinalize(this);
    }
}
