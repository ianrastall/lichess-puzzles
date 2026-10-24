using Jupiter.LichessPuzzles.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jupiter.LichessPuzzles.App.Views;

public sealed partial class FilterPane : UserControl
{
    public FilterPane(FilterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public FilterViewModel ViewModel { get; }

    /// <summary>Raised by the close button shown while the pane is an overlay.</summary>
    public event Action? CloseRequested;

    public bool IsOverlay
    {
        get => CloseOverlayButton.Visibility == Visibility.Visible;
        set => CloseOverlayButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Moves keyboard focus into the pane.</summary>
    public void FocusFirst() => RatingFrom.Focus(FocusState.Programmatic);

    public Visibility NotAvailable(bool available) => available ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private void OnThemeClick(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case ThemeRow row:
                row.Cycle();
                break;
            case ThemeGroupRow group:
                ViewModel.ToggleGroup(group);
                break;
        }
    }

    private void OnOpeningTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = ViewModel.Suggest(sender.Text);
        }
    }

    private void OnOpeningChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is OpeningSuggestion suggestion)
        {
            ViewModel.AddOpening(suggestion.Tag);
            sender.Text = "";
            sender.ItemsSource = null;
        }
    }

    private void OnOpeningSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var suggestion = args.ChosenSuggestion as OpeningSuggestion ?? ViewModel.Suggest(args.QueryText).FirstOrDefault();
        if (suggestion is not null)
        {
            ViewModel.AddOpening(suggestion.Tag);
            sender.Text = "";
            sender.ItemsSource = null;
        }
    }

    private void OnRemoveOpening(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OpeningToken token })
        {
            ViewModel.RemoveOpeningCommand.Execute(token);
        }
    }

    private void OnCloseOverlay(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}

public sealed partial class ThemeRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Group { get; set; }

    public DataTemplate? Theme { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is ThemeGroupRow ? Group : Theme;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
