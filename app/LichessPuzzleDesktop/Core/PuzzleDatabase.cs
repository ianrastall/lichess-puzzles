using LichessPuzzleDesktop.Interop;

namespace LichessPuzzleDesktop.Core;

/// <summary>The indexed puzzle CSV. Long operations run on a worker thread and report progress.</summary>
public sealed class PuzzleDatabase : IDisposable
{
    private nint _handle = Native.lpd_db_create();

    public int Count => Native.lpd_db_count(_handle);
    public int FilteredCount => Native.lpd_db_filtered_count(_handle);

    public async Task LoadAsync(string path, IProgress<double>? progress, CancellationToken cancel)
    {
        nint h = _handle;
        int rc = await RunAsync(() => Native.lpd_db_load(h, path), progress, cancel);
        cancel.ThrowIfCancellationRequested();
        if (rc != Native.Ok)
            throw new CoreException(rc == -3 ? "The file does not look like a Lichess puzzle CSV." : $"Could not read \"{path}\".");
    }

    public unsafe PuzzleInfo Get(int row)
    {
        LpdPuzzleInfo raw;
        if (Native.lpd_db_get(_handle, row, &raw) != Native.Ok) throw new CoreException($"Row {row} could not be read.");
        return new PuzzleInfo(raw);
    }

    /// <summary>Row of the puzzle with this id, or -1.</summary>
    public int FindId(string id)
    {
        int row = Native.lpd_db_find_id(_handle, id);
        return row < 0 ? -1 : row;
    }

    public unsafe IReadOnlyList<NamedCount> Themes()
    {
        nint h = _handle;
        var list = new List<NamedCount>();
        for (int i = 0, n = Native.lpd_db_theme_count(h); i < n; i++)
        {
            int index = i;
            list.Add(new(Native.GetText((buf, cap) => Native.lpd_db_theme_name(h, index, buf, cap)), Native.lpd_db_theme_puzzles(h, i)));
        }
        return list;
    }

    public unsafe IReadOnlyList<NamedCount> Openings()
    {
        nint h = _handle;
        var list = new List<NamedCount>();
        for (int i = 0, n = Native.lpd_db_opening_count(h); i < n; i++)
        {
            int index = i;
            list.Add(new(Native.GetText((buf, cap) => Native.lpd_db_opening_name(h, index, buf, cap)), Native.lpd_db_opening_puzzles(h, i)));
        }
        return list;
    }

    public Task<int> FilterAsync(PuzzleFilter filter, PuzzleHistory? history)
    {
        nint h = _handle;
        nint exclude = filter.SkipPlayed && history is not null ? history.Handle : 0;
        string themes = string.Join(' ', filter.Themes);
        return Task.Run(() => Native.lpd_db_filter(h, filter.RatingMin, filter.RatingMax, filter.PopularityMin,
            filter.PlaysMin, themes, filter.Opening, exclude));
    }

    /// <summary>A random row from the filtered set, or -1 when nothing matches.</summary>
    public int RandomRow()
    {
        int row = Native.lpd_db_random_row(_handle);
        return row < 0 ? -1 : row;
    }

    /// <summary>A random filtered row rated within <paramref name="spread"/> of the target (nearest if none), or -1.</summary>
    public int RandomRowNear(int targetRating, int spread)
    {
        int row = Native.lpd_db_random_row_near(_handle, targetRating, spread);
        return row < 0 ? -1 : row;
    }

    /// <summary>Writes puzzles as PGN. <paramref name="rows"/> null = the filtered set. Returns the number written.</summary>
    public async Task<int> ExportPgnAsync(string path, int[]? rows, bool fromPlayerMove, IProgress<double>? progress, CancellationToken cancel)
    {
        nint h = _handle;
        int flags = fromPlayerMove ? Native.PgnPlayerStart : 0;
        int rc = await RunAsync(() =>
        {
            unsafe { fixed (int* p = rows) return Native.lpd_db_export_pgn(h, path, p, rows?.Length ?? 0, 0, flags); }
        }, progress, cancel);
        return CheckExport(rc, path, cancel);
    }

    public async Task<int> ExportCsvAsync(string path, int[]? rows, IProgress<double>? progress, CancellationToken cancel)
    {
        nint h = _handle;
        int rc = await RunAsync(() =>
        {
            unsafe { fixed (int* p = rows) return Native.lpd_db_export_csv(h, path, p, rows?.Length ?? 0, 0); }
        }, progress, cancel);
        return CheckExport(rc, path, cancel);
    }

    private static int CheckExport(int rc, string path, CancellationToken cancel)
    {
        if (rc == Native.ErrCancelled) throw new OperationCanceledException(cancel);
        if (rc < 0) throw new CoreException($"Could not write \"{path}\".");
        return rc;
    }

    // Runs a blocking native call on the thread pool while polling its progress counter.
    private async Task<int> RunAsync(Func<int> call, IProgress<double>? progress, CancellationToken cancel)
    {
        nint h = _handle;
        using var registration = cancel.Register(() => Native.lpd_db_cancel(h));
        Task<int> work = Task.Run(call);
        while (await Task.WhenAny(work, Task.Delay(80)) != work)
            progress?.Report(Native.lpd_db_progress(h) / 1000.0);
        return await work;
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Native.lpd_db_destroy(_handle);
        _handle = 0;
    }
}
