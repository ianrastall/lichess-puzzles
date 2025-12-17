using System.IO;
using System.Net.Http;
using Lichess_Puzzles;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Lichess_Puzzles.Services;

public class PuzzleDatabaseService : IDisposable
{
    private const string PuzzleDbUrl = "https://database.lichess.org/lichess_db_puzzle.csv.zst";
    private const string DatabaseFileName = "chess_puzzles.db";
    
    private static readonly SemaphoreSlim _databaseLock = new(1, 1);
    
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    
    public event Action<string>? StatusChanged;
    public event Action<double>? ProgressChanged;
    public event Action<string>? LogMessage;
    
    public PuzzleDatabaseService()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(Constants.HttpTimeoutSeconds); // Long timeout for large download
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LichessPuzzlesApp/1.0");
    }
    
    private void Log(string message)
    {
        LogMessage?.Invoke(message);
    }
    
    public static string GetDatabasePath()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LichessPuzzles");
        
        Directory.CreateDirectory(appDataPath);
        return Path.Combine(appDataPath, DatabaseFileName);
    }
    
    public static bool DatabaseExists()
    {
        var dbPath = GetDatabasePath();
        return File.Exists(dbPath) && new FileInfo(dbPath).Length > 0;
    }
    
    public static DateTime? GetDatabaseDate()
    {
        var dbPath = GetDatabasePath();
        if (!File.Exists(dbPath)) return null;
        return File.GetLastWriteTime(dbPath);
    }
    
    public void Cancel()
    {
        _cancellationTokenSource?.Cancel();
    }
    
    public async Task<bool> DownloadAndCreateDatabaseAsync(CancellationToken externalToken = default)
    {
        // Prevent concurrent database operations
        if (!await _databaseLock.WaitAsync(0, externalToken))
        {
            Log("Another database operation is already in progress");
            StatusChanged?.Invoke("Another operation is in progress...");
            return false;
        }
        
        try
        {
            return await DownloadAndCreateDatabaseInternalAsync(externalToken);
        }
        finally
        {
            _databaseLock.Release();
        }
    }
    
    private async Task<bool> DownloadAndCreateDatabaseInternalAsync(CancellationToken externalToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = _cancellationTokenSource.Token;
        
        var dbPath = GetDatabasePath();
        var tempDbPath = dbPath + ".tmp";
        var tempZstPath = Path.Combine(Path.GetTempPath(), $"lichess_puzzles_{Guid.NewGuid():N}.csv.zst");
        
        Log($"Target database: {dbPath}");
        Log($"Temp ZST file: {tempZstPath}");
        Log($"Temp DB file: {tempDbPath}");
        
        try
        {
            // Clean up any existing temp files first
            Log("Cleaning up any existing temp files...");
            CleanupFile(tempDbPath);
            CleanupFile(tempZstPath);
            
            // Step 1: Download the ZST file COMPLETELY
            Log($"Starting download from {PuzzleDbUrl}");
            StatusChanged?.Invoke("Downloading puzzle database (~100 MB)...");
            await DownloadFileCompletelyAsync(PuzzleDbUrl, tempZstPath, token);
            
            if (token.IsCancellationRequested) 
            {
                Log("Download cancelled by user");
                CleanupFile(tempZstPath);
                return false;
            }
            
            // Verify download completed
            var downloadedFile = new FileInfo(tempZstPath);
            Log($"Download complete. File size: {downloadedFile.Length:N0} bytes");
            
            StatusChanged?.Invoke("Download complete. Processing...");
            ProgressChanged?.Invoke(50);
            
            // Step 2: Now that download is 100% complete, process the file
            Log("Starting database creation from downloaded file...");
            await CreateDatabaseFromZstFileAsync(tempZstPath, tempDbPath, token);
            
            if (token.IsCancellationRequested)
            {
                Log("Database creation cancelled by user");
                CleanupFile(tempDbPath);
                CleanupFile(tempZstPath);
                return false;
            }
            
            // Step 3: Replace old database with new one
            Log("Finalizing - replacing old database...");
            StatusChanged?.Invoke("Finalizing...");

            // Aggressively ensure SQLite releases file handles before move
            SqliteConnection.ClearAllPools();
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            await Task.Delay(1000, token);
            
            if (File.Exists(dbPath))
            {
                Log($"Deleting existing database: {dbPath}");
                File.Delete(dbPath);
            }
            
            // Retry the move operation a few times in case the file is still locked
            Log($"Moving temp database to final location...");
            var moveSuccess = false;
            Exception? lastException = null;
            
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    File.Move(tempDbPath, dbPath);
                    moveSuccess = true;
                    Log($"Move succeeded on attempt {attempt}");
                    break;
                }
                catch (IOException ex) when (attempt < 5)
                {
                    lastException = ex;
                    Log($"Move attempt {attempt} failed: {ex.Message}. Retrying in {attempt * 500}ms...");
                    
                    // Clear pools again and wait
                    SqliteConnection.ClearAllPools();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(attempt * 500, token);
                }
            }
            
            if (!moveSuccess)
            {
                throw new IOException($"Failed to move database file after 5 attempts", lastException);
            }
            
            // Clean up the ZST file
            Log("Cleaning up temp ZST file...");
            CleanupFile(tempZstPath);
            
            var finalSize = new FileInfo(dbPath).Length;
            Log($"Database created successfully. Size: {finalSize / 1024 / 1024:N0} MB");
            
            StatusChanged?.Invoke("Complete!");
            ProgressChanged?.Invoke(100);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log("Operation was cancelled");
            StatusChanged?.Invoke("Cancelled");
            CleanupFile(tempDbPath);
            CleanupFile(tempZstPath);
            return false;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Log($"  Inner exception: {ex.InnerException.Message}");
            }
            StatusChanged?.Invoke($"Error: {ex.Message}");
            CleanupFile(tempDbPath);
            CleanupFile(tempZstPath);
            throw;
        }
    }
    
    private void CleanupFile(string? path)
    {
        try
        {
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
                Log($"Cleaned up: {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            Log($"Warning: Could not delete {path}: {ex.Message}");
        }
    }
    
    private async Task DownloadFileCompletelyAsync(string url, string destinationPath, CancellationToken token)
    {
        Log("Sending HTTP request...");
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        Log($"Response received: {response.StatusCode}");
        
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        Log($"Content-Length: {totalBytes:N0} bytes ({totalBytes / 1024 / 1024:N0} MB)");
        
        var downloadedBytes = 0L;
        var tempDownloadPath = destinationPath + ".downloading";
        
        Log($"Downloading to temp file: {tempDownloadPath}");
        
        try
        {
            // Use explicit stream management to ensure proper cleanup
            var contentStream = await response.Content.ReadAsStreamAsync(token);
            try
            {
                var fileStream = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, Constants.FileBufferSize, FileOptions.SequentialScan);
                try
                {
                    var buffer = new byte[Constants.FileBufferSize];
                    int bytesRead;
                    var lastProgressUpdate = DateTime.UtcNow;
                    
                    while ((bytesRead = await contentStream.ReadAsync(buffer, token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                        downloadedBytes += bytesRead;
                        
                        // Update progress at most every ProgressUpdateIntervalMs to avoid UI overhead
                        if ((DateTime.UtcNow - lastProgressUpdate).TotalMilliseconds > Constants.ProgressUpdateIntervalMs)
                        {
                            var progressHandler = ProgressChanged;
                            var statusHandler = StatusChanged;

                            if (totalBytes > 0)
                            {
                                var progress = (double)downloadedBytes / totalBytes * 50.0;
                                progressHandler?.Invoke(progress);
                                statusHandler?.Invoke($"Downloading... ({downloadedBytes / 1024 / 1024:N0} MB / {totalBytes / 1024 / 1024:N0} MB)");
                            }
                            lastProgressUpdate = DateTime.UtcNow;
                        }
                    }
                    
                    // Ensure all data is flushed
                    await fileStream.FlushAsync(token);
                    Log($"Download complete: {downloadedBytes:N0} bytes written");
                }
                finally
                {
                    await fileStream.DisposeAsync();
                    Log("File stream closed");
                }
            }
            finally
            {
                await contentStream.DisposeAsync();
                Log("Content stream closed");
            }
            
            // Small delay to ensure file system has released the file
            await Task.Delay(100, token);
            
            // Now rename the completed download to the final path
            Log($"Renaming temp download to: {destinationPath}");
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(tempDownloadPath, destinationPath);
            Log("Rename complete");
        }
        finally
        {
            // Clean up temp download file if it still exists
            if (File.Exists(tempDownloadPath))
            {
                try
                {
                    File.Delete(tempDownloadPath);
                }
                catch { /* Ignore */ }
            }
        }
    }
    
    private async Task CreateDatabaseFromZstFileAsync(string zstPath, string dbPath, CancellationToken token)
    {
        const long estimatedTotalRows = Constants.EstimatedPuzzleCount;
        
        Log($"Opening ZST file: {zstPath}");
        
        // Small delay to ensure file is fully released
        await Task.Delay(200, token);
        
        await using var fileStream = new FileStream(zstPath, FileMode.Open, FileAccess.Read, FileShare.Read, Constants.FileBufferSize, FileOptions.SequentialScan);
        Log($"ZST file opened. Size: {fileStream.Length:N0} bytes");
        
        await using var decompressionStream = new DecompressionStream(fileStream);
        using var reader = new StreamReader(decompressionStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536);
        
        SqliteConnection? connection = null;
        try
        {
            // Read header line
            var header = await reader.ReadLineAsync(token);
            if (string.IsNullOrEmpty(header))
                throw new InvalidDataException("CSV file is empty");
            Log($"CSV header: {header[..Math.Min(100, header.Length)]}...");
            
            // Create database connection
            Log($"Creating database: {dbPath}");
            connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync(token);
            Log("Database connection opened");
            
            await CreateSchemaAsync(connection, token);
            Log("Schema created");
            
            // Process in batches for performance
            const int batchSize = Constants.DatabaseBatchSize;
            var rowCount = 0L;
            var batch = new List<PuzzleRow>(batchSize);
            var allThemes = new HashSet<string>();
            var lastLogTime = DateTime.UtcNow;
            
            string? line;
            while ((line = await reader.ReadLineAsync(token)) != null && !token.IsCancellationRequested)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                var row = ParseCsvLine(line);
                if (row != null)
                {
                    batch.Add(row);
                    foreach (var theme in row.Themes)
                        allThemes.Add(theme);
                    
                    if (batch.Count >= batchSize)
                    {
                        InsertBatch(connection, batch, token);
                        rowCount += batch.Count;
                        batch.Clear();
                        
                        var progress = 50 + (double)rowCount / estimatedTotalRows * 40;
                        ProgressChanged?.Invoke(Math.Min(progress, 90));
                        StatusChanged?.Invoke($"Processing puzzles... ({rowCount:N0} / ~{estimatedTotalRows:N0})");
                        
                        // Log every ~10 seconds
                        if ((DateTime.UtcNow - lastLogTime).TotalSeconds > 10)
                        {
                            Log($"Progress: {rowCount:N0} puzzles processed");
                            lastLogTime = DateTime.UtcNow;
                        }
                    }
                }
            }
            
            // Insert remaining rows
            if (batch.Count > 0 && !token.IsCancellationRequested)
            {
                InsertBatch(connection, batch, token);
                rowCount += batch.Count;
            }
            
            if (token.IsCancellationRequested)
            {
                Log("Cancellation requested during processing");
                return;
            }
            
            Log($"All puzzles processed: {rowCount:N0} total");
            
            // Insert themes
            StatusChanged?.Invoke($"Creating theme index ({allThemes.Count} themes)...");
            ProgressChanged?.Invoke(92);
            Log($"Inserting {allThemes.Count} themes...");
            InsertThemes(connection, allThemes, token);
            
            // Create indexes
            StatusChanged?.Invoke("Creating indexes (this may take a moment)...");
            ProgressChanged?.Invoke(95);
            Log("Creating indexes...");
            await CreateIndexesAsync(connection, token);
            Log("Indexes created");
            
            StatusChanged?.Invoke($"Database created with {rowCount:N0} puzzles");
        }
        finally
        {
            if (connection != null)
            {
                await connection.CloseAsync();
                await connection.DisposeAsync();
                Log("Database connection closed");
                
                // CRITICAL: Clear SQLite connection pool to release file locks
                SqliteConnection.ClearAllPools();
                Log("SQLite connection pools cleared");
                
                // Force garbage collection to release any remaining handles
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                // Wait a bit for Windows to fully release the file lock
                await Task.Delay(500);
                Log("Waited for file lock release");
            }
        }
    }
    
    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = OFF;
            PRAGMA journal_mode = OFF;
            PRAGMA synchronous = OFF;
            PRAGMA cache_size = -64000;
            PRAGMA temp_store = MEMORY;
            PRAGMA locking_mode = EXCLUSIVE;
            
            CREATE TABLE IF NOT EXISTS Puzzles (
                PuzzleId TEXT PRIMARY KEY,
                Fen TEXT NOT NULL,
                Moves TEXT NOT NULL,
                Rating INTEGER NOT NULL,
                RatingDeviation INTEGER NOT NULL,
                Popularity INTEGER NOT NULL,
                NbPlays INTEGER NOT NULL,
                GameUrl TEXT,
                OpeningTags TEXT
            );
            
            CREATE TABLE IF NOT EXISTS Themes (
                ThemeId TEXT PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                Description TEXT
            );
            
            CREATE TABLE IF NOT EXISTS PuzzleThemes (
                PuzzleId TEXT NOT NULL,
                ThemeId TEXT NOT NULL,
                PRIMARY KEY (PuzzleId, ThemeId)
            );
            """;
        await cmd.ExecuteNonQueryAsync(token);
    }
    
    private static async Task CreateIndexesAsync(SqliteConnection connection, CancellationToken token)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_Puzzles_Rating ON Puzzles(Rating);
            CREATE INDEX IF NOT EXISTS IX_PuzzleThemes_ThemeId ON PuzzleThemes(ThemeId);
            CREATE INDEX IF NOT EXISTS IX_PuzzleThemes_PuzzleId ON PuzzleThemes(PuzzleId);
            """;
        await cmd.ExecuteNonQueryAsync(token);
    }
    
    private static void InsertBatch(SqliteConnection connection, List<PuzzleRow> batch, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        
        using var puzzleCmd = connection.CreateCommand();
        puzzleCmd.Transaction = transaction;
        puzzleCmd.CommandText = """
            INSERT OR REPLACE INTO Puzzles (PuzzleId, Fen, Moves, Rating, RatingDeviation, Popularity, NbPlays, GameUrl, OpeningTags)
            VALUES ($id, $fen, $moves, $rating, $ratingDev, $popularity, $nbPlays, $gameUrl, $openingTags)
            """;
        
        var pId = puzzleCmd.CreateParameter(); pId.ParameterName = "$id"; puzzleCmd.Parameters.Add(pId);
        var pFen = puzzleCmd.CreateParameter(); pFen.ParameterName = "$fen"; puzzleCmd.Parameters.Add(pFen);
        var pMoves = puzzleCmd.CreateParameter(); pMoves.ParameterName = "$moves"; puzzleCmd.Parameters.Add(pMoves);
        var pRating = puzzleCmd.CreateParameter(); pRating.ParameterName = "$rating"; puzzleCmd.Parameters.Add(pRating);
        var pRatingDev = puzzleCmd.CreateParameter(); pRatingDev.ParameterName = "$ratingDev"; puzzleCmd.Parameters.Add(pRatingDev);
        var pPopularity = puzzleCmd.CreateParameter(); pPopularity.ParameterName = "$popularity"; puzzleCmd.Parameters.Add(pPopularity);
        var pNbPlays = puzzleCmd.CreateParameter(); pNbPlays.ParameterName = "$nbPlays"; puzzleCmd.Parameters.Add(pNbPlays);
        var pGameUrl = puzzleCmd.CreateParameter(); pGameUrl.ParameterName = "$gameUrl"; puzzleCmd.Parameters.Add(pGameUrl);
        var pOpeningTags = puzzleCmd.CreateParameter(); pOpeningTags.ParameterName = "$openingTags"; puzzleCmd.Parameters.Add(pOpeningTags);
        
        using var themeCmd = connection.CreateCommand();
        themeCmd.Transaction = transaction;
        themeCmd.CommandText = "INSERT OR IGNORE INTO PuzzleThemes (PuzzleId, ThemeId) VALUES ($puzzleId, $themeId)";
        
        var tPuzzleId = themeCmd.CreateParameter(); tPuzzleId.ParameterName = "$puzzleId"; themeCmd.Parameters.Add(tPuzzleId);
        var tThemeId = themeCmd.CreateParameter(); tThemeId.ParameterName = "$themeId"; themeCmd.Parameters.Add(tThemeId);
        
        foreach (var row in batch)
        {
            if (token.IsCancellationRequested) break;
            
            pId.Value = row.PuzzleId;
            pFen.Value = row.Fen;
            pMoves.Value = row.Moves;
            pRating.Value = row.Rating;
            pRatingDev.Value = row.RatingDeviation;
            pPopularity.Value = row.Popularity;
            pNbPlays.Value = row.NbPlays;
            pGameUrl.Value = row.GameUrl ?? (object)DBNull.Value;
            pOpeningTags.Value = row.OpeningTags ?? (object)DBNull.Value;
            puzzleCmd.ExecuteNonQuery();
            
            tPuzzleId.Value = row.PuzzleId;
            foreach (var theme in row.Themes)
            {
                tThemeId.Value = theme;
                themeCmd.ExecuteNonQuery();
            }
        }
        
        transaction.Commit();
    }
    
    private static void InsertThemes(SqliteConnection connection, HashSet<string> themes, CancellationToken token)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Themes (ThemeId, DisplayName) VALUES ($id, $name)";
        
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        
        foreach (var theme in themes)
        {
            if (token.IsCancellationRequested) break;
            
            pId.Value = theme;
            pName.Value = FormatThemeName(theme);
            cmd.ExecuteNonQuery();
        }
    }
    
    private static string FormatThemeName(string themeId)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(themeId, "([a-z])([A-Z])", "$1 $2");
        return char.ToUpper(result[0]) + result[1..];
    }
    
    private static PuzzleRow? ParseCsvLine(string line)
    {
        try
        {
            var parts = SplitCsvLine(line);
            if (parts.Length < 8) return null;
            
            return new PuzzleRow
            {
                PuzzleId = parts[0],
                Fen = parts[1],
                Moves = parts[2],
                Rating = int.Parse(parts[3]),
                RatingDeviation = int.Parse(parts[4]),
                Popularity = int.Parse(parts[5]),
                NbPlays = int.Parse(parts[6]),
                Themes = parts[7].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                GameUrl = parts.Length > 8 && !string.IsNullOrWhiteSpace(parts[8]) ? parts[8] : null,
                OpeningTags = parts.Length > 9 && !string.IsNullOrWhiteSpace(parts[9]) ? parts[9] : null
            };
        }
        catch
        {
            return null;
        }
    }
    
    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                // Escaped quote inside a quoted field
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++; // Skip the escaped quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
    }
    
    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
    
    private class PuzzleRow
    {
        public required string PuzzleId { get; init; }
        public required string Fen { get; init; }
        public required string Moves { get; init; }
        public required int Rating { get; init; }
        public required int RatingDeviation { get; init; }
        public required int Popularity { get; init; }
        public required int NbPlays { get; init; }
        public List<string> Themes { get; init; } = [];
        public string? GameUrl { get; init; }
        public string? OpeningTags { get; init; }
    }
}
