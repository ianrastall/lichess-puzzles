using LichessPuzzleDesktop.Interop;

namespace LichessPuzzleDesktop.Core;

/// <summary>The puzzle being played: solution checking, displayed position and notation.</summary>
public sealed unsafe class PuzzleSession : IDisposable
{
    private nint _handle = Native.lpd_session_create();

    public void Load(PuzzleInfo puzzle)
    {
        fixed (LpdPuzzleInfo* p = &puzzle.Raw)
        {
            if (Native.lpd_session_load(_handle, p) != Native.Ok)
                throw new CoreException($"Puzzle {puzzle.Id} has an invalid position or move list.");
        }
    }

    public void Retry() => Native.lpd_session_retry(_handle);

    public SessionState State
    {
        get
        {
            SessionState state;
            Native.lpd_session_state_get(_handle, &state);
            return state;
        }
    }

    /// <summary>64 piece letters indexed by square (a1 = 0), '.' for empty.</summary>
    public string Board()
    {
        byte* board = stackalloc byte[64];
        Native.lpd_session_board(_handle, board);
        return string.Create(64, (nint)board, static (chars, ptr) =>
        {
            for (int i = 0; i < 64; i++) chars[i] = (char)((byte*)ptr)[i];
        });
    }

    public string Fen()
    {
        nint h = _handle;
        return Native.GetText((buf, cap) => Native.lpd_session_fen(h, buf, cap));
    }

    public string Pgn(bool fromPlayerMove)
    {
        nint h = _handle;
        int flags = fromPlayerMove ? Native.PgnPlayerStart : 0;
        return Native.GetText((buf, cap) => Native.lpd_session_pgn(h, flags, buf, cap));
    }

    public IReadOnlyList<PlyInfo> Plies()
    {
        var list = new List<PlyInfo>();
        LpdPlyInfo ply;
        for (int i = 0; Native.lpd_session_ply(_handle, i, &ply) == Native.Ok; i++)
        {
            list.Add(new PlyInfo(i, Native.Text(ply.San, 16), Native.Text(ply.Uci, 8), ply.From, ply.To,
                ply.MoveNumber, ply.Color == 0, (ply.Flags & 8) != 0));
        }
        return list;
    }

    public ulong LegalTargets(int from) => Native.lpd_session_legal_targets(_handle, from);
    public bool IsPromotion(int from, int to) => Native.lpd_session_is_promotion(_handle, from, to) != 0;
    public MoveResult TryMove(int from, int to, char promotion) => (MoveResult)Native.lpd_session_try_move(_handle, from, to, promotion);

    /// <summary>Origin square of the next solution move, or -1.</summary>
    public int Hint() => Native.lpd_session_hint(_handle);
    public void RevealAll() => Native.lpd_session_reveal_all(_handle);
    public int GoTo(int view) => Native.lpd_session_goto(_handle, view);

    public void Dispose()
    {
        if (_handle == 0) return;
        Native.lpd_session_destroy(_handle);
        _handle = 0;
    }
}
