using Jupiter.LichessPuzzles.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Jupiter.LichessPuzzles.App.Views;

/// <summary>Player commands shown in the player menu; the window carries them out.</summary>
public enum PlayerCommand
{
    New,
    Rename,
    Delete,
    ClearHistory,
}

public sealed partial class PuzzlePane : UserControl
{
    public PuzzlePane(PuzzleViewModel puzzle, PlayerViewModel player, HistoryViewModel history, EvaluationViewModel evaluation, CoreHost host)
    {
        ViewModel = puzzle;
        Player = player;
        History = history;
        Evaluation = evaluation;
        Host = host;
        InitializeComponent();
        ViewModel.Moves.CollectionChanged += (_, _) => ScrollToCurrent();
    }

    public PuzzleViewModel ViewModel { get; }

    public PlayerViewModel Player { get; }

    public HistoryViewModel History { get; }

    public EvaluationViewModel Evaluation { get; }

    private CoreHost Host { get; }

    /// <summary>Raised for New, Rename, Delete and Clear history in the player menu.</summary>
    public event Action<PlayerCommand>? PlayerCommandRequested;

    public bool ShowingHistory => Views.SelectedItem == HistoryItem;

    public void ShowPuzzle() => Views.SelectedItem = PuzzleItem;

    public void ShowHistory() => Views.SelectedItem = HistoryItem;

    public void FocusFirst()
    {
        if (ShowingHistory)
        {
            HistoryList.Focus(FocusState.Programmatic);
        }
        else
        {
            NextButton.Focus(FocusState.Programmatic);
        }
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Shown(MoveCell? cell) => cell is { IsMove: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "…" shown in the White column when a row starts with Black's move.</summary>
    public static Visibility Placeholder(MoveCell? cell) => cell is { IsMove: false } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The move on the board is the selection in the move list (standard 3.2).</summary>
    public static Brush CurrentBackground(bool current) => current
        ? Jupiter.UI.JupiterTheme.Brush("JupiterSelectedBrush")
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static string HistoryTooltip(string mistakes, string hints, string time) => $"Mistakes {mistakes} · hints {hints} · time {time}";

    private void OnViewChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var history = sender.SelectedItem == HistoryItem;
        PuzzleView.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryView.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        if (history)
        {
            _ = History.RefreshAsync();
        }
    }

    private void ScrollToCurrent()
    {
        var current = ViewModel.Moves.LastOrDefault(r => r.White?.IsCurrent == true || r.Black?.IsCurrent == true);
        if (current is not null)
        {
            MoveList.ScrollIntoView(current);
        }
    }

    private void OnOpenLichess(object sender, RoutedEventArgs e) => Host.OpenLink(ViewModel.PuzzleLink);

    private void OnPlayerMenuOpening(object sender, object e)
    {
        PlayerMenu.Items.Clear();
        foreach (var p in Player.Players)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{p.Name} ({p.Rating:0}{(p.Provisional ? "?" : "")})",
                GroupName = "players",
                IsChecked = p.Id == Player.Current?.Id,
                Tag = p.Id,
            };
            item.Click += async (s, _) => await Player.SwitchToAsync((long)((FrameworkElement)s).Tag);
            PlayerMenu.Items.Add(item);
        }
        PlayerMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (text, command) in new[]
        {
            ("New player…", PlayerCommand.New),
            ("Rename player…", PlayerCommand.Rename),
            ("Delete player…", PlayerCommand.Delete),
            ("Clear history…", PlayerCommand.ClearHistory),
        })
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = command != PlayerCommand.Delete || Player.Players.Count > 1 };
            item.Click += (_, _) => PlayerCommandRequested?.Invoke(command);
            PlayerMenu.Items.Add(item);
        }
    }

    private void OnSortHistory(object sender, RoutedEventArgs e) => History.SortBy((string)((FrameworkElement)sender).Tag);

    private async Task OpenReplayAsync(string id)
    {
        var problem = await ViewModel.LoadAsync(id, replay: true);
        if (problem is null)
        {
            ShowPuzzle();
        }
        else
        {
            Host.ReportProblem?.Invoke(problem);
        }
    }

    private async void OnHistoryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryRow row)
        {
            await OpenReplayAsync(row.PuzzleId);
        }
    }

    private async void OnHistoryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && HistoryList.SelectedItem is HistoryRow row)
        {
            e.Handled = true;
            await OpenReplayAsync(row.PuzzleId);
        }
    }

    private async void OnHistoryOpen(object sender, RoutedEventArgs e) => await OpenReplayAsync((string)((FrameworkElement)sender).Tag);

    private void OnHistoryLichess(object sender, RoutedEventArgs e) =>
        Host.OpenLink($"https://lichess.org/training/{(string)((FrameworkElement)sender).Tag}");

    private void OnHistoryCopy(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText((string)((FrameworkElement)sender).Tag);
        Clipboard.SetContent(package);
    }
}

public sealed partial class MoveRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Move { get; set; }

    public DataTemplate? Marker { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is MoveRow { IsMarker: true } ? Marker : Move;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
