using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace Jupiter.LichessPuzzles.App;

public static partial class Program
{
    private const string InstanceMutexName = @"Local\Jupiter.LichessPuzzles";
    internal const string ShowEventName = @"Local\Jupiter.LichessPuzzles.Show";

    [STAThread]
    public static int Main(string[] args)
    {
        // Create before the instance mutex: a second launch can signal even during WinUI startup.
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        // One instance per user session. A second launch asks the first to show its window.
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            showSignal.Set();
            return 0;
        }

        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("Jupiter.LichessPuzzles"));
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        GC.KeepAlive(instance);
        return 0;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
