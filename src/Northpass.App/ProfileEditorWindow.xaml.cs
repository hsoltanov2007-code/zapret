using System.Windows;
using Northpass.ViewModels;

namespace Northpass;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow(ProfileEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += () => DialogResult = true;
    }
}
