using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Homepage;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.Run(args);
        if (args.Contains("--probe-websites")) return WebsiteChecks.Probe(args);
        var baseDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDirectory.ToUpperInvariant())));
        using var instance = new Mutex(true, "Local\\Homepage-" + identity, out bool first);
        // Starting Homepage again brings the running one (possibly hidden in the tray) to the front.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Homepage-show-" + identity);
        if (!first) { show.Set(); return 0; }
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Homepage", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        // Environment.ProcessPath points to the EXE even when the runtime extracts elsewhere.
        try
        {
            var store = new LayoutStore(baseDirectory);
            var window = new MainWindow(store) { Resident = !args.Contains("--smoke-launch") };
            ThreadPool.RegisterWaitForSingleObject(show, (_, _) => window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray)), null, -1, false);
            if (args.Contains("--smoke-launch"))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.ShowActivated = false;
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() => window.Close(), DispatcherPriority.ApplicationIdle);
            }
            if (args.Contains("--tray"))
            {
                // Started by Windows at sign-in: sit in the tray until asked for.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown; window.Closed += (_, _) => app.Shutdown();
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle(); app.Run();
            }
            else app.Run(window);
            return args.Contains("--smoke-launch") && !File.Exists(store.SavePath) ? 1 : 0;
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Homepage could not start"); return 1; }
        finally { instance.ReleaseMutex(); }
    }
}
