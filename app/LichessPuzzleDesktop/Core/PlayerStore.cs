using LichessPuzzleDesktop.Interop;

namespace LichessPuzzleDesktop.Core;

/// <summary>The persistent list of players and their Glicko-2 ratings.</summary>
public sealed unsafe class PlayerStore(string? path) : IDisposable
{
    private nint _handle = Native.lpd_players_open(path);

    public int Count => Native.lpd_players_count(_handle);

    public IReadOnlyList<PlayerInfo> All()
    {
        var list = new List<PlayerInfo>();
        LpdPlayerInfo raw;
        for (int i = 0; Native.lpd_players_get(_handle, i, &raw) == Native.Ok; i++) list.Add(Convert(raw));
        return list;
    }

    /// <summary>Adds a player and returns it, or null when the name is empty or already taken.</summary>
    public PlayerInfo? Add(string name)
    {
        int id = Native.lpd_players_add(_handle, name, DateTimeOffset.Now.ToUnixTimeMilliseconds());
        return id > 0 ? All().FirstOrDefault(p => p.Id == id) : null;
    }

    /// <summary>Renames a player and returns it, or null when the name is empty or already taken.</summary>
    public PlayerInfo? Rename(int id, string name) =>
        Native.lpd_players_rename(_handle, id, name) == Native.Ok ? All().FirstOrDefault(p => p.Id == id) : null;

    public bool Remove(int id) => Native.lpd_players_remove(_handle, id) == Native.Ok;

    /// <summary>
    /// Rates one attempt as a game between the player and the puzzle. Returns the updated player,
    /// or null if the player does not exist.
    /// </summary>
    public PlayerInfo? Record(int id, PuzzleInfo puzzle, bool solved)
    {
        LpdPlayerInfo raw;
        int rc = Native.lpd_players_record(_handle, id, puzzle.Rating, puzzle.RatingDeviation, solved ? 1 : 0, &raw);
        // A failed save (LPD_ERR_IO) still updated the rating in memory.
        return rc == Native.Ok || rc == Native.ErrIo ? Convert(raw) : null;
    }

    private static PlayerInfo Convert(in LpdPlayerInfo raw)
    {
        LpdPlayerInfo copy = raw;
        return new PlayerInfo(copy.Id, Native.Text(copy.Name, 64), copy.Rating.Rating, copy.Rating.Deviation,
            copy.Rating.Volatility, copy.Games, copy.Wins);
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Native.lpd_players_close(_handle);
        _handle = 0;
    }
}
