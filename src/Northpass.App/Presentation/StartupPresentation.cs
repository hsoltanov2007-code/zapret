using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Northpass.ViewModels;

namespace Northpass.Presentation;

public static class StartupPresentation
{
    // Show real preparation progress, without a minimum splash dwell or awaiting
    // background web checks. MainWindow remains the lifetime/process owner.
    public static async Task<bool> ShowAsync(MainWindow window, MainViewModel model, bool startInTray = false, SplashWindow? splash = null)
    {
        var app = Application.Current;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.MainWindow = window;
        if (startInTray)
        {
            await model.InitializeAsync();
            window.WindowState = WindowState.Minimized;
            window.Show(); window.Hide();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return true;
        }
        splash ??= new SplashWindow(model.Strings);
        bool cancelled = false;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Closed(object? sender, EventArgs args) => closed.TrySetResult();
        void Cancel() { if (!cancelled) { cancelled = true; window.Close(); } }
        void Progress(object? sender, PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(MainViewModel.EngineSetupText)) splash.SetStatus(model.EngineSetupText); }
        splash.CancelRequested += Cancel;
        window.Closed += Closed;
        model.PropertyChanged += Progress;
        try
        {
            splash.Show();
            await Dispatcher.Yield(DispatcherPriority.Render);
            await model.InitializeAsync();
            if (cancelled) { await closed.Task; return false; }
            splash.SetStatus(model.Strings["LoadingInterface"]);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.Render);
            splash.SetStatus(model.EngineSetupText);
            await splash.FinishAsync();
            if (cancelled) { await closed.Task; return false; }
            window.Activate();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return true;
        }
        finally
        {
            model.PropertyChanged -= Progress;
            splash.CancelRequested -= Cancel;
            window.Closed -= Closed;
            if (splash.IsVisible) await splash.FinishAsync();
        }
    }
}
