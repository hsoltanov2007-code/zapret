using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Northpass.ViewModels;
using Forms = System.Windows.Forms;

namespace Northpass;

// View-only responsibilities: window lifetime, log scrolling and Windows tray.
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _timer;
    private readonly Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon _icon;
    private bool _closing, _allowClose;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = _model = model;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Northpass;component/Assets/Northpass.ico")).Stream;
        _icon = new System.Drawing.Icon(stream);
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "Northpass", Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var menu = new Forms.ContextMenuStrip();
        var show = menu.Items.Add("");
        var toggle = menu.Items.Add("");
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = menu.Items.Add("");
        show.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        toggle.Click += (_, _) => Dispatcher.Invoke(() => { if (_model.ConnectCommand.CanExecute(null)) _model.ConnectCommand.Execute(null); });
        exit.Click += (_, _) => Dispatcher.Invoke(Close);
        menu.Opening += (_, _) =>
        {
            show.Text = _model.Strings["Show"];
            toggle.Text = _model.ConnectText;
            toggle.Enabled = _model.ConnectCommand.CanExecute(null);
            exit.Text = _model.Strings["Exit"];
        };
        _tray.ContextMenuStrip = menu;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await _model.RefreshAsync();
        _timer.Start();
    }

    public void ShowFromTray()
    {
        Show(); WindowState = WindowState.Normal; Activate();
    }
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _model.MinimizeToTray) Hide();
    }
    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox?.ScrollToEnd();
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_allowClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        base.OnClosing(e);
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        try
        {
            await _model.DisposeAsync();
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose(); _icon.Dispose();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _model.Fail(ex); ShowFromTray();
            MessageBox.Show(this, "The engine could not be stopped. The window will remain open so you can retry.\n" + ex.Message, "Northpass", MessageBoxButton.OK, MessageBoxImage.Error);
            _closing = false;
            _timer.Start();
        }
    }
}
