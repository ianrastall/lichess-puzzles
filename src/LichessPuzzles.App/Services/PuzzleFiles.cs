namespace Jupiter.LichessPuzzles.App.Services;

/// <summary>Finds user-managed Lichess files without requiring download metadata.</summary>
internal static class PuzzleFiles
{
    public const string DownloadPage = "https://database.lichess.org/#puzzles";

    public static string? Find(string appDirectory, string dataDirectory)
    {
        // Prefer the copy deliberately placed beside the executable, then legacy data locations.
        foreach (var directory in new[] { appDirectory, dataDirectory, Path.Combine(dataDirectory, "source") })
        {
            foreach (var name in new[] { "lichess_db_puzzle.csv.zst", "lichess_db_puzzle.csv" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }
}
