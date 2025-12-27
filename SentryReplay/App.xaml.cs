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
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Log.Error("Unhandled Exception");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unhandled Exception");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .CreateLogger();

        Log.Information("Application starting...");
    }
}
