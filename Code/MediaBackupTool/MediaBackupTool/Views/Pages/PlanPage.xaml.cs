using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaBackupTool.ViewModels;

namespace MediaBackupTool.Views.Pages;

public partial class PlanPage : UserControl
{
    public PlanPage()
    {
        InitializeComponent();
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is PlanViewModel viewModel && e.NewValue is LazyFolderNodeViewModel folder)
        {
            viewModel.SelectedFolder = folder;
        }
    }

    /// <summary>
    /// Handles the checkbox click to persist changes and recalculate size.
    /// </summary>
    private async void FolderCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox &&
            checkBox.DataContext is LazyFolderNodeViewModel folder &&
            DataContext is PlanViewModel viewModel)
        {
            // The binding already updated CopyEnabled, now persist and recalculate
            await viewModel.ToggleFolderEnabledCommand.ExecuteAsync(folder);
        }
    }

    #region File List Event Handlers

    /// <summary>
    /// Opens the file in default viewer when double-clicking on the DataGrid row.
    /// </summary>
    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid dataGrid && dataGrid.SelectedItem is UniqueFileListItem item)
        {
            item.OpenInDefaultViewer();
        }
    }

    /// <summary>
    /// Opens the selected file in default viewer.
    /// </summary>
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel viewModel && viewModel.SelectedFile != null)
        {
            viewModel.SelectedFile.OpenInDefaultViewer();
        }
    }

    /// <summary>
    /// Opens the containing folder of the selected file.
    /// </summary>
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel viewModel && viewModel.SelectedFile != null)
        {
            viewModel.SelectedFile.OpenContainingFolder();
        }
    }

    /// <summary>
    /// Copies the full path of the selected file to clipboard.
    /// </summary>
    private void CopyFullPath_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel viewModel && viewModel.SelectedFile != null)
        {
            viewModel.SelectedFile.CopyFullPathToClipboard();
        }
    }

    /// <summary>
    /// Copies the folder path (without filename) of the selected file to clipboard.
    /// </summary>
    private void CopyFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel viewModel && viewModel.SelectedFile != null)
        {
            viewModel.SelectedFile.CopyFolderPathToClipboard();
        }
    }

    #endregion

    #region Duplicate Location Event Handlers

    /// <summary>
    /// Opens the duplicate file in default viewer when double-clicking.
    /// </summary>
    private void DuplicateLocation_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is DuplicateLocationItem item)
        {
            item.OpenInDefaultViewer();
        }
    }

    /// <summary>
    /// Opens the selected duplicate file in default viewer.
    /// </summary>
    private void DuplicateOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedDuplicateLocation() is DuplicateLocationItem item)
        {
            item.OpenInDefaultViewer();
        }
    }

    /// <summary>
    /// Opens the containing folder of the selected duplicate file.
    /// </summary>
    private void DuplicateOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedDuplicateLocation() is DuplicateLocationItem item)
        {
            item.OpenContainingFolder();
        }
    }

    /// <summary>
    /// Copies the full path of the selected duplicate file to clipboard.
    /// </summary>
    private void DuplicateCopyFullPath_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedDuplicateLocation() is DuplicateLocationItem item)
        {
            item.CopyFullPathToClipboard();
        }
    }

    /// <summary>
    /// Copies the folder path (without filename) of the selected duplicate file to clipboard.
    /// </summary>
    private void DuplicateCopyFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedDuplicateLocation() is DuplicateLocationItem item)
        {
            item.CopyFolderPathToClipboard();
        }
    }

    /// <summary>
    /// Gets the selected duplicate location from the context menu source.
    /// </summary>
    private DuplicateLocationItem? GetSelectedDuplicateLocation()
    {
        // Try to get from the context menu's placement target
        if (ContextMenuService.GetPlacementTarget(this) is ListBox listBox)
        {
            return listBox.SelectedItem as DuplicateLocationItem;
        }

        // Fallback: check ViewModel's duplicate locations
        if (DataContext is PlanViewModel viewModel && viewModel.DuplicateLocations.Count > 0)
        {
            // Find any ListBox within this control and get its selected item
            var listBoxes = FindVisualChildren<ListBox>(this);
            foreach (var lb in listBoxes)
            {
                if (lb.ItemsSource == viewModel.DuplicateLocations && lb.SelectedItem is DuplicateLocationItem item)
                {
                    return item;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds all visual children of a specific type.
    /// </summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;

        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var grandChild in FindVisualChildren<T>(child))
            {
                yield return grandChild;
            }
        }
    }

    #endregion
}
