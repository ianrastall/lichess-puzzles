using System.Runtime.InteropServices;
using System.Text;

namespace LichessPuzzleDesktop.Interop;

// Mirrors of the structs in core/include/lpd_core.h. All are blittable.

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LpdPuzzleInfo
{
    public fixed byte Id[16];
    public fixed byte Fen[96];
    public fixed byte Moves[512];
    public fixed byte Themes[512];
    public fixed byte GameUrl[128];
    public fixed byte OpeningTags[256];
    public int Rating;
    public int RatingDeviation;
    public int Popularity;
    public int NbPlays;
    public long DailyDate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LpdPlyInfo
{
    public fixed byte San[16];
    public fixed byte Uci[8];
    public int From;
    public int To;
    public int MoveNumber;
    public int Color;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct SessionState
{
    public int Status;
    public int PlayerColor;
    public int PlyCount;
    public int TotalPlies;
    public int View;
    public int SideToMove;
    public int LastFrom;
    public int LastTo;
    public int CheckSquare;
    public int Mistakes;
    public int Hints;
    public int SolutionShown;
    public int CanMove;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LpdEngineInfo
{
    public int Running;
    public int Depth;
    public int ScoreCp;
    public int Mate;
    public long Nodes;
    public int TimeMs;
    public int Generation;
    public fixed byte BestUci[8];
    public fixed byte PvSan[256];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LpdHistoryEntry
{
    public fixed byte Id[16];
    public long TimestampMs;
    public int Rating;
    public int Solved;
    public int Mistakes;
    public int Hints;
    public int Seconds;
    public int PlayerRating;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LpdRating
{
    public double Rating;
    public double Deviation;
    public double Volatility;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LpdPlayerInfo
{
    public int Id;
    public int Games;
    public int Wins;
    public int Reserved;
    public LpdRating Rating;
    public long CreatedMs;
    public fixed byte Name[64];
}

internal static unsafe partial class Native
{
    private const string Dll = "lpd_core";

    public const int AbiVersion = 2;

    public const int Ok = 0;
    public const int ErrIo = -2;
    public const int ErrCancelled = -4;
    public const int ErrNotFound = -5;

    public const int PgnPlayerStart = 1;

    [LibraryImport(Dll)] public static partial int lpd_abi_version();

    // Database
    [LibraryImport(Dll)] public static partial nint lpd_db_create();
    [LibraryImport(Dll)] public static partial void lpd_db_destroy(nint db);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial int lpd_db_load(nint db, string path);
    [LibraryImport(Dll)] public static partial int lpd_db_progress(nint db);
    [LibraryImport(Dll)] public static partial void lpd_db_cancel(nint db);
    [LibraryImport(Dll)] public static partial int lpd_db_count(nint db);
    [LibraryImport(Dll)] public static partial int lpd_db_get(nint db, int row, LpdPuzzleInfo* info);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial int lpd_db_find_id(nint db, string id);
    [LibraryImport(Dll)] public static partial int lpd_db_theme_count(nint db);
    [LibraryImport(Dll)] public static partial int lpd_db_theme_name(nint db, int index, byte* buf, int cap);
    [LibraryImport(Dll)] public static partial int lpd_db_theme_puzzles(nint db, int index);
    [LibraryImport(Dll)] public static partial int lpd_db_opening_count(nint db);
    [LibraryImport(Dll)] public static partial int lpd_db_opening_name(nint db, int index, byte* buf, int cap);
    [LibraryImport(Dll)] public static partial int lpd_db_opening_puzzles(nint db, int index);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int lpd_db_filter(nint db, int ratingMin, int ratingMax, int popularityMin, int playsMin,
        string? themes, string? opening, nint excludePlayed);
    [LibraryImport(Dll)] public static partial int lpd_db_filtered_count(nint db);
    [LibraryImport(Dll)] public static partial int lpd_db_random_row(nint db);
    [LibraryImport(Dll)] public static partial int lpd_db_random_row_near(nint db, int targetRating, int spread);

    // Export
    [LibraryImport(Dll)] public static partial int lpd_puzzle_pgn(LpdPuzzleInfo* puzzle, int flags, byte* buf, int cap);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int lpd_db_export_pgn(nint db, string path, int* rows, int rowCount, int maxCount, int flags);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int lpd_db_export_csv(nint db, string path, int* rows, int rowCount, int maxCount);

    // Session
    [LibraryImport(Dll)] public static partial nint lpd_session_create();
    [LibraryImport(Dll)] public static partial void lpd_session_destroy(nint session);
    [LibraryImport(Dll)] public static partial int lpd_session_load(nint session, LpdPuzzleInfo* puzzle);
    [LibraryImport(Dll)] public static partial int lpd_session_retry(nint session);
    [LibraryImport(Dll)] public static partial int lpd_session_state_get(nint session, SessionState* state);
    [LibraryImport(Dll)] public static partial int lpd_session_board(nint session, byte* board64);
    [LibraryImport(Dll)] public static partial int lpd_session_fen(nint session, byte* buf, int cap);
    [LibraryImport(Dll)] public static partial int lpd_session_ply(nint session, int index, LpdPlyInfo* ply);
    [LibraryImport(Dll)] public static partial ulong lpd_session_legal_targets(nint session, int from);
    [LibraryImport(Dll)] public static partial int lpd_session_is_promotion(nint session, int from, int to);
    [LibraryImport(Dll)] public static partial int lpd_session_try_move(nint session, int from, int to, int promotion);
    [LibraryImport(Dll)] public static partial int lpd_session_hint(nint session);
    [LibraryImport(Dll)] public static partial int lpd_session_reveal_next(nint session);
    [LibraryImport(Dll)] public static partial int lpd_session_reveal_all(nint session);
    [LibraryImport(Dll)] public static partial int lpd_session_goto(nint session, int view);
    [LibraryImport(Dll)] public static partial int lpd_session_pgn(nint session, int flags, byte* buf, int cap);

    // Engine
    [LibraryImport(Dll)] public static partial nint lpd_engine_create();
    [LibraryImport(Dll)] public static partial void lpd_engine_destroy(nint engine);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int lpd_engine_start(nint engine, string fen, int maxDepth, int movetimeMs);
    [LibraryImport(Dll)] public static partial void lpd_engine_stop(nint engine);
    [LibraryImport(Dll)] public static partial int lpd_engine_poll(nint engine, LpdEngineInfo* info);

    // History
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial nint lpd_history_open(string? path);
    [LibraryImport(Dll)] public static partial void lpd_history_close(nint history);
    [LibraryImport(Dll)] public static partial int lpd_history_add(nint history, LpdHistoryEntry* entry);
    [LibraryImport(Dll)] public static partial int lpd_history_count(nint history);
    [LibraryImport(Dll)] public static partial int lpd_history_get(nint history, int index, LpdHistoryEntry* entry);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial int lpd_history_contains(nint history, string id);
    [LibraryImport(Dll)] public static partial int lpd_history_clear(nint history);

    // Players and ratings
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial nint lpd_players_open(string? path);
    [LibraryImport(Dll)] public static partial void lpd_players_close(nint players);
    [LibraryImport(Dll)] public static partial int lpd_players_count(nint players);
    [LibraryImport(Dll)] public static partial int lpd_players_get(nint players, int index, LpdPlayerInfo* info);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial int lpd_players_add(nint players, string name, long createdMs);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial int lpd_players_rename(nint players, int id, string name);
    [LibraryImport(Dll)] public static partial int lpd_players_remove(nint players, int id);
    [LibraryImport(Dll)]
    public static partial int lpd_players_record(nint players, int id, double puzzleRating, double puzzleDeviation, int solved, LpdPlayerInfo* info);

    /// <summary>Reads a NUL-terminated UTF-8 field of at most <paramref name="capacity"/> bytes.</summary>
    public static string Text(byte* field, int capacity)
    {
        var span = new ReadOnlySpan<byte>(field, capacity);
        int end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    public static void SetText(byte* field, int capacity, string value)
    {
        var span = new Span<byte>(field, capacity);
        span.Clear();
        Encoding.UTF8.GetBytes(value.AsSpan(0, Math.Min(value.Length, capacity - 1)), span[..(capacity - 1)]);
    }

    public delegate int TextCall(byte* buf, int cap);

    /// <summary>Calls a "(buf, cap) returns required length" function, growing the buffer if needed.</summary>
    public static string GetText(TextCall call)
    {
        byte* small = stackalloc byte[1024];
        int needed = call(small, 1024);
        if (needed < 0) return string.Empty;
        if (needed < 1024) return Encoding.UTF8.GetString(small, needed);

        byte[] big = new byte[needed + 1];
        fixed (byte* p = big)
        {
            needed = Math.Min(call(p, big.Length), needed);
            return needed < 0 ? string.Empty : Encoding.UTF8.GetString(p, needed);
        }
    }
}
