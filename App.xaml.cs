using System.Windows;
using Lichess_Puzzles.Services;

namespace Lichess_Puzzles
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Check if database exists
            if (!PuzzleDatabaseService.DatabaseExists())
            {
                var setupWindow = new DatabaseSetupWindow();
                var result = setupWindow.ShowDialog();
                
                if (result != true || !setupWindow.DatabaseReady)
                {
                    // User cancelled or database not ready
                    Shutdown();
                    return;
                }
            }
            
            // Show main window and set it as the application's main window
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }
}

