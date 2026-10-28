using Jupiter.LichessPuzzles.App.Controls;
using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.App.ViewModels;
using Jupiter.LichessPuzzles.Interop;
using Jupiter.LichessPuzzles.Messages;
using Jupiter.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Jupiter.LichessPuzzles.App;

public partial class App : Application
{
    private DispatcherQueueTimer? _timer;
    private EventWaitHandle? _showSignal;
    private string _storeKey = "";
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Status.Report($"Unexpected error: {e.Message}");
        };
    }

    public static new App Current => (App)Application.Current;

    /// <summary>Settings, state and puzzles; LICHESS_PUZZLES_DATA_DIR overrides it for development and testing.</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("LICHESS_PUZZLES_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jupiter", "LichessPuzzles");

    public CoreHost Host { get; } = new();

    public JsonStore<AppSettings> Settings { get; private set; } = null!;

    public JsonStore<UiState> Ui { get; private set; } = null!;

    public StatusViewModel Status { get; } = new();

    public FilterViewModel Filter { get; private set; } = null!;

    public PuzzleViewModel Puzzle { get; private set; } = null!;

    public PlayerViewModel Player { get; private set; } = null!;

    public HistoryViewModel History { get; private set; } = null!;

    public EvaluationViewModel Evaluation { get; private set; } = null!;

    public AccentService Accent { get; private set; } = null!;

    public ThemeService Theme { get; private set; } = null!;

    public MainWindow Window { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(DataDirectory);
        Settings = new JsonStore<AppSettings>(Path.Combine(DataDirectory, "settings.json"), PreferencesJson.Default.AppSettings, s => s.Version, AppSettings.CurrentVersion);
        Ui = new JsonStore<UiState>(Path.Combine(DataDirectory, "ui.json"), PreferencesJson.Default.UiState, s => s.Version, UiState.CurrentVersion);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        Host.ReportProblem = message => dispatcher.TryEnqueue(() => Status.Report(message));
        Filter = new FilterViewModel(Host, Settings, Ui.Value, dispatcher);
        Puzzle = new PuzzleViewModel(Host, Settings, Ui.Value, dispatcher);
        Player = new PlayerViewModel(Host, Settings);
        History = new HistoryViewModel(Host);
        Evaluation = new EvaluationViewModel(Host, Settings);
        Filter.CountsChanged += () =>
        {
            Puzzle.FilterHasMatches = Filter.HasMatches;
            Window?.UpdateCommands();
        };
        Puzzle.RatingSettled += (before, after) => Player.ShowChange(before, after);
        Puzzle.Recorded += () => _ = OnRecordedAsync();
        Puzzle.PositionShown += (fen, finished) => Evaluation.Show(fen, finished);
        Player.Changed += () => _ = OnPlayerChangedAsync();

        Accent = new AccentService(new AccentPreferences(Settings));
        Theme = new ThemeService(new ThemePreferences(Settings));
        Window = new MainWindow(this);
        Theme.Attach(Window);
        Theme.Changed += Window.OnAccentChanged;
        Accent.Attach(Window.RootElement);
        Accent.Changed += Window.OnAccentChanged;
        Window.Activate();
        ListenForShowRequests(dispatcher);

        try
        {
            await ChessBoard.LoadPiecesAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status.Report($"The piece images could not be loaded: {e.Message}");
        }
        await OpenCoreAsync();

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(400);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
    }

    private async Task OpenCoreAsync()
    {
        try
        {
            var core = await Task.Run(() => PuzzleCore.Create(DataDirectory, "", Environment.UserName));
            if (_exiting)
            {
                await Task.Run(core.Dispose);
                return;
            }
            Host.Attach(core);
            var rated = Settings.Value.Rated;
            await Host.Try(c => c.SetRated(rated), "The rated setting could not be applied");
            await Player.RefreshAsync();
            if (core.GetStatus().Store is null && PuzzleFiles.Find(AppContext.BaseDirectory, DataDirectory) is { } input)
            {
                await Host.Try(c => c.StartBuild(input, keepDownload: true), "The local puzzle file could not be imported");
            }
            await RefreshAsync();
        }
        catch (PuzzleCoreException e)
        {
            Status.Report($"The puzzle core could not start: {Format.Sentence(e.Message)}");
        }
    }

    private bool _refreshing;

    private async Task RefreshAsync()
    {
        if (_refreshing || Host.Core is not { } core)
        {
            return;
        }
        _refreshing = true;
        try
        {
            var (status, events) = await Task.Run(() => (core.GetStatus(), core.PollEvents()));
            await Evaluation.PollAsync();
            Status.Apply(status);
            foreach (var e in events)
            {
                Status.AddEvent(e);
            }
            Window.UpdateCommands();
            var key = status.Store is { } s ? $"{s.Path}|{s.BuiltAtUnix}|{s.PuzzleCount}|{s.FileBytes}" : "";
            if (key != _storeKey)
            {
                _storeKey = key;
                await OnStoreChangedAsync(status.Store is not null);
            }
        }
        catch (PuzzleCoreException e)
        {
            _timer?.Stop();
            Status.Report($"The puzzle core stopped responding: {Format.Sentence(e.Message)}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Loads the catalogue and a puzzle when a store opens; clears them when it closes.</summary>
    private async Task OnStoreChangedAsync(bool available)
    {
        Puzzle.StoreAvailable = available;
        if (!available)
        {
            Filter.Unload();
            Puzzle.Clear();
            return;
        }
        Catalogue? catalogue;
        try
        {
            catalogue = await Host.Run(core => core.GetCatalogue());
        }
        catch (PuzzleCoreException e)
        {
            Status.Report($"The puzzle themes could not be read: {Format.Sentence(e.Message)}");
            return;
        }
        if (catalogue is null)
        {
            return;
        }
        Puzzle.ThemeNames = catalogue.Themes.ToDictionary(t => t.Key, t => t.Name);
        var families = catalogue.Families.ToDictionary(f => f.Id, f => f.Name);
        Puzzle.OpeningNames = catalogue.Families.Select(f => (f.Tag, f.Name))
            .Concat(catalogue.Variations.Select(v => (v.Tag, v.Name)))
            .ToDictionary(p => p.Item1, p => p.Item2);
        Filter.Load(catalogue);
        await Filter.ApplyAsync();
        if (Puzzle.HasPuzzle)
        {
            Puzzle.Refresh();
            return;
        }
        if (!await Puzzle.RestoreAsync())
        {
            await Puzzle.NextAsync();
        }
    }

    private string _playerKey = "";

    /// <summary>After a mistake, hint, solve or new puzzle: totals, history and played puzzles may have changed.</summary>
    private async Task OnRecordedAsync()
    {
        await Player.RefreshAsync();
        if (Window?.ShowingHistory == true)
        {
            await History.RefreshAsync();
        }
        if (Settings.Value.Filter.SkipPlayed)
        {
            await Filter.ApplyAsync();
        }
    }

    /// <summary>A different player has their own played puzzles, rating target and history.</summary>
    private async Task OnPlayerChangedAsync()
    {
        Filter.PlayerRating = Player.Rating;
        var key = Player.Current is { } p ? $"{p.Id}" : "";
        if (key == _playerKey)
        {
            return;
        }
        _playerKey = key;
        if (Puzzle.HasPuzzle)
        {
            var error = await Puzzle.LoadAsync(Puzzle.PuzzleId);
            if (error is not null)
            {
                Status.Report(error);
            }
        }
        await Filter.ApplyAsync();
        if (Window?.ShowingHistory == true)
        {
            await History.RefreshAsync();
        }
    }

    /// <summary>A second launch signals this event to bring the window forward.</summary>
    private void ListenForShowRequests(DispatcherQueue dispatcher)
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        var signal = _showSignal;
        var thread = new Thread(() =>
        {
            while (signal.WaitOne() && !_exiting)
            {
                dispatcher.TryEnqueue(Window.ShowAndActivate);
            }
        })
        { IsBackground = true, Name = "show-requests" };
        thread.Start();
    }

    /// <summary>Saves state, stops a running operation (a download stays resumable), releases the core and exits.</summary>
    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _timer?.Stop();
        Window.SaveLayout();
        Evaluation.Hide();
        Settings.Save();
        Ui.Save();
        Window.AppWindow.Hide();
        _showSignal?.Set();
        if (Host.Detach() is { } core)
        {
            await Task.Run(core.Dispose);
        }
        Window.AllowClose = true;
        Window.Close();
        Exit();
    }
}
