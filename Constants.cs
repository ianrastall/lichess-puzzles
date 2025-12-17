namespace Lichess_Puzzles;

internal static class Constants
{
    public const int FileBufferSize = 81920; // 80KB default buffer for file streams
    public const int DatabaseBatchSize = 50000;
    public const long EstimatedPuzzleCount = 5_600_000;
    public const int HttpTimeoutSeconds = 3600;
    public const int ProgressUpdateIntervalMs = 100;
}
