using System.Net.Http;
using System.Text.RegularExpressions;

namespace Lichess_Puzzles.Services;

public partial class LichessGameService : IDisposable
{
    private const string LichessApiBaseUrl = "https://lichess.org/";
    private readonly HttpClient _httpClient;

    public LichessGameService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(LichessApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/x-chess-pgn");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LichessPuzzlesApp/1.0 (+https://github.com/lichess-puzzles)");
    }

    /// <summary>
    /// Fetches a game from Lichess by game ID and returns the moves with annotations.
    /// </summary>
    public async Task<GameData?> GetGameAsync(string gameUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract game ID from URL (e.g., "https://lichess.org/ABC123#25" -> "ABC123")
            var gameId = ExtractGameId(gameUrl);
            if (string.IsNullOrEmpty(gameId))
                return null;

            // Fetch PGN from Lichess API with annotations enabled
            // literate=true adds text annotations, evals=true includes engine evaluations
            var pgnUrl = $"game/export/{gameId}?pgnInJson=false&clocks=false&evals=true&literate=true&opening=true";
            var response = await _httpClient.GetAsync(pgnUrl, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
                return null;

            var pgn = await response.Content.ReadAsStringAsync(cancellationToken);
            var gameData = ParsePgn(pgn, gameUrl);
            if (gameData != null)
                gameData.RawPgn = pgn;
            return gameData;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractGameId(string gameUrl)
    {
        if (string.IsNullOrEmpty(gameUrl))
            return null;

        // Handle various Lichess URL formats
        // https://lichess.org/ABC123
        // https://lichess.org/ABC123#25
        // https://lichess.org/ABC123/white
        // https://lichess.org/ABC123/black#25
        var match = GameIdRegex().Match(gameUrl);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static GameData? ParsePgn(string pgn, string gameUrl)
    {
        var gameData = new GameData { GameUrl = gameUrl };

        // Extract headers
        var headers = HeaderRegex().Matches(pgn);
        foreach (Match header in headers)
        {
            var key = header.Groups[1].Value;
            var value = header.Groups[2].Value;

            switch (key)
            {
                case "White":
                    gameData.WhitePlayer = value;
                    break;
                case "Black":
                    gameData.BlackPlayer = value;
                    break;
                case "Result":
                    gameData.Result = value;
                    break;
                case "WhiteElo":
                    if (int.TryParse(value, out var whiteElo))
                        gameData.WhiteElo = whiteElo;
                    break;
                case "BlackElo":
                    if (int.TryParse(value, out var blackElo))
                        gameData.BlackElo = blackElo;
                    break;
                case "Event":
                    gameData.Event = value;
                    break;
                case "Date":
                    gameData.Date = value;
                    break;
                case "TimeControl":
                    gameData.TimeControl = value;
                    break;
                case "ECO":
                    gameData.Eco = value;
                    break;
                case "Opening":
                    gameData.Opening = value;
                    break;
            }
        }

        // Extract moves (everything after the headers)
        var movesSection = MovesSectionRegex().Match(pgn);
        if (movesSection.Success)
        {
            var movesText = movesSection.Groups[1].Value;
            
            // Parse moves with their associated comments and NAGs
            gameData.Moves = ParseMovesWithAnnotations(movesText);
        }

        return gameData;
    }
    
    private static List<MoveData> ParseMovesWithAnnotations(string movesText)
    {
        var moves = new List<MoveData>();
        
        // Remove variations (parentheses) - we don't handle them yet
        // Handle nested parentheses by repeatedly removing innermost variations
        movesText = RemoveNestedVariations(movesText);
        
        // Remove result at the end
        movesText = ResultRegex().Replace(movesText, "");
        
        // Split into tokens (preserving comments and NAGs)
        var tokens = TokenizeMovesRegex().Matches(movesText);
        
        string? currentMove = null;
        string? currentComment = null;
        string? currentNag = null;
        
        foreach (Match token in tokens)
        {
            var text = token.Value.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            
            // Check if it's a comment
            if (text.StartsWith('{') && text.EndsWith('}'))
            {
                currentComment = text[1..^1].Trim();
                continue;
            }
            
            // Check if it's a NAG (Numeric Annotation Glyph like $1, $2, etc.)
            if (text.StartsWith('$') && int.TryParse(text[1..], out var nagNum))
            {
                currentNag = ConvertNagToSymbol(nagNum);
                continue;
            }
            
            // Check if it's a move number (skip it)
            if (MoveNumbersRegex().IsMatch(text))
            {
                continue;
            }
            
            // Validate that this looks like a chess move before accepting it
            // Valid moves start with: piece letters (KQRBN), file letters (a-h), or castling (O-O)
            if (!IsLikelyChessMove(text))
            {
                continue;
            }
            
            // It's a move - save the previous one if exists
            if (currentMove != null)
            {
                moves.Add(new MoveData 
                { 
                    San = currentMove,
                    Comment = currentComment,
                    Nag = currentNag
                });
                currentComment = null;
                currentNag = null;
            }
            
            currentMove = text;
        }
        
        // Don't forget the last move
        if (currentMove != null)
        {
            moves.Add(new MoveData 
            { 
                San = currentMove,
                Comment = currentComment,
                Nag = currentNag
            });
        }
        
        return moves;
    }
    
    /// <summary>
    /// Checks if a token looks like it could be a valid chess move in SAN notation.
    /// </summary>
    private static bool IsLikelyChessMove(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        
        // Remove trailing annotation symbols for checking (including Unicode variants)
        var clean = StripAnnotationSuffix(text);
        if (string.IsNullOrEmpty(clean)) return false;
        
        // Castling
        if (clean is "O-O" or "O-O-O" or "0-0" or "0-0-0")
            return true;
        
        // Must start with a piece letter (K, Q, R, B, N) or a file letter (a-h)
        char first = clean[0];
        if (!"KQRBNabcdefgh".Contains(first))
            return false;
        
        // Must contain at least one file letter and one rank digit
        bool hasFile = clean.Any(c => c >= 'a' && c <= 'h');
        bool hasRank = clean.Any(c => c >= '1' && c <= '8');
        
        return hasFile && hasRank;
    }
    
    /// <summary>
    /// Strips annotation symbols from the end of a move string.
    /// Handles !, ?, !!, ??, !?, ?!, +, #, and Unicode variants.
    /// Note: Does NOT strip '=' as it's used for promotion notation (e.g., e8=Q)
    /// </summary>
    private static string StripAnnotationSuffix(string move)
    {
        if (string.IsNullOrEmpty(move)) return move;
        
        // Keep stripping known annotation characters from the end
        // This handles combinations like "Nxe5?!" or "Qh7+!" 
        while (move.Length > 0)
        {
            char last = move[^1];
            // Standard annotations and check/mate symbols
            // Note: '=' is NOT included - it's used for promotion (e8=Q)
            if (last is '!' or '?' or '+' or '#' or 
                // Unicode annotation symbols
                '?' or '?' or '?' or '?' or
                // Other possible symbols (position evaluation)
                '?' or '?' or '?' or '±' or '?' or '?')
            {
                move = move[..^1];
            }
            else
            {
                break;
            }
        }
        
        return move;
    }
    
    private static string ConvertNagToSymbol(int nag)
    {
        return nag switch
        {
            1 => "!",      // Good move
            2 => "?",      // Poor move
            3 => "!!",     // Very good move
            4 => "??",     // Very poor move (blunder)
            5 => "!?",     // Speculative move
            6 => "?!",     // Questionable move
            7 => "?",      // Forced move
            10 => "=",     // Equal position
            13 => "?",     // Unclear position
            14 => "?",     // White has a slight advantage
            15 => "?",     // Black has a slight advantage
            16 => "±",     // White has a moderate advantage
            17 => "?",     // Black has a moderate advantage
            18 => "+?",    // White has a decisive advantage
            19 => "?+",    // Black has a decisive advantage
            _ => $"${nag}" // Unknown NAG, keep as-is
        };
    }

    /// <summary>
    /// Extracts the ply number from a Lichess game URL (e.g., "#25" -> 25)
    /// </summary>
    public static int? ExtractPlyFromUrl(string gameUrl)
    {
        if (string.IsNullOrEmpty(gameUrl))
            return null;

        var match = PlyRegex().Match(gameUrl);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var ply))
            return ply;

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"lichess\.org/(\w{8})")]
    private static partial Regex GameIdRegex();

    [GeneratedRegex(@"\[(\w+)\s+""([^""]*)""\]")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\]\s*\n\s*\n(.+)$", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex MovesSectionRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex CommentsRegex();

    // Removed VariationsRegex - using RemoveNestedVariations method instead for nested parens support
    
    /// <summary>
    /// Removes nested variations (parentheses) from PGN text by repeatedly removing innermost variations.
    /// </summary>
    private static string RemoveNestedVariations(string text)
    {
        // Keep removing innermost parentheses until none remain
        string previous;
        do
        {
            previous = text;
            text = SimpleParensRegex().Replace(text, "");
        } while (text != previous);
        
        return text;
    }
    
    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex SimpleParensRegex();

    [GeneratedRegex(@"(1-0|0-1|1/2-1/2|\*)")]
    private static partial Regex ResultRegex();

    [GeneratedRegex(@"\d+\.+")]
    private static partial Regex MoveNumbersRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex PlyRegex();

    [GeneratedRegex(@"\{[^}]*\}|\$\d+|\d+\.+|\S+")]
    private static partial Regex TokenizeMovesRegex();
}

public class GameData
{
    public string GameUrl { get; set; } = "";
    public string WhitePlayer { get; set; } = "";
    public string BlackPlayer { get; set; } = "";
    public int? WhiteElo { get; set; }
    public int? BlackElo { get; set; }
    public string Result { get; set; } = "";
    public string Event { get; set; } = "";
    public string Date { get; set; } = "";
    public string TimeControl { get; set; } = "";
    public string Eco { get; set; } = "";
    public string Opening { get; set; } = "";
    public List<MoveData> Moves { get; set; } = [];
    public string RawPgn { get; set; } = "";
}

public class MoveData
{
    public string San { get; set; } = "";
    public string? Comment { get; set; }
    public string? Nag { get; set; }
}
