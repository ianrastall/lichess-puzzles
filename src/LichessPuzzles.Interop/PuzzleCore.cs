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
    InvalidFilter,
    State,
    Io,
    UnknownCommand,
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
            ErrorKind.InvalidFilter => PuzzleErrorKind.InvalidFilter,
            ErrorKind.State => PuzzleErrorKind.State,
            ErrorKind.Io => PuzzleErrorKind.Io,
            ErrorKind.UnknownCommand => PuzzleErrorKind.UnknownCommand,
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
    public static unsafe PuzzleCore Create(string dataDir, string sourceUrl = "")
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

        var config = new CreateConfig { DataDir = dataDir, SourceUrl = sourceUrl }.ToByteArray();
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
    public Counts SetFilter(Filter filter) => Call(new Request { SetFilter = new SetFilter { Filter = filter } }).Counts;

    /// <summary>Starts a random puzzle matching the current filter.</summary>
    public SessionResult NextPuzzle() => Call(new Request { NextPuzzle = new NextPuzzle() }).Session;

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
