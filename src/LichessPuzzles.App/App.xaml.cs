using Jupiter.LichessPuzzles.App.Controls;
using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.App.ViewModels;
using Jupiter.LichessPuzzles.Interop;
using Jupiter.LichessPuzzles.Messages;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Jupiter.LichessPuzzles.App;

public partial class App : Application
{
    private DispatcherQueueTimer? _timer;
    private EventWaitHandle? _showSignal;
    private string _storeKey = "";
    private bool _exiting;
    private bool _checkedForUpdate;

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

    public AccentService Accent { get; private set; } = null!;

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
        Filter.CountsChanged += () => Puzzle.FilterHasMatches = Filter.HasMatches;

        Accent = new AccentService(Settings, dispatcher);
        Window = new MainWindow(this);
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
            var core = await Task.Run(() => PuzzleCore.Create(DataDirectory));
            if (_exiting)
            {
                await Task.Run(core.Dispose);
                return;
            }
            Host.Attach(core);
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
            if (!_checkedForUpdate && status.Store is not null && status.Operation is null)
            {
                _checkedForUpdate = true;
                if (Settings.Value.CheckForUpdatesAtStart)
                {
                    await Host.Try(c => c.CheckForUpdate(), "Checking for an update failed");
                }
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
