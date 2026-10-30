using Google.Protobuf;
using Jupiter.LichessPuzzles.Messages;

namespace Jupiter.LichessPuzzles.Interop;

public enum PuzzleErrorKind
{
    Transport,
    NoStore,
    Busy,
    NotFound,
    NoMatches,
    Selection,
    InvalidFilter,
    State,
    Io,
    UnknownCommand,
    Player,
}

public sealed class PuzzleCoreException(PuzzleErrorKind kind, string message) : Exception(message)
{
    public PuzzleErrorKind Kind { get; } = kind;

    internal static PuzzleCoreException From(Error error) => new(
        error.Kind switch
        {
            ErrorKind.NoStore => PuzzleErrorKind.NoStore,
            ErrorKind.Busy => PuzzleErrorKind.Busy,
            ErrorKind.NotFound => PuzzleErrorKind.NotFound,
            ErrorKind.NoMatches => PuzzleErrorKind.NoMatches,
            ErrorKind.Selection => PuzzleErrorKind.Selection,
            ErrorKind.InvalidFilter => PuzzleErrorKind.InvalidFilter,
            ErrorKind.State => PuzzleErrorKind.State,
            ErrorKind.Io => PuzzleErrorKind.Io,
            ErrorKind.UnknownCommand => PuzzleErrorKind.UnknownCommand,
            ErrorKind.Player => PuzzleErrorKind.Player,
            _ => PuzzleErrorKind.State,
        },
        error.Message);
}

/// <summary>
/// Typed access to the Rust puzzle core. Calls may come from any thread. Filter and puzzle
/// queries scan the store (about 15 ms) and <see cref="Dispose"/> waits for a running operation,
/// so those belong off the UI thread.
/// </summary>
public sealed class PuzzleCore : IDisposable
{
    private readonly CoreSafeHandle _handle;
    private long _nextRequestId;

    private PuzzleCore(CoreSafeHandle handle) => _handle = handle;

    /// <summary>Loads the native core, checks its ABI version and opens the data folder.</summary>
    /// <param name="sourceUrl">Download URL; empty for the Lichess puzzle database.</param>
    /// <param name="defaultPlayer">Name of the first player in a new player database.</param>
    public static unsafe PuzzleCore Create(string dataDir, string sourceUrl = "", string defaultPlayer = "")
    {
        uint version;
        try
        {
            version = NativeMethods.lpz_abi_version();
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new PuzzleCoreException(PuzzleErrorKind.Transport, $"The puzzle core (lichess_puzzles_ffi.dll) could not be loaded: {e.Message}");
        }
        if (version != NativeMethods.AbiVersion)
        {
            throw new PuzzleCoreException(PuzzleErrorKind.Transport, $"The puzzle core has ABI version {version}; this app needs {NativeMethods.AbiVersion}.");
        }

        var config = new CreateConfig { DataDir = dataDir, SourceUrl = sourceUrl, DefaultPlayer = defaultPlayer }.ToByteArray();
        NativeStatus status;
        CoreSafeHandle core;
        ResultSafeHandle error;
        fixed (byte* ptr = config)
        {
            status = NativeMethods.lpz_create(ptr, (nuint)config.Length, out core, out error);
        }
        using (error)
        {
            if (status == NativeStatus.Ok && !core.IsInvalid)
            {
                return new PuzzleCore(core);
            }
            core.Dispose();
            if (!error.IsInvalid && Response.Parser.ParseFrom(error.CopyBytes()).Error is { } e)
            {
                throw PuzzleCoreException.From(e);
            }
            throw new PuzzleCoreException(PuzzleErrorKind.Transport, $"The puzzle core could not start ({status}).");
        }
    }

    private unsafe Response Call(Request request)
    {
        request.RequestId = (ulong)Interlocked.Increment(ref _nextRequestId);
        var bytes = request.ToByteArray();
        NativeStatus status;
        ResultSafeHandle result;
        fixed (byte* ptr = bytes)
        {
            status = NativeMethods.lpz_call(_handle, ptr, (nuint)bytes.Length, out result);
        }
        using (result)
        {
            if (status != NativeStatus.Ok)
            {
                throw new PuzzleCoreException(PuzzleErrorKind.Transport, $"The puzzle core rejected a call ({status}).");
            }
            var response = Response.Parser.ParseFrom(result.CopyBytes());
            if (response.RequestId != request.RequestId)
            {
                throw new PuzzleCoreException(PuzzleErrorKind.Transport, "The puzzle core answered a different request.");
            }
            if (response.ResultCase == Response.ResultOneofCase.Error)
            {
                throw PuzzleCoreException.From(response.Error);
            }
            return response;
        }
    }

    public Status GetStatus() => Call(new Request { GetStatus = new GetStatus() }).Status;

    public Catalogue GetCatalogue() => Call(new Request { GetCatalogue = new GetCatalogue() }).Catalogue;

    /// <summary>Replaces the current filter; returns the matching counts.</summary>
    public Counts SetFilter(Filter filter, bool skipPlayed = false) =>
        Call(new Request { SetFilter = new SetFilter { Filter = filter, SkipPlayed = skipPlayed } }).Counts;

    /// <summary>Starts a random puzzle matching the current filter, optionally near the player's rating plus an offset.</summary>
    public SessionResult NextPuzzle(bool nearRating = false, int offset = 0, int ratingWindow = 150, int excludeShortMates = 0) =>
        Call(new Request { NextPuzzle = Selection(nearRating, offset, ratingWindow, excludeShortMates) }).Session;

    public void PrepareNext(bool nearRating, int offset, int ratingWindow, int excludeShortMates) =>
        Call(new Request { PrepareNext = Selection(nearRating, offset, ratingWindow, excludeShortMates) });

    private static NextPuzzle Selection(bool near, int offset, int window, int mates) => new()
    {
        NearRating = near, Offset = offset, RatingWindow = (uint)Math.Clamp(window, 1, 600),
        ExcludeShortMates = (uint)Math.Clamp(mates, 0, 2),
    };

    /// <summary>Starts a puzzle by id or lichess.org training link.</summary>
    public SessionResult LoadPuzzle(string id, bool replay = false) =>
        Call(new Request { LoadPuzzle = new LoadPuzzle { Id = id, Replay = replay } }).Session;

    public SessionResult GetSession() => Session(new SessionCommand { Get = new Empty() });

    public SessionResult PlayOpponent() => Session(new SessionCommand { PlayOpponent = new Empty() });

    public SessionResult MakeMove(string uci) => Session(new SessionCommand { MakeMove = uci });

    public SessionResult Hint() => Session(new SessionCommand { Hint = new Empty() });

    public SessionResult ShowSolution() => Session(new SessionCommand { ShowSolution = new Empty() });

    public SessionResult Retry() => Session(new SessionCommand { Retry = new Empty() });

    public SessionResult Navigate(int ply) => Session(new SessionCommand { Navigate = (uint)Math.Max(0, ply) });

    private SessionResult Session(SessionCommand command) => Call(new Request { Session = command }).Session;

    public void StartDownload(bool keepDownload) =>
        Call(new Request { StartDownload = new StartDownload { KeepDownload = keepDownload } });

    /// <summary>Builds from a .csv or .csv.zst file, or from the kept download when <paramref name="input"/> is empty.</summary>
    public void StartBuild(string input, bool keepDownload) =>
        Call(new Request { StartBuild = new StartBuild { Input = input, KeepDownload = keepDownload } });

    public void CheckForUpdate() => Call(new Request { CheckForUpdate = new CheckForUpdate() });

    public void PauseDownload() => Call(new Request { PauseDownload = new PauseDownload() });

    public void CancelOperation() => Call(new Request { CancelOperation = new CancelOperation() });

    public void SwitchStore() => Call(new Request { SwitchStore = new SwitchStore() });

    /// <summary>Players, the current player and their totals; <paramref name="dayStart"/> starts "today".</summary>
    public PlayerList GetPlayers(DateTimeOffset dayStart) =>
        Call(new Request { GetPlayers = new GetPlayers { DayStartMs = dayStart.ToUnixTimeMilliseconds() } }).Players;

    public void SetCurrentPlayer(long id) => Call(new Request { SetCurrentPlayer = new SetCurrentPlayer { Id = id } });

    public Player AddPlayer(string name) => Call(new Request { AddPlayer = new AddPlayer { Name = name } }).Player;

    public Player RenamePlayer(long id, string name) =>
        Call(new Request { RenamePlayer = new RenamePlayer { Id = id, Name = name } }).Player;

    public void DeletePlayer(long id) => Call(new Request { DeletePlayer = new DeletePlayer { Id = id } });

    public void ClearHistory(long id) => Call(new Request { ClearHistory = new ClearHistory { Id = id } });

    /// <summary>Whether puzzles started from now on are rated.</summary>
    public void SetRated(bool rated) => Call(new Request { SetRated = new SetRated { Rated = rated } });

    /// <summary>The current player's history, newest first; 0 means all.</summary>
    public IReadOnlyList<HistoryEntry> GetHistory(uint limit = 0) =>
        Call(new Request { GetHistory = new GetHistory { Limit = limit } }).History.Entries;

    public string GetPuzzlePgn(bool playerStart) =>
        Call(new Request { GetPuzzlePgn = new GetPuzzlePgn { PlayerStart = playerStart } }).Text.Text_;

    /// <summary>Starts writing puzzles to <paramref name="path"/>; returns how many will be written.</summary>
    public ulong StartExport(ExportSource source, ExportFormat format, bool playerStart, string path) =>
        Call(new Request { StartExport = new StartExport { Source = source, Format = format, PlayerStart = playerStart, Path = path } }).Started.Count;

    public void StartAnalysis(string fen, TimeSpan limit) =>
        Call(new Request { StartAnalysis = new StartAnalysis { Fen = fen, Millis = (uint)Math.Clamp(limit.TotalMilliseconds, 100, 60000) } });

    public Analysis GetAnalysis() => Call(new Request { GetAnalysis = new GetAnalysis() }).Analysis;

    public void StopAnalysis() => Call(new Request { StopAnalysis = new StopAnalysis() });

    public IReadOnlyList<Event> PollEvents(uint max = 100)
    {
        var status = NativeMethods.lpz_poll_events(_handle, max, out var result);
        using (result)
        {
            if (status != NativeStatus.Ok)
            {
                throw new PuzzleCoreException(PuzzleErrorKind.Transport, $"Reading events failed ({status}).");
            }
            return EventBatch.Parser.ParseFrom(result.CopyBytes()).Events;
        }
    }

    /// <summary>Stops a running operation (a download stays resumable), waits for it and releases the core.</summary>
    public void Dispose() => _handle.Dispose();
}
