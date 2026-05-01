using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FlowLens;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Global\FlowLens.SingleInstance.v2";
    private const string ActivateEventName = @"Global\FlowLens.Activate.v2";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activateEvent))
            {
                activateEvent.Set();
                activateEvent.Dispose();
            }
            else if (!ExistingInstanceActivator.TryActivate())
            {
                System.Windows.MessageBox.Show("FlowLens is already running. Check the tray area or end the existing FlowLens process before starting it again.", "FlowLens", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivationListener();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void StartActivationListener()
    {
        var activateEvent = _activateEvent;
        if (activateEvent is null)
        {
            return;
        }

        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    activateEvent.WaitOne();
                    Dispatcher.Invoke(() =>
                    {
                        if (MainWindow is MainWindow mainWindow)
                        {
                            mainWindow.ShowFromTray();
                        }
                    });
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch
                {
                    return;
                }
            }
        });
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        System.Windows.MessageBox.Show("FlowLens crashed. A crash log was written to %APPDATA%\\FlowLens\\crash.log.", "FlowLens", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = false;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowLens");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n{exception}\r\n\r\n");
        }
        catch
        {
        }
    }
}

internal static class ExistingInstanceActivator
{
    private const int SwRestore = 9;
    private const int SwShow = 5;

    public static bool TryActivate()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                ShowWindow(handle, SwRestore);
                ShowWindow(handle, SwShow);
                SetForegroundWindow(handle);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
