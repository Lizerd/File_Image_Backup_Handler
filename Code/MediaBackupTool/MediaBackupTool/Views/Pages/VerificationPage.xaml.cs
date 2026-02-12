using System.Windows;
using System.Windows.Controls;
using MediaBackupTool.ViewModels;

namespace MediaBackupTool.Views.Pages;

/// <summary>
/// Interaction logic for VerificationPage.xaml
/// </summary>
public partial class VerificationPage : UserControl
{
    public VerificationPage()
    {
        InitializeComponent();
    }

    private void OpenSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is MismatchItem item)
        {
            item.OpenSourceFolder();
        }
    }

    private void OpenDestFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is MismatchItem item)
        {
            item.OpenDestFolder();
        }
    }

    private void OpenSourceFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is MismatchItem item)
        {
            item.OpenSourceFile();
        }
    }

    private void OpenDestFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is MismatchItem item)
        {
            item.OpenDestFile();
        }
    }
}
