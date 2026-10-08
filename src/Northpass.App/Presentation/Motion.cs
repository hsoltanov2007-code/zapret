using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;

namespace Northpass.Presentation;

// View-only motion: no delays in installation, connection or diagnostics.
public static class Motion
{
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.RegisterAttached(
        "ReduceMotion", typeof(bool), typeof(Motion), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, ReducedChanged));
    public static bool GetReduceMotion(DependencyObject element) => (bool)element.GetValue(ReduceMotionProperty);
    public static void SetReduceMotion(DependencyObject element, bool value) => element.SetValue(ReduceMotionProperty, value);
    public static bool Allowed(DependencyObject element) => !GetReduceMotion(element) && SystemParameters.ClientAreaAnimation && SystemParameters.UIEffects;
    private static void ReducedChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is FrameworkElement view)
        {
            if ((bool)args.NewValue) view.BeginAnimation(UIElement.OpacityProperty, null);
            if (GetSpin(view)) ApplySpin(view);
        }
    }

    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached("Enter", typeof(bool), typeof(Motion), new PropertyMetadata(false, EnterChanged));
    public static bool GetEnter(DependencyObject element) => (bool)element.GetValue(EnterProperty);
    public static void SetEnter(DependencyObject element, bool value) => element.SetValue(EnterProperty, value);
    private static void EnterChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement view) return;
        if ((bool)args.NewValue) view.Loaded += Appear;
        else view.Loaded -= Appear;
    }
    private static void Appear(object sender, RoutedEventArgs args) => Reveal((FrameworkElement)sender);
    public static void Reveal(FrameworkElement element)
    {
        if (!Allowed(element)) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; return; }
        element.BeginAnimation(UIElement.OpacityProperty, Fade(0.35, 1, 240));
    }
    public static DoubleAnimation Fade(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.RegisterAttached("Revision", typeof(object), typeof(Motion), new PropertyMetadata(null, RevisionChanged));
    public static object GetRevision(DependencyObject element) => element.GetValue(RevisionProperty);
    public static void SetRevision(DependencyObject element, object value) => element.SetValue(RevisionProperty, value);
    private static void RevisionChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    { if (element is FrameworkElement { IsLoaded: true, IsVisible: true } view) Reveal(view); }

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached("Interactive", typeof(bool), typeof(Motion), new PropertyMetadata(false, InteractiveChanged));
    public static bool GetInteractive(DependencyObject element) => (bool)element.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject element, bool value) => element.SetValue(InteractiveProperty, value);
    private static void InteractiveChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement view) return;
        if ((bool)args.NewValue)
        { view.MouseEnter += Hover; view.MouseLeave += Leave; view.PreviewMouseLeftButtonDown += Press; view.PreviewMouseLeftButtonUp += Release; view.Unloaded += Reset; view.IsEnabledChanged += EnabledChanged; }
        else
        { view.MouseEnter -= Hover; view.MouseLeave -= Leave; view.PreviewMouseLeftButtonDown -= Press; view.PreviewMouseLeftButtonUp -= Release; view.Unloaded -= Reset; view.IsEnabledChanged -= EnabledChanged; }
    }
    private static void Hover(object sender, MouseEventArgs args) => Ease((FrameworkElement)sender, 0.86);
    private static void Leave(object sender, MouseEventArgs args) => Ease((FrameworkElement)sender, 1);
    private static void Press(object sender, MouseButtonEventArgs args) => Ease((FrameworkElement)sender, 0.68);
    private static void Release(object sender, MouseButtonEventArgs args) => Ease((FrameworkElement)sender, ((FrameworkElement)sender).IsMouseOver ? 0.86 : 1);
    private static void Reset(object sender, RoutedEventArgs args) => ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty, null);
    private static void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty, null);
    private static void Ease(FrameworkElement element, double opacity)
    {
        if (!element.IsEnabled) return;
        // Keep the disabled-opacity style and the control's base value intact.
        var animation = Fade(element.Opacity, opacity, 110);
        animation.FillBehavior = FillBehavior.HoldEnd;
        element.BeginAnimation(UIElement.OpacityProperty, Allowed(element) ? animation : null);
    }

    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached("Spin", typeof(bool), typeof(Motion), new PropertyMetadata(false, SpinChanged));
    public static bool GetSpin(DependencyObject element) => (bool)element.GetValue(SpinProperty);
    public static void SetSpin(DependencyObject element, bool value) => element.SetValue(SpinProperty, value);
    private static void SpinChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement view) return;
        view.Loaded -= UpdateSpin; view.IsVisibleChanged -= VisibilityChanged; view.Unloaded -= StopSpin;
        UnsubscribePolicy(view);
        if ((bool)args.NewValue)
        { view.Loaded += UpdateSpin; view.IsVisibleChanged += VisibilityChanged; view.Unloaded += StopSpin; }
        if ((bool)args.NewValue && view.IsLoaded) SubscribePolicy(view);
        ApplySpin(view);
    }
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => ApplySpin((FrameworkElement)sender);
    private static void UpdateSpin(object sender, RoutedEventArgs args)
    { var view = (FrameworkElement)sender; SubscribePolicy(view); ApplySpin(view); }
    private static void StopSpin(object sender, RoutedEventArgs args)
    {
        var view = (FrameworkElement)sender; UnsubscribePolicy(view);
        if (view.RenderTransform is RotateTransform rotation) rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }
    private static readonly DependencyProperty PolicyHandlerProperty = DependencyProperty.RegisterAttached("PolicyHandler", typeof(PropertyChangedEventHandler), typeof(Motion));
    private static void SubscribePolicy(FrameworkElement view)
    {
        if (view.GetValue(PolicyHandlerProperty) is not null) return;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.UIEffects))
                view.Dispatcher.BeginInvoke(new Action(() => { if (view.IsLoaded) ApplySpin(view); }));
        };
        view.SetValue(PolicyHandlerProperty, handler);
        SystemParameters.StaticPropertyChanged += handler;
    }
    private static void UnsubscribePolicy(FrameworkElement view)
    {
        if (view.GetValue(PolicyHandlerProperty) is PropertyChangedEventHandler handler)
        { SystemParameters.StaticPropertyChanged -= handler; view.ClearValue(PolicyHandlerProperty); }
    }
    private static void ApplySpin(FrameworkElement element)
    {
        if (element.RenderTransform is not RotateTransform rotation)
        { rotation = new RotateTransform(); element.RenderTransform = rotation; element.RenderTransformOrigin = new Point(0.5, 0.5); }
        rotation.BeginAnimation(RotateTransform.AngleProperty, GetSpin(element) && element.IsLoaded && element.IsVisible && Allowed(element)
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }
}
