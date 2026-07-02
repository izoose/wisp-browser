using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Wisp;

/// <summary>
/// Application entry point. Enforces a single instance (so being the default browser opens links
/// as tabs in the running window instead of new processes), handles a URL/file command-line
/// argument, registers Wisp as a browser, then shows the main window.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "WispBrowser_SingleInstance_v1";
    private const string PipeName = "WispBrowser_Pipe_v1";

    public AppSettings Settings { get; private set; } = null!;
    public BrowserEnvironment Browser { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    private Mutex? _mutex;
    private MainWindow? _main;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var startupUrl = ParseArg(e.Args);

        _mutex = new Mutex(true, MutexName, out bool isNew);
        // WISP_NO_SINGLE_INSTANCE lets a fully isolated instance run alongside a normal one
        // (used for benchmarking with a throwaway WISP_UDF).
        bool soloAllowed = Environment.GetEnvironmentVariable("WISP_NO_SINGLE_INSTANCE") == "1";
        if (!isNew && !soloAllowed)
        {
            // Another Wisp is running — hand it the URL and exit.
            TrySendToRunningInstance(startupUrl);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        AppPaths.EnsureDataDir();
        Settings = AppSettings.Load();
        try { DefaultBrowser.Register(); } catch { }

        // Finish any password import staged before the last restart. Must happen before the
        // WebView2 environment starts, because it locks the Login Data database.
        try
        {
            if (File.Exists(AppPaths.PendingLoginsFile))
                ChromiumImport.ApplyPendingPasswords(AppPaths.PendingLoginsFile, AppPaths.WebViewLoginData);
        }
        catch { }

        try
        {
            Browser = await BrowserEnvironment.CreateAsync(Settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Wisp could not start the browser engine.\n\n" +
                "The Microsoft Edge WebView2 Runtime may be missing.\n\n" + ex.Message,
                "Wisp", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _main = new MainWindow(Browser, Settings, startupUrl: startupUrl);
        MainWindow = _main;
        _main.Show();

        StartPipeServer();
    }

    /// <summary>First non-flag arg treated as a URL or a local file to open.</summary>
    private static string? ParseArg(string[] args)
    {
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith("-")) continue;
            if (File.Exists(a))
            {
                try { return new Uri(Path.GetFullPath(a)).AbsoluteUri; } catch { }
            }
            if (Uri.TryCreate(a, UriKind.Absolute, out var uri)) return uri.ToString();
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private const int ASFW_ANY = -1;

    private static void TrySendToRunningInstance(string? url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            // We (the just-launched instance) currently hold foreground rights; pass them to the
            // running instance so it can actually pull its window to the front, not just blink.
            AllowSetForegroundWindow(ASFW_ANY);
            using var w = new StreamWriter(client);
            w.WriteLine(url ?? "");
            w.Flush();
        }
        catch { }
    }

    private void StartPipeServer()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    var url = await r.ReadLineAsync();
                    Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(url)) _main?.OpenUrlExternally(url);
                        else _main?.BringToForeground();
                    });
                }
                catch { await Task.Delay(500); }
            }
        });
    }
}
