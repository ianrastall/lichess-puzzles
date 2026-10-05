using LichessPuzzleDesktop.Interop;

namespace LichessPuzzleDesktop.Core;

/// <summary>Persistent log of attempted puzzles.</summary>
public sealed unsafe class PuzzleHistory(string? path) : IDisposable
{
    private nint _handle = Native.lpd_history_open(path);

    internal nint Handle => _handle;
    public int Count => Native.lpd_history_count(_handle);

    public void Add(HistoryEntry entry)
    {
        LpdHistoryEntry raw = default;
        Native.SetText(raw.Id, 16, entry.Id);
        raw.TimestampMs = entry.Time.ToUnixTimeMilliseconds();
        raw.Rating = entry.Rating;
        raw.Solved = entry.Solved ? 1 : 0;
        raw.Mistakes = entry.Mistakes;
        raw.Hints = entry.Hints;
        raw.Seconds = entry.Seconds;
        Native.lpd_history_add(_handle, &raw);
    }

    public IReadOnlyList<HistoryEntry> Entries()
    {
        var list = new List<HistoryEntry>();
        LpdHistoryEntry raw;
        for (int i = 0; Native.lpd_history_get(_handle, i, &raw) == Native.Ok; i++)
        {
            list.Add(new HistoryEntry(Native.Text(raw.Id, 16), DateTimeOffset.FromUnixTimeMilliseconds(raw.TimestampMs),
                raw.Rating, raw.Solved != 0, raw.Mistakes, raw.Hints, raw.Seconds));
        }
        return list;
    }

    public void Clear() => Native.lpd_history_clear(_handle);

    public void Dispose()
    {
        if (_handle == 0) return;
        Native.lpd_history_close(_handle);
        _handle = 0;
    }
}
