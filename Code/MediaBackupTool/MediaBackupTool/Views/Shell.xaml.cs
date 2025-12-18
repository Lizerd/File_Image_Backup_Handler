using System.Windows;
using MediaBackupTool.ViewModels;

namespace MediaBackupTool.Views;

public partial class Shell : Window
{
    public Shell(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
