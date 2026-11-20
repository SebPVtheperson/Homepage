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
        if (!first) { MessageBox.Show("Homepage is already open for this folder.", "Homepage"); return 0; }
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
            var window = new MainWindow(store);
            if (args.Contains("--smoke-launch"))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.ShowActivated = false;
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() => window.Close(), DispatcherPriority.ApplicationIdle);
            }
            app.Run(window);
            return args.Contains("--smoke-launch") && !File.Exists(store.SavePath) ? 1 : 0;
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Homepage could not start"); return 1; }
        finally { instance.ReleaseMutex(); }
    }
}
