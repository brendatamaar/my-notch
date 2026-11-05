using Microsoft.UI.Xaml;

namespace MyNotch;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        TraceStartup("App constructor");
#if DEBUG
        UnhandledException += (_, e) =>
        {
            if (Environment.GetEnvironmentVariable("MYNOTCH_NOTIFICATION_PROBE") is { Length: > 0 } path)
                File.WriteAllText(path + ".error", e.Exception.ToString());
        };
#endif
        InitializeComponent();
        TraceStartup("App XAML loaded");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!ClaimSingleInstance())
        {
            TraceStartup("Another instance is running; asked it to open");
            Exit();
            return;
        }
        TraceStartup("Creating MainWindow");
        var window = new MainWindow();
        _window = window;
        if (_openSignal is not null)
            _openWait = ThreadPool.RegisterWaitForSingleObject(_openSignal, (_, _) => window.RequestOpen(), null, Timeout.Infinite, executeOnlyOnce: false);
        TraceStartup("Activating MainWindow");
        _window.Activate();
        TraceStartup("MainWindow activated");
    }

    // One notch per session: a second launch signals the first to open, then exits.
    // Set MYNOTCH_MULTI_INSTANCE=1 to bypass (e.g. side-by-side debugging).
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _openSignal;
    private static RegisteredWaitHandle? _openWait;

    private static bool ClaimSingleInstance()
    {
        if (Environment.GetEnvironmentVariable("MYNOTCH_MULTI_INSTANCE") == "1") return true;
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\MyNotch.Instance", out bool created);
        if (!created)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\MyNotch.Open"); signal.Set(); }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
            return false;
        }
        _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MyNotch.Open");
        return true;
    }
    internal static void TraceStartup(string stage)
    {
        if (Environment.GetEnvironmentVariable("MYNOTCH_STARTUP_TRACE") is not { Length: > 0 } path) return;
        try { File.AppendAllText(path, stage + "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
