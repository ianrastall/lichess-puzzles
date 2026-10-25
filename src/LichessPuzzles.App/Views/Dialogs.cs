using Jupiter.LichessPuzzles.App.Services;
using Jupiter.LichessPuzzles.App.ViewModels;
using Jupiter.LichessPuzzles.Messages;
using Jupiter.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Jupiter.LichessPuzzles.App.Views;

/// <summary>The app's small dialogs, built in code.</summary>
public static class Dialogs
{
    private static ContentDialog Create(XamlRoot root, string title, object content, string close = "Close")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
        };
        App.Current.Theme.Attach(dialog);
        return dialog;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string action)
    {
        // Standard section 10: the close button is "Cancel" and is the default for a destructive action.
        var dialog = Create(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, "Cancel");
        dialog.PrimaryButtonText = action;
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Asks for a name. <paramref name="check"/> returns a note to show for the current text; with
    /// <paramref name="blockOnWarning"/> a note also disables the primary button. Returns null when cancelled.
    /// </summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string label, string initial, string action,
        Func<string, string?>? check = null, bool blockOnWarning = false, string? problem = null)
    {
        var box = new TextBox { Header = label, Text = initial, MaxLength = 40 };
        var note = new TextBlock { Visibility = Visibility.Collapsed, Style = JupiterTheme.Style("JupiterCaptionTextStyle") };
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(box);
        panel.Children.Add(note);
        var dialog = Create(root, title, panel, "Cancel");
        dialog.PrimaryButtonText = action;
        dialog.DefaultButton = ContentDialogButton.Primary;
        void Update()
        {
            var text = box.Text.Trim();
            var warning = text.Length == 0 ? null : check?.Invoke(text);
            note.Text = warning ?? problem ?? "";
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = text.Length > 0 && !(blockOnWarning && warning is not null);
            problem = null;
        }
        box.TextChanged += (_, _) => Update();
        Update();
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public sealed record ExportChoice(ExportFormat Format, bool PlayerStart, string Path);

    /// <summary>Format, PGN start and destination for an export; null when cancelled.</summary>
    public static async Task<ExportChoice?> ShowExportAsync(XamlRoot root, Window owner, string title, string countText, string suggestedName, JsonStore<AppSettings> settings)
    {
        var formats = new RadioButtons { Header = "Format", MaxColumns = 3 };
        formats.Items.Add("PGN");
        formats.Items.Add("CSV");
        formats.Items.Add("EPD");
        formats.SelectedIndex = settings.Value.ExportFormat switch { "csv" => 1, "epd" => 2, _ => 0 };
        var playerStart = new CheckBox { Content = "Start each game at the solver's position", IsChecked = settings.Value.ExportPlayerStart };
        var explain = new TextBlock { Style = JupiterTheme.Style("JupiterCaptionTextStyle") };
        var path = new TextBox { Header = "File", IsReadOnly = false, PlaceholderText = "Choose where to save" };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(path);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);
        var panel = new StackPanel { Spacing = 16, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = countText, Style = JupiterTheme.Style("JupiterBodyTextStyle") });
        panel.Children.Add(formats);
        panel.Children.Add(playerStart);
        panel.Children.Add(explain);
        panel.Children.Add(pathRow);
        var dialog = Create(root, title, panel, "Cancel");
        dialog.PrimaryButtonText = "Export";
        dialog.DefaultButton = ContentDialogButton.Primary;

        string Extension() => formats.SelectedIndex switch { 1 => ".csv", 2 => ".epd", _ => ".pgn" };
        void Update()
        {
            playerStart.IsEnabled = formats.SelectedIndex == 0;
            explain.Text = formats.SelectedIndex switch
            {
                1 => "CSV uses the Lichess columns, one puzzle per line.",
                2 => "EPD gives the position the solver faces, with the first solution move as bm.",
                _ => playerStart.IsChecked == true
                    ? "Each game starts from the position the solver faces."
                    : "Each game starts from the database position: the opponent's move comes first.",
            };
            if (path.Text.Length > 0 && !path.Text.EndsWith(Extension(), StringComparison.OrdinalIgnoreCase))
            {
                path.Text = System.IO.Path.ChangeExtension(path.Text, Extension());
            }
            dialog.IsPrimaryButtonEnabled = path.Text.Trim().Length > 0;
        }
        formats.SelectionChanged += (_, _) => Update();
        playerStart.Click += (_, _) => Update();
        path.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = path.Text.Trim().Length > 0;
        browse.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggestedName };
            var (name, extension) = formats.SelectedIndex switch
            {
                1 => ("CSV", ".csv"),
                2 => ("EPD", ".epd"),
                _ => ("PGN", ".pgn"),
            };
            picker.FileTypeChoices.Add(name, [extension]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            if (await picker.PickSaveFileAsync() is { } file)
            {
                path.Text = file.Path;
            }
        };
        Update();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }
        var format = formats.SelectedIndex switch { 1 => ExportFormat.Csv, 2 => ExportFormat.Epd, _ => ExportFormat.Pgn };
        settings.Value.ExportFormat = format switch { ExportFormat.Csv => "csv", ExportFormat.Epd => "epd", _ => "pgn" };
        settings.Value.ExportPlayerStart = playerStart.IsChecked == true;
        settings.Save();
        return new ExportChoice(format, settings.Value.ExportPlayerStart, path.Text.Trim());
    }

    public static async Task ShowGoToPuzzleAsync(XamlRoot root, PuzzleViewModel puzzle)
    {
        var box = new TextBox { Header = "Puzzle id or lichess.org link", PlaceholderText = "00sHx" };
        var error = new TextBlock { Visibility = Visibility.Collapsed, Style = JupiterTheme.Style("JupiterCaptionTextStyle"), Foreground = JupiterTheme.Brush("JupiterErrorBrush", App.Current.Theme.Resolved) };
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
            grid.Children.Add(new TextBlock { Text = label, Style = JupiterTheme.Style("JupiterCaptionTextStyle"), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = value, Style = JupiterTheme.Style("JupiterBodyTextStyle"), IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            panel.Children.Add(grid);
        }
        void Heading(string text) => panel.Children.Add(new TextBlock { Text = text, Style = JupiterTheme.Style("JupiterPaneHeadingTextStyle"), Margin = new Thickness(0, 8, 0, 0) });

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
            ("Ctrl+1 / Ctrl+2", "Puzzle / History"),
            ("Ctrl+E", "Export matching puzzles"),
            ("Ctrl+Shift+E", "Export the puzzle as PGN"),
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
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = rows[i].Keys, Style = JupiterTheme.Style("JupiterBodyStrongTextStyle"), TextWrapping = TextWrapping.NoWrap };
            var action = new TextBlock { Text = rows[i].Action, Style = JupiterTheme.Style("JupiterBodyTextStyle") };
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
        // Standard section 13: About shows the build identity, here the version with its source commit.
        var build = typeof(Dialogs).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? version;
        var plus = build.IndexOf('+');
        if (plus >= 0 && build.Length > plus + 13)
        {
            build = build[..(plus + 13)];
        }
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
        Paragraph(new Run { Text = $"Lichess Puzzles {version}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new LineBreak(),
            new Run { Text = "Part of Jupiter · MIT licence" }, new LineBreak(), new Run { Text = $"Build {build}" });
        Paragraph(new Run { Text = "Puzzles: the Lichess puzzle database, released under CC0 (database.lichess.org)." });
        Paragraph(new Run { Text = "Chess rules: cozy-chess, MIT licence. Evaluation: a small built-in engine using Tomasz Michniewski's simplified evaluation function." });
        Paragraph(new Run { Text = "Pieces: Merida by Armando Hernandez Marroquin, GPL version 2 or later." });
        Paragraph(new Run { Text = $"Data folder: {App.DataDirectory}" });
        await Create(root, "About Lichess Puzzles", text).ShowAsync();
    }
}
