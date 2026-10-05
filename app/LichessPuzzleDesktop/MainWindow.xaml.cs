using LichessPuzzleDesktop.Services;
using LichessPuzzleDesktop.ViewModels;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace LichessPuzzleDesktop;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(new ShellService(this), AppSettings.Load());
        InitializeComponent();

        Title = "Lichess Puzzle Desktop";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        SizeToDisplay();

        Root.Loaded += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
            // Keep the initial focus off the filter boxes so the arrow keys step through moves.
            NextPuzzleButton.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) => ViewModel.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    // AppWindow works in physical pixels; size the window in DIPs and keep it inside the work area.
    private void SizeToDisplay()
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(1440 * scale), work.Width - 40);
        int height = Math.Min((int)(920 * scale), work.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(1040 * scale), work.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(700 * scale), work.Height);
        }
    }

    private void OnStepBack(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Run(ViewModel.StepBackCommand, args);
    private void OnStepForward(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Run(ViewModel.StepForwardCommand, args);
    private void OnStepFirst(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Run(ViewModel.StepFirstCommand, args);
    private void OnStepLast(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Run(ViewModel.StepLastCommand, args);
    private void OnNextPuzzle(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Run(ViewModel.NextPuzzleCommand, args);
    private void OnOpenDatabase(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Run(ViewModel.OpenDatabaseCommand, args);
    private void OnSavePgn(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Run(ViewModel.SavePgnCommand, args);

    private static void Run(System.Windows.Input.ICommand command, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (command.CanExecute(null)) command.Execute(null);
    }
}
