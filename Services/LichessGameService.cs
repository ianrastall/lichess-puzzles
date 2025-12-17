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
    /// Fetches a game from Lichess by game ID and returns the moves in UCI format.
    /// </summary>
    public async Task<GameData?> GetGameAsync(string gameUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract game ID from URL (e.g., "https://lichess.org/ABC123#25" -> "ABC123")
            var gameId = ExtractGameId(gameUrl);
            if (string.IsNullOrEmpty(gameId))
                return null;

            // Fetch PGN from Lichess API
            var pgnUrl = $"game/export/{gameId}?pgnInJson=false&clocks=false&evals=false";
            var response = await _httpClient.GetAsync(pgnUrl, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
                return null;

            var pgn = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParsePgn(pgn, gameUrl);
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
            
            // Remove comments {}, variations (), result, and move numbers
            movesText = CommentsRegex().Replace(movesText, "");
            movesText = VariationsRegex().Replace(movesText, "");
            movesText = ResultRegex().Replace(movesText, "");
            movesText = MoveNumbersRegex().Replace(movesText, " ");
            movesText = MultipleSpacesRegex().Replace(movesText, " ").Trim();

            // Split into individual moves (SAN notation from PGN)
            var sanMoves = movesText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();

            gameData.Moves = sanMoves;
        }

        return gameData;
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

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex VariationsRegex();

    [GeneratedRegex(@"(1-0|0-1|1/2-1/2|\*)")]
    private static partial Regex ResultRegex();

    [GeneratedRegex(@"\d+\.+")]
    private static partial Regex MoveNumbersRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex PlyRegex();
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
    public List<string> Moves { get; set; } = [];
}
