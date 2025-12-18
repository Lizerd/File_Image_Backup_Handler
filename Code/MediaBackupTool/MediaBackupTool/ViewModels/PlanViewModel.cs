using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Plan page - folder tree editor.
/// </summary>
public partial class PlanViewModel : ViewModelBase
{
    private readonly ILogger<PlanViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<FolderNodeViewModel> _rootFolders = new();

    [ObservableProperty]
    private FolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    private ObservableCollection<UniqueFileViewModel> _filesInFolder = new();

    [ObservableProperty]
    private int _totalUniqueFiles;

    [ObservableProperty]
    private int _totalDuplicates;

    [ObservableProperty]
    private long _totalSizeBytes;

    public PlanViewModel(ILogger<PlanViewModel> logger)
    {
        _logger = logger;
    }

    [RelayCommand]
    private async Task GeneratePlanAsync()
    {
        _logger.LogInformation("Generating folder plan...");
        SetBusy(true, "Generating folder structure...");

        // TODO: Implement in Phase 4
        await Task.Delay(100);

        SetBusy(false);
    }

    [RelayCommand]
    private void ToggleFolderEnabled(FolderNodeViewModel folder)
    {
        folder.CopyEnabled = !folder.CopyEnabled;
        _logger.LogInformation("Toggled folder {Name} enabled: {Enabled}", folder.DisplayName, folder.CopyEnabled);
    }

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        if (value != null)
        {
            LoadFilesInFolder(value);
        }
        else
        {
            FilesInFolder.Clear();
        }
    }

    private void LoadFilesInFolder(FolderNodeViewModel folder)
    {
        // TODO: Load files from database
        FilesInFolder.Clear();
    }
}

/// <summary>
/// ViewModel for a folder node in the tree.
/// </summary>
public partial class FolderNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private long _folderId;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _copyEnabled = true;

    [ObservableProperty]
    private int _uniqueCount;

    [ObservableProperty]
    private long _totalSizeBytes;

    [ObservableProperty]
    private string? _whyExplanation;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private ObservableCollection<FolderNodeViewModel> _children = new();
}

/// <summary>
/// ViewModel for displaying a unique file in the details panel.
/// </summary>
public partial class UniqueFileViewModel : ObservableObject
{
    [ObservableProperty]
    private long _uniqueFileId;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    private int _duplicateCount;

    [ObservableProperty]
    private string _sourcePath = string.Empty;
}
