using Jupiter.LichessPuzzles.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jupiter.LichessPuzzles.App.Views;

public sealed partial class PuzzlePane : UserControl
{
    public PuzzlePane(PuzzleViewModel viewModel, CoreHost host)
    {
        ViewModel = viewModel;
        Host = host;
        InitializeComponent();
        ViewModel.Moves.CollectionChanged += (_, _) => ScrollToCurrent();
    }

    public PuzzleViewModel ViewModel { get; }

    private CoreHost Host { get; }

    public void FocusFirst() => NextButton.Focus(FocusState.Programmatic);

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Shown(MoveCell? cell) => cell is { IsMove: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "…" shown in the White column when a row starts with Black's move.</summary>
    public static Visibility Placeholder(MoveCell? cell) => cell is { IsMove: false } ? Visibility.Visible : Visibility.Collapsed;

    public static Brush CurrentBackground(bool current) => current
        ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void ScrollToCurrent()
    {
        var current = ViewModel.Moves.LastOrDefault(r => r.White?.IsCurrent == true || r.Black?.IsCurrent == true);
        if (current is not null)
        {
            MoveList.ScrollIntoView(current);
        }
    }

    private void OnOpenLichess(object sender, RoutedEventArgs e) => Host.OpenLink(ViewModel.PuzzleLink);
}

public sealed partial class MoveRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Move { get; set; }

    public DataTemplate? Marker { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is MoveRow { IsMarker: true } ? Marker : Move;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
