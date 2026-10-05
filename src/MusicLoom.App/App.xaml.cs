using Microsoft.UI.Xaml;

namespace MusicLoom.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        AppDiagnostics.Initialize();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException +=
            OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException +=
            OnDomainUnhandledException;
    }

    protected override void OnLaunched(
        LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
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
