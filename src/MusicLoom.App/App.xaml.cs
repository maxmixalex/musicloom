using Microsoft.UI.Xaml;

namespace MusicLoom.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        StartupTrace.Mark("App.ctor.enter");
        StartupTrace.Mark("App.InitializeComponent.before");
        InitializeComponent();
        StartupTrace.Mark("App.InitializeComponent.after");

        StartupTrace.Mark("AppDiagnostics.Initialize.before");
        AppDiagnostics.Initialize();
        StartupTrace.Mark("AppDiagnostics.Initialize.after");
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException +=
            OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException +=
            OnDomainUnhandledException;
    }

    protected override void OnLaunched(
        LaunchActivatedEventArgs args)
    {
        StartupTrace.Mark("App.OnLaunched.enter");
        StartupTrace.Mark("MainWindow.ctor.before");
        _window = new MainWindow();
        StartupTrace.Mark("MainWindow.ctor.after");

        string? smokeMarker =
            Environment.GetEnvironmentVariable(
                "MUSICLOOM_SMOKE_MARKER");
        if (!string.IsNullOrWhiteSpace(smokeMarker))
        {
            _window.Activated += (_, _) =>
            {
                try
                {
                    string? directory =
                        Path.GetDirectoryName(
                            smokeMarker);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(
                        smokeMarker,
                        "MusicLoom main window activated.");
                }
                catch (Exception exception)
                {
                    AppDiagnostics.LogException(
                        "Could not write launch smoke marker.",
                        exception);
                }
            };
        }

        // CI can exercise the exact NetEase-derived matching component,
        // including real WinUI artwork rendering, asynchronous cover
        // hydration and parity across all three providers.
        string? matchingMarker =
            Environment.GetEnvironmentVariable("MUSICLOOM_MATCH_SMOKE_MARKER");
        if (!string.IsNullOrWhiteSpace(matchingMarker))
        {
            bool started = false;
            _window.Activated += async (_, _) =>
            {
                if (started || _window is not MainWindow main) return;
                started = true;
                await main.RunMatchUiSmokeAsync(matchingMarker);
            };
        }

        StartupTrace.Mark("MainWindow.Activate.before");
        _window.Activate();
        StartupTrace.Mark("MainWindow.Activate.after");
    }

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        AppDiagnostics.LogException(
            "Unhandled WinUI exception.",
            args.Exception);
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        AppDiagnostics.LogException(
            "Unobserved task exception.",
            args.Exception);
        args.SetObserved();
    }

    private static void OnDomainUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            AppDiagnostics.LogException(
                "Unhandled process exception.",
                exception);
        }
        else
        {
            AppDiagnostics.Log(
                "Unhandled process exception: "
                + args.ExceptionObject);
        }
    }
}
