using LichessPuzzleDesktop.Interop;

namespace LichessPuzzleDesktop.Core;

/// <summary>The built-in background analysis engine.</summary>
public sealed unsafe class AnalysisEngine : IDisposable
{
    private nint _handle = Native.lpd_engine_create();

    /// <summary>Starts analysing a position, replacing any running search. Returns false for an invalid FEN.</summary>
    public bool Start(string fen, int maxDepth, int movetimeMs) =>
        Native.lpd_engine_start(_handle, fen, maxDepth, movetimeMs) == Native.Ok;

    public void Stop() => Native.lpd_engine_stop(_handle);

    public EngineInfo Poll()
    {
        LpdEngineInfo info;
        Native.lpd_engine_poll(_handle, &info);
        return new EngineInfo(info.Running != 0, info.Depth, info.ScoreCp, info.Mate, info.Nodes, info.TimeMs,
            info.Generation, Native.Text(info.BestUci, 8), Native.Text(info.PvSan, 256));
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Native.lpd_engine_destroy(_handle);
        _handle = 0;
    }
}
