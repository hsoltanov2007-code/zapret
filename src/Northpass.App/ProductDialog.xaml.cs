using System.Windows;
using Northpass.ViewModels;

namespace Northpass;

// A real owned WPF modal: keyboard navigation stays inside it and the owner is
// disabled by ShowDialog. No nested custom dispatcher or native MessageBox.
public partial class ProductDialog : Window
{
    public ProductDialog(UiStrings strings, string message, bool confirmation = true)
    {
        InitializeComponent();
        Message.Text = message;
        CancelButton.Content = strings["Cancel"];
        AcceptButton.Content = strings[confirmation ? "Continue" : "Close"];
        CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => (confirmation ? CancelButton : AcceptButton).Focus();
    }
    public static bool Show(Window? owner, UiStrings strings, string message, bool confirmation = true)
    {
        var dialog = new ProductDialog(strings, message, confirmation);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }
    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
