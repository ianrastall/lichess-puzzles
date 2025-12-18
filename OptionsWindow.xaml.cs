using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lichess_Puzzles.Services;
using Microsoft.Win32;

namespace Lichess_Puzzles;

public partial class OptionsWindow : Window
{
    public AppSettings UpdatedSettings { get; }

    private readonly PuzzleExportService _exportService = new();
    private CancellationTokenSource? _exportCts;

    public OptionsWindow(AppSettings currentSettings)
    {
        InitializeComponent();
        UpdatedSettings = new AppSettings
        {
            BoardTheme = currentSettings.BoardTheme,
            SanDisplay = currentSettings.SanDisplay
        };

        BoardThemeCombo.ItemsSource = Enum.GetValues(typeof(BoardThemeOption));
        BoardThemeCombo.SelectedItem = UpdatedSettings.BoardTheme;

        SanSymbolsRadio.IsChecked = UpdatedSettings.SanDisplay == SanDisplayOption.Symbols;
        SanLettersRadio.IsChecked = UpdatedSettings.SanDisplay == SanDisplayOption.Letters;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        UpdateSettingsFromUi();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _exportCts?.Cancel();
        DialogResult = false;
        Close();
    }

    private async void BtnExportPgn_Click(object sender, RoutedEventArgs e)
    {
        await StartExportAsync(ExportFormat.Pgn, "PGN files (*.pgn)|*.pgn", "lichess_puzzles.pgn");
    }

    private async void BtnExportEpd_Click(object sender, RoutedEventArgs e)
    {
        await StartExportAsync(ExportFormat.Epd, "EPD files (*.epd)|*.epd", "lichess_puzzles.epd");
    }

    private void BtnCancelExport_Click(object sender, RoutedEventArgs e)
    {
        _exportCts?.Cancel();
    }

    private async Task StartExportAsync(ExportFormat format, string filter, string suggestedName)
    {
        if (_exportCts != null)
        {
            ExportStatusText.Text = "An export is already running.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = suggestedName,
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() != true)
            return;

        _exportCts = new CancellationTokenSource();
        SetExportUiState(true);
        ExportStatusText.Text = "Preparing export...";
        ExportProgressBar.Value = 0;

        var progress = new Progress<double>(value =>
        {
            ExportProgressBar.Visibility = Visibility.Visible;
            ExportProgressBar.Value = value;
        });

        var status = new Progress<string>(text => ExportStatusText.Text = text);

        try
        {
            await _exportService.ExportAsync(format, dialog.FileName, progress, status, _exportCts.Token);
            ExportStatusText.Text = "Export complete.";
        }
        catch (OperationCanceledException)
        {
            ExportStatusText.Text = "Export cancelled.";
            if (File.Exists(dialog.FileName))
            {
                try { File.Delete(dialog.FileName); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
            SetExportUiState(false);
        }
    }

    private void SetExportUiState(bool isExporting)
    {
        BtnExportPgn.IsEnabled = !isExporting;
        BtnExportEpd.IsEnabled = !isExporting;
        BtnSave.IsEnabled = !isExporting;
        BtnCancel.IsEnabled = !isExporting;
        BtnCancelExport.Visibility = isExporting ? Visibility.Visible : Visibility.Collapsed;
        ExportProgressBar.Visibility = isExporting ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSettingsFromUi()
    {
        if (BoardThemeCombo.SelectedItem is BoardThemeOption theme)
        {
            UpdatedSettings.BoardTheme = theme;
        }

        UpdatedSettings.SanDisplay = SanSymbolsRadio.IsChecked == true
            ? SanDisplayOption.Symbols
            : SanDisplayOption.Letters;
    }

    protected override void OnClosed(EventArgs e)
    {
        _exportCts?.Cancel();
        base.OnClosed(e);
    }
}
