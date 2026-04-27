using System.Windows;
using Serilog;

namespace SentryReplay;

/// <summary>
/// Interaction logic for App.xaml
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

        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .WriteTo.Console()
            .WriteTo.Debug()
            .CreateLogger();

        Log.Information(
            "Application starting. Version={Version}; Runtime={Runtime}; OS={OS}; ProcessArchitecture={ProcessArchitecture}",
            GetType().Assembly.GetName().Version,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application exiting. ExitCode={ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
