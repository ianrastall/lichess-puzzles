using Jupiter.LichessPuzzles.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Jupiter.LichessPuzzles.App.Views;

/// <summary>The app's small dialogs, built in code.</summary>
public static class Dialogs
{
    private static ContentDialog Create(XamlRoot root, string title, object content, string close = "Close") => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Close,
        RequestedTheme = ElementTheme.Dark,
    };

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string action)
    {
        var dialog = Create(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, "Keep working");
        dialog.PrimaryButtonText = action;
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task ShowGoToPuzzleAsync(XamlRoot root, PuzzleViewModel puzzle)
    {
        var box = new TextBox { Header = "Puzzle id or lichess.org link", PlaceholderText = "00sHx" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["JupiterErrorBrush"] };
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(box);
        panel.Children.Add(error);
        var dialog = Create(root, "Go to puzzle", panel, "Cancel");
        dialog.PrimaryButtonText = "Open puzzle";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.IsPrimaryButtonEnabled = false;
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            var problem = await puzzle.LoadAsync(box.Text.Trim());
            if (problem is not null)
            {
                args.Cancel = true;
                error.Text = problem;
                error.Visibility = Visibility.Visible;
            }
            deferral.Complete();
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        await dialog.ShowAsync();
    }

    public static async Task ShowDatabaseAsync(XamlRoot root, App app, MainWindow window)
    {
        var status = app.Status.Status;
        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        void Row(string label, string value)
        {
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FieldLabelStyle"] });
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            panel.Children.Add(grid);
        }
        void Heading(string text) => panel.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["PaneHeadingStyle"], Margin = new Thickness(0, 8, 0, 0) });

        Heading("Puzzle store");
        if (status?.Store is { } store)
        {
            Row("Puzzles", Format.Count(store.PuzzleCount));
            Row("Size", Format.Megabytes(store.FileBytes));
            Row("Built", Format.UnixDate(store.BuiltAtUnix));
            Row("Ratings", $"{store.RatingMin} to {store.RatingMax}");
            Row("Skipped rows", store.SkippedCount == 0 ? "None" : $"{Format.Count(store.SkippedCount)} (could not be read or replayed)");
            Heading("Source");
            Row("File", $"{store.SourceFile} ({Format.Megabytes(store.SourceBytes)})");
            if (store.SourceVersion is { } v)
            {
                Row("Lichess date", v.LastModified.Length > 0 ? Format.HttpDate(v.LastModified) : "Unknown");
                if (v.Etag.Length > 0)
                {
                    Row("ETag", v.Etag);
                }
            }
            else
            {
                Row("Lichess date", "Unknown; built from a local file");
            }
        }
        else
        {
            Row("State", status?.StoreProblem ?? "Unknown");
        }
        Row("Downloaded file", status?.Download is not null ? "Kept for rebuilding" : "Not kept");
        if (status?.UpdateAvailable is { } update)
        {
            Row("Update", $"Available ({Format.HttpDate(update.LastModified)})");
        }

        Heading("Data folder");
        var folder = status?.DataDir ?? App.DataDirectory;
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(new TextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center });
        var show = new Button { Content = "Show folder" };
        show.Click += (_, _) => app.Host.OpenFolder(folder);
        Grid.SetColumn(show, 1);
        folderRow.Children.Add(show);
        panel.Children.Add(folderRow);

        var running = app.Status.IsRunning;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var dialog = Create(root, "Database details", new ScrollViewer { Content = panel, MaxHeight = 520 });
        var check = new Button { Content = "Check for update", IsEnabled = !running };
        check.Click += async (_, _) =>
        {
            dialog.Hide();
            await app.Host.Try(c => c.CheckForUpdate(), "Checking for an update failed");
        };
        var rebuild = new Button { Content = "Rebuild store", IsEnabled = !running && status?.Download is not null };
        ToolTipService.SetToolTip(rebuild, status?.Download is not null ? "Build the store again from the downloaded file" : "Available when the downloaded file is kept");
        rebuild.Click += async (_, _) =>
        {
            dialog.Hide();
            await window.StartRebuildAsync();
        };
        var import = new Button { Content = "Import puzzle file…", IsEnabled = !running };
        import.Click += (_, _) =>
        {
            dialog.Hide();
            window.StartImport();
        };
        actions.Children.Add(check);
        actions.Children.Add(rebuild);
        actions.Children.Add(import);
        panel.Children.Add(actions);
        await dialog.ShowAsync();
    }

    public static async Task ShowShortcutsAsync(XamlRoot root)
    {
        (string Keys, string Action)[] rows =
        [
            ("Ctrl+N", "Next puzzle"),
            ("Ctrl+H", "Hint"),
            ("Ctrl+Shift+H", "Show solution"),
            ("Ctrl+R", "Retry"),
            ("Ctrl+G", "Go to puzzle"),
            ("Left / Right", "Back / forward one move"),
            ("Home / End", "First / latest position"),
            ("F", "Flip board"),
            ("Ctrl+Shift+F", "Show or hide filters"),
            ("Ctrl+Shift+C", "Copy FEN"),
            ("Ctrl+,", "Settings"),
            ("F6, Shift+F6", "Move between filters, board, puzzle and status bar"),
            ("Board: arrow keys", "Move the square cursor"),
            ("Board: Enter or Space", "Select a piece, or move it to the cursor"),
            ("Board: Ctrl+Left / Ctrl+Right", "Back / forward one move"),
            ("Board: Escape", "Clear the selection or close the promotion choice"),
            ("Promotion: Q, R, B, N", "Choose the piece"),
        ];
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = rows[i].Keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var action = new TextBlock { Text = rows[i].Action, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        await Create(root, "Keyboard shortcuts", new ScrollViewer { Content = grid, MaxHeight = 520 }).ShowAsync();
    }

    public static async Task ShowAboutAsync(XamlRoot root)
    {
        var version = typeof(Dialogs).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        void Paragraph(params Inline[] inlines)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var inline in inlines)
            {
                p.Inlines.Add(inline);
            }
            text.Blocks.Add(p);
        }
        Paragraph(new Run { Text = $"Lichess Puzzles {version}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new LineBreak(), new Run { Text = "Jupiter family · MIT licence" });
        Paragraph(new Run { Text = "Puzzles: the Lichess puzzle database, released under CC0 (database.lichess.org)." });
        Paragraph(new Run { Text = "Chess rules: cozy-chess, MIT licence." });
        Paragraph(new Run { Text = "Pieces: Merida by Armando Hernandez Marroquin, GPL version 2 or later." });
        Paragraph(new Run { Text = $"Data folder: {App.DataDirectory}" });
        await Create(root, "About Lichess Puzzles", text).ShowAsync();
    }
}
