using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using Northpass.Presentation;
using Northpass.ViewModels;

namespace Northpass;

public partial class SplashWindow : Window
{
    private bool _canClose;
    public event Action? CancelRequested;
    public SplashWindow(UiStrings strings)
    {
        InitializeComponent();
        StartupTagline.Text = strings["Tagline"];
        SetStatus(strings["Initializing"]);
    }
    public string Status => StartupStatus.Text;
    public void SetStatus(string status) => StartupStatus.Text = status;
    protected override void OnClosing(CancelEventArgs args)
    {
        if (!_canClose) { args.Cancel = true; CancelRequested?.Invoke(); }
        base.OnClosing(args);
    }
    public async Task FinishAsync()
    {
        if (IsVisible && Motion.Allowed(this))
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fade = Motion.Fade(Opacity, 0, 160);
            fade.FillBehavior = FillBehavior.HoldEnd;
            fade.Completed += (_, _) => finished.TrySetResult();
            BeginAnimation(OpacityProperty, fade);
            // A Windows animation-policy change can remove an active clock.
            // Never let a presentation clock hold application startup indefinitely.
            await Task.WhenAny(finished.Task, Task.Delay(220));
        }
        _canClose = true;
        Close();
    }
}
