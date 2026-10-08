using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Northpass.ViewModels;

namespace Northpass;

public partial class LicensesWindow : Window
{
    private sealed record Document(string Name, string Path);
    private readonly UiStrings _strings;
    public LicensesWindow(UiStrings strings)
    {
        InitializeComponent(); _strings = strings;
        Heading.Text = strings["LicensesTitle"]; CloseButton.Content = strings["Close"]; Sources.Content = strings["ViewSources"];
        string root = AppContext.BaseDirectory;
        var files = new List<Document>();
        string notices = Path.Combine(root, "THIRD_PARTY_NOTICES.md");
        if (File.Exists(notices)) files.Add(new("Northpass · Third-party notices", notices));
        string licenses = Path.Combine(root, "docs", "licenses");
        if (Directory.Exists(licenses)) files.AddRange(Directory.EnumerateFiles(licenses, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => new Document(Path.GetRelativePath(licenses, path), path)));
        Documents.ItemsSource = files; Documents.SelectedIndex = 0;
        Sources.IsEnabled = Directory.Exists(Path.Combine(root, "docs", "third-party-source"));
    }
    private void Document_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Documents.SelectedItem is not Document document) return;
        try { LicenseText.Text = File.ReadAllText(document.Path); LicenseText.ScrollToHome(); }
        catch (IOException) { LicenseText.Text = _strings["ActionFailed"]; }
    }
    private void Sources_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "docs", "third-party-source")) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        { ProductDialog.Show(this, _strings, _strings["ActionFailed"], false); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
