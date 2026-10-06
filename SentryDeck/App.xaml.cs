using System.Windows;
using Serilog;
using Serilog.Core;

namespace SentryDeck;

/// <summary>
/// Application startup and process-level logging hooks.
/// </summary>
public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled application exception. IsTerminating={IsTerminating}", e.IsTerminating);
            }
            else
            {
                Log.Fatal("Unhandled application exception. IsTerminating={IsTerminating}; ExceptionObject={ExceptionObject}",
                    e.IsTerminating,
                    e.ExceptionObject);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log.Error(e.Exception, "Unobserved task exception");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A packaged WinExe has no console and no debugger attached, so without a file sink every log line, including the Log.Fatal from the unhandled-exception hooks above, is discarded in the field, making a shipped crash undiagnosable.
        // Rolling daily with a small retention keeps the folder bounded.
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SentryDeck",
            "logs",
            "log-.txt");

        Log.Logger = CreateLogger(logPath);

        // Last-resort safety net for exceptions raised on the UI thread by a command or event handler (e.g. Clipboard.SetText throwing COMException when a clipboard manager holds the clipboard, or Process.Start failing on a missing shell association).
        // Without this, WPF tears the whole process down; a media reviewer should log the fault and stay open instead.
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled dispatcher exception; keeping the application alive");
            e.Handled = true;
        };

        Log.Information(
            "Application starting. Version={Version}; Runtime={Runtime}; OS={OS}; ProcessArchitecture={ProcessArchitecture}",
            GetType().Assembly.GetName().Version,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

        // Opened here rather than through StartupUri so that a window that fails to build ends the app.
        // Through StartupUri, the failure reached the keep-alive handler above, which left a process with no window that only Task Manager could end.
        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to open the main window");
            MessageBox.Show(
                $"Sentry Deck couldn't start: {ex.GetBaseException().Message}\n\nThe log in {System.IO.Path.GetDirectoryName(logPath)} has the details.",
                "Sentry Deck",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// The app's logger, writing a daily file at <paramref name="logPath"/> (Serilog inserts the date before the extension).
    /// </summary>
    internal static Logger CreateLogger(string logPath) =>
        new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Console()
            .WriteTo.Debug()
            // Shared so that a second running instance appends to today's file.
            // Unshared, it found the file locked and started a numbered copy, and the retention counts files, not days, so each copy deleted an earlier day's log.
            // Every line names its process because concurrent instances interleave in that one file, and a bug report has to show which window did what.
            .WriteTo.File(
                logPath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ProcessId}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application exiting. ExitCode={ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
