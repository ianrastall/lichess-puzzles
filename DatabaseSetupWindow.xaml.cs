using System.IO;
using System.Net.Http;
using System.Windows;
using Lichess_Puzzles.Services;
using Microsoft.Data.Sqlite;

namespace Lichess_Puzzles;

public partial class DatabaseSetupWindow : Window
{
    private readonly PuzzleDatabaseService _databaseService;
    private bool _isDownloading;
    private bool _downloadComplete;
    
    public bool DatabaseReady { get; private set; }
    
    public DatabaseSetupWindow(bool isRefresh = false)
    {
        InitializeComponent();
        _databaseService = new PuzzleDatabaseService();
        _databaseService.StatusChanged += OnStatusChanged;
        _databaseService.ProgressChanged += OnProgressChanged;
        _databaseService.LogMessage += OnLogMessage;
        
        UpdateDatabaseInfo();
        
        if (isRefresh)
        {
            DescriptionText.Text = "You can refresh the puzzle database to get the latest puzzles from Lichess. This will download approximately 100 MB and rebuild the local database.";
            BtnDownload.Content = "Refresh Database";
        }
        
        if (PuzzleDatabaseService.DatabaseExists())
        {
            BtnContinue.Visibility = Visibility.Visible;
            BtnContinue.Content = isRefresh ? "Cancel" : "Use Existing";
        }
        
        Log("Ready. Click 'Download Database' to begin.");
        Log($"Database location: {PuzzleDatabaseService.GetDatabasePath()}");
    }
    
    private void UpdateDatabaseInfo()
    {
        if (PuzzleDatabaseService.DatabaseExists())
        {
            var date = PuzzleDatabaseService.GetDatabaseDate();
            DatabaseInfoText.Text = date.HasValue 
                ? $"Current database: Last updated {date.Value:MMMM d, yyyy}" 
                : "Existing database found";
        }
        else
        {
            DatabaseInfoText.Text = "No database found";
        }
    }
    
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var logLine = $"[{timestamp}] {message}\n";
        
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText(logLine);
            LogScrollViewer.ScrollToEnd();
        });
    }
    
    private void OnStatusChanged(string status)
    {
        Dispatcher.Invoke(() => StatusText.Text = status);
    }
    
    private void OnProgressChanged(double progress)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = progress;
            ProgressText.Text = $"{progress:F1}%";
        });
    }
    
    private void OnLogMessage(string message)
    {
        Log(message);
    }
    
    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        
        _isDownloading = true;
        BtnDownload.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        BtnContinue.Visibility = Visibility.Collapsed;
        ProgressBar.Value = 0;
        ProgressText.Text = "0%";
        
        Log("Starting download...");
        
        try
        {
            var success = await _databaseService.DownloadAndCreateDatabaseAsync();
            
            // Mark download as complete BEFORE any UI updates that might trigger close
            _isDownloading = false;
            BtnCancel.IsEnabled = false;
            
            if (success)
            {
                _downloadComplete = true;
                DatabaseReady = true;
                BtnContinue.Content = "Continue";
                BtnContinue.Visibility = Visibility.Visible;
                BtnDownload.IsEnabled = false;
                UpdateDatabaseInfo();
                Log("Database setup complete!");
                
                // Auto-continue after successful download
                await Task.Delay(1500);
                if (_downloadComplete)
                {
                    DialogResult = true;
                    Close();
                }
            }
            else
            {
                Log("Download was cancelled or failed.");
                BtnDownload.IsEnabled = true;
                if (PuzzleDatabaseService.DatabaseExists())
                {
                    BtnContinue.Visibility = Visibility.Visible;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _isDownloading = false;
            BtnCancel.IsEnabled = false;
            
            Log($"Network error: {ex.Message}");
            MessageBox.Show("Failed to download database. Please check your internet connection and try again.", 
                "Network Error", MessageBoxButton.OK, MessageBoxImage.Error);
            BtnDownload.IsEnabled = true;
            if (PuzzleDatabaseService.DatabaseExists())
            {
                BtnContinue.Visibility = Visibility.Visible;
            }
        }
        catch (IOException ex)
        {
            _isDownloading = false;
            BtnCancel.IsEnabled = false;
            
            Log($"File system error: {ex.Message}");
            MessageBox.Show($"Failed to write database file. Please ensure you have write permissions.\n\nDetails: {ex.Message}", 
                "File Error", MessageBoxButton.OK, MessageBoxImage.Error);
            BtnDownload.IsEnabled = true;
            if (PuzzleDatabaseService.DatabaseExists())
            {
                BtnContinue.Visibility = Visibility.Visible;
            }
        }
        catch (SqliteException ex)
        {
            _isDownloading = false;
            BtnCancel.IsEnabled = false;
            
            Log($"Database error: {ex.Message}");
            MessageBox.Show($"Failed to create database. The downloaded file may be corrupt.\n\nDetails: {ex.Message}", 
                "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            BtnDownload.IsEnabled = true;
            if (PuzzleDatabaseService.DatabaseExists())
            {
                BtnContinue.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            BtnCancel.IsEnabled = false;
            
            Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Log($"  Inner: {ex.InnerException.Message}");
            }
            Log($"  Stack: {ex.StackTrace}");
            
            MessageBox.Show($"An unexpected error occurred: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            BtnDownload.IsEnabled = true;
            if (PuzzleDatabaseService.DatabaseExists())
            {
                BtnContinue.Visibility = Visibility.Visible;
            }
        }
    }
    
    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            Log("Cancellation requested...");
            _databaseService.Cancel();
            StatusText.Text = "Cancelling...";
        }
    }
    
    private void BtnContinue_Click(object sender, RoutedEventArgs e)
    {
        if (PuzzleDatabaseService.DatabaseExists() || _downloadComplete)
        {
            DatabaseReady = true;
            DialogResult = true;
        }
        Close();
    }
    
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isDownloading)
        {
            var result = MessageBox.Show("Download is in progress. Cancel and exit?", 
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                _databaseService.Cancel();
            }
            else
            {
                e.Cancel = true;
                return;
            }
        }

        _databaseService.StatusChanged -= OnStatusChanged;
        _databaseService.ProgressChanged -= OnProgressChanged;
        _databaseService.LogMessage -= OnLogMessage;
        _databaseService.Dispose();
        base.OnClosing(e);
    }
}
