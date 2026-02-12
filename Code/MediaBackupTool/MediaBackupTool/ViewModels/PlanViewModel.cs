using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Data.Repositories;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Infrastructure.State;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Plan page - folder tree editor with file preview.
/// </summary>
public partial class PlanViewModel : ViewModelBase
{
    private readonly ILogger<PlanViewModel> _logger;
    private readonly AppStateManager _stateManager;
    private readonly IProjectService _projectService;
    private readonly NavigationService _navigationService;
    private readonly FolderNodeRepository _folderNodeRepo;
    private readonly UniqueFileRepository _uniqueFileRepo;
    private readonly IThumbnailService _thumbnailService;

    private const int PageSize = 100;

    [ObservableProperty]
    private ObservableCollection<LazyFolderNodeViewModel> _rootFolders = new();

    [ObservableProperty]
    private LazyFolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    private ObservableCollection<UniqueFileListItem> _filesInFolder = new();

    [ObservableProperty]
    private UniqueFileListItem? _selectedFile;

    [ObservableProperty]
    private ObservableCollection<DuplicateLocationItem> _duplicateLocations = new();

    [ObservableProperty]
    private BitmapImage? _previewImage;

    [ObservableProperty]
    private int _totalUniqueFiles;

    [ObservableProperty]
    private int _totalDuplicates;

    [ObservableProperty]
    private long _totalSizeBytes;

    [ObservableProperty]
    private int _scannedFileCount;

    [ObservableProperty]
    private int _hashedFileCount;

    [ObservableProperty]
    private string _workflowStatus = string.Empty;

    [ObservableProperty]
    private string _workflowMessage = string.Empty;

    [ObservableProperty]
    private bool _needsScanning;

    [ObservableProperty]
    private bool _needsHashing;

    [ObservableProperty]
    private bool _needsPlanning;

    [ObservableProperty]
    private bool _hasPlanData;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _totalFilesInFolder;

    [ObservableProperty]
    private bool _isLoadingFiles;

    [ObservableProperty]
    private bool _isLoadingPreview;

    [ObservableProperty]
    private long _estimatedSizeBytes;

    [ObservableProperty]
    private int _estimatedFileCount;

    [ObservableProperty]
    private bool _isRecalculatingSize;

    public AppStateManager State => _stateManager;

    /// <summary>
    /// Gets the formatted estimated size string.
    /// </summary>
    public string EstimatedSizeFormatted
    {
        get
        {
            if (EstimatedSizeBytes < 1024) return $"{EstimatedSizeBytes} B";
            if (EstimatedSizeBytes < 1024 * 1024) return $"{EstimatedSizeBytes / 1024.0:F1} KB";
            if (EstimatedSizeBytes < 1024 * 1024 * 1024) return $"{EstimatedSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{EstimatedSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    partial void OnEstimatedSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(EstimatedSizeFormatted));
    }

    public PlanViewModel(
        ILogger<PlanViewModel> logger,
        AppStateManager stateManager,
        IProjectService projectService,
        NavigationService navigationService,
        FolderNodeRepository folderNodeRepo,
        UniqueFileRepository uniqueFileRepo,
        IThumbnailService thumbnailService)
    {
        _logger = logger;
        _stateManager = stateManager;
        _projectService = projectService;
        _navigationService = navigationService;
        _folderNodeRepo = folderNodeRepo;
        _uniqueFileRepo = uniqueFileRepo;
        _thumbnailService = thumbnailService;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();
        await RefreshWorkflowStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGeneratePlan))]
    private async Task GeneratePlanAsync()
    {
        _logger.LogInformation("Generating folder plan...");
        SetBusy(true, "Generating plan - organizing files by date...");

        try
        {
            var result = await _projectService.GeneratePlanAsync();

            if (result.Success)
            {
                TotalUniqueFiles = result.UniqueFilesCreated;
                TotalDuplicates = result.DuplicatesFound;

                _logger.LogInformation("Plan generated: {Unique} unique files, {Duplicates} duplicates, {Folders} folders",
                    result.UniqueFilesCreated, result.DuplicatesFound, result.FoldersCreated);

                // Transition to ReadyToCopy state
                _stateManager.TryTransitionTo(AppState.ReadyToCopy, "Plan generated");

                // Refresh workflow status and load folder tree
                await RefreshWorkflowStatusAsync();
            }
            else
            {
                _logger.LogError("Plan generation failed: {Error}", result.ErrorMessage);
                WorkflowStatus = "Error";
                WorkflowMessage = $"Plan generation failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plan generation failed");
            WorkflowStatus = "Error";
            WorkflowMessage = $"Plan generation failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool CanGeneratePlan() => NeedsPlanning || HasPlanData;

    [RelayCommand]
    private async Task ToggleFolderEnabledAsync(LazyFolderNodeViewModel folder)
    {
        // Note: CopyEnabled is already set by the checkbox binding before this command runs
        var newValue = folder.CopyEnabled;

        // Persist to database (cascade to children)
        await _folderNodeRepo.UpdateCopyEnabledCascadeAsync(folder.FolderId, newValue);

        // Update children in the UI if they're loaded
        UpdateChildrenCopyEnabled(folder, newValue);

        _logger.LogInformation("Toggled folder {Name} enabled: {Enabled}", folder.DisplayName, newValue);

        // Recalculate the estimated size
        await RecalculateEstimatedSizeAsync();
    }

    /// <summary>
    /// Recursively updates the CopyEnabled status of all loaded children.
    /// </summary>
    private void UpdateChildrenCopyEnabled(LazyFolderNodeViewModel folder, bool enabled)
    {
        foreach (var child in folder.Children)
        {
            if (child.FolderId != -1) // Skip dummy nodes
            {
                child.CopyEnabled = enabled;
                UpdateChildrenCopyEnabled(child, enabled);
            }
        }
    }

    /// <summary>
    /// Recalculates the estimated disk size based on enabled folders.
    /// </summary>
    public async Task RecalculateEstimatedSizeAsync()
    {
        IsRecalculatingSize = true;
        try
        {
            EstimatedSizeBytes = await _folderNodeRepo.GetTotalEnabledSizeAsync();
            EstimatedFileCount = await _folderNodeRepo.GetTotalEnabledFileCountAsync();
            _logger.LogDebug("Recalculated: {Files:N0} files, {Size} bytes", EstimatedFileCount, EstimatedSizeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recalculate estimated size");
        }
        finally
        {
            IsRecalculatingSize = false;
        }
    }

    [RelayCommand]
    private async Task NavigateToScanAsync()
    {
        await _navigationService.NavigateToAsync("Scan");
    }

    [RelayCommand]
    private async Task NavigateToHashAsync()
    {
        _stateManager.TryTransitionTo(AppState.Hashing, "User navigated to Hash");
        await _navigationService.NavigateToAsync("Hash");
    }

    [RelayCommand]
    private async Task NavigateToSourcesAsync()
    {
        await _navigationService.NavigateToAsync("Sources");
    }

    [RelayCommand]
    private async Task NavigateToCopyAsync()
    {
        _stateManager.TryTransitionTo(AppState.Copying, "User navigated to Copy");
        await _navigationService.NavigateToAsync("Copy");
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage > 1 && SelectedFolder != null)
        {
            CurrentPage--;
            await LoadFilesInFolderAsync(SelectedFolder.FolderId, CurrentPage);
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage < TotalPages && SelectedFolder != null)
        {
            CurrentPage++;
            await LoadFilesInFolderAsync(SelectedFolder.FolderId, CurrentPage);
        }
    }

    partial void OnSelectedFolderChanged(LazyFolderNodeViewModel? value)
    {
        if (value != null)
        {
            CurrentPage = 1;
            _ = LoadFilesInFolderAsync(value.FolderId, 1);
        }
        else
        {
            FilesInFolder.Clear();
            TotalFilesInFolder = 0;
            TotalPages = 1;
        }
    }

    partial void OnSelectedFileChanged(UniqueFileListItem? value)
    {
        if (value != null)
        {
            _ = LoadFileDetailsAsync(value);
        }
        else
        {
            DuplicateLocations.Clear();
            PreviewImage = null;
        }
    }

    private async Task LoadFilesInFolderAsync(long folderId, int page)
    {
        IsLoadingFiles = true;
        try
        {
            var offset = (page - 1) * PageSize;
            var files = await _uniqueFileRepo.GetByFolderIdAsync(folderId, offset, PageSize);
            TotalFilesInFolder = await _uniqueFileRepo.GetCountByFolderIdAsync(folderId);
            TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalFilesInFolder / PageSize));

            FilesInFolder.Clear();
            foreach (var file in files)
            {
                FilesInFolder.Add(new UniqueFileListItem
                {
                    UniqueFileId = file.UniqueFileId,
                    HashId = file.HashId,
                    FileName = file.FileName,
                    Extension = file.Extension,
                    SizeBytes = file.SizeBytes,
                    SizeFormatted = file.SizeFormatted,
                    DuplicateCount = file.DuplicateCount,
                    FullPath = file.FullPath,
                    HashHex = file.HashHex,
                    CopyEnabled = file.CopyEnabled
                });
            }

            _logger.LogDebug("Loaded {Count} files for folder {FolderId}, page {Page}/{TotalPages}",
                files.Count, folderId, page, TotalPages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load files for folder {FolderId}", folderId);
        }
        finally
        {
            IsLoadingFiles = false;
        }
    }

    private async Task LoadFileDetailsAsync(UniqueFileListItem file)
    {
        // Load duplicate locations
        try
        {
            var duplicates = await _uniqueFileRepo.GetDuplicateInstancesAsync(file.HashId);
            DuplicateLocations.Clear();
            foreach (var dup in duplicates)
            {
                DuplicateLocations.Add(new DuplicateLocationItem
                {
                    FileInstanceId = dup.FileInstanceId,
                    FileName = dup.FileName,
                    FullPath = dup.FullPath,
                    DisplayPath = dup.DisplayPath,
                    ScanRootLabel = dup.ScanRootLabel,
                    ModifiedUtc = dup.ModifiedUtc
                });
            }

            _logger.LogDebug("Loaded {Count} duplicate locations for file {FileName}", duplicates.Count, file.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load duplicate locations for file {FileId}", file.UniqueFileId);
        }

        // Load preview image
        await LoadPreviewAsync(file.FullPath);
    }

    private async Task LoadPreviewAsync(string filePath)
    {
        IsLoadingPreview = true;
        try
        {
            PreviewImage = await _thumbnailService.GetThumbnailAsync(filePath, 400, 400);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load preview for {FilePath}", filePath);
            PreviewImage = null;
        }
        finally
        {
            IsLoadingPreview = false;
        }
    }

    private async Task LoadRootFoldersAsync()
    {
        try
        {
            var roots = await _folderNodeRepo.GetRootNodesAsync();
            RootFolders.Clear();

            foreach (var root in roots)
            {
                var hasChildren = await _folderNodeRepo.HasChildrenAsync(root.FolderNodeId);
                RootFolders.Add(new LazyFolderNodeViewModel(root, hasChildren, _folderNodeRepo, _logger));
            }

            _logger.LogInformation("Loaded {Count} root folders", roots.Count);

            // Calculate initial estimated size
            await RecalculateEstimatedSizeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load root folders");
        }
    }

    private async Task RefreshWorkflowStatusAsync()
    {
        if (!_projectService.IsProjectOpen)
        {
            WorkflowStatus = "No Project Open";
            WorkflowMessage = "Please create or open a project first.";
            NeedsScanning = false;
            NeedsHashing = false;
            NeedsPlanning = false;
            HasPlanData = false;
            return;
        }

        try
        {
            var stats = await _projectService.GetProjectStatsAsync();
            ScannedFileCount = stats.TotalFiles;
            HashedFileCount = stats.HashedFiles;
            TotalUniqueFiles = stats.UniqueFiles;
            TotalDuplicates = stats.DuplicateFiles;

            // Check if we have folder nodes
            var folderCount = await _folderNodeRepo.GetTotalCountAsync();

            if (ScannedFileCount == 0)
            {
                WorkflowStatus = "Step 1: Scan Files";
                WorkflowMessage = "No files have been scanned yet. Go to Sources to add folders, then run a scan to discover media files.";
                NeedsScanning = true;
                NeedsHashing = false;
                NeedsPlanning = false;
                HasPlanData = false;
            }
            else if (HashedFileCount == 0)
            {
                WorkflowStatus = "Step 2: Hash Files";
                WorkflowMessage = $"Found {ScannedFileCount:N0} files during scan. Next step is to compute file hashes to detect duplicates.";
                NeedsScanning = false;
                NeedsHashing = true;
                NeedsPlanning = false;
                HasPlanData = false;
            }
            else if (TotalUniqueFiles == 0 || folderCount == 0)
            {
                WorkflowStatus = "Step 3: Generate Plan";
                WorkflowMessage = $"Hashed {HashedFileCount:N0} files. Click 'Generate Plan' to organize files and detect duplicates.";
                NeedsScanning = false;
                NeedsHashing = false;
                NeedsPlanning = true;
                HasPlanData = false;
            }
            else
            {
                WorkflowStatus = "Plan Ready";
                WorkflowMessage = $"Found {TotalUniqueFiles:N0} unique files and {TotalDuplicates:N0} duplicates organized into {folderCount} folders.";
                NeedsScanning = false;
                NeedsHashing = false;
                NeedsPlanning = false;
                HasPlanData = true;

                // Load the folder tree
                await LoadRootFoldersAsync();
            }

            GeneratePlanCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh workflow status");
            WorkflowStatus = "Error";
            WorkflowMessage = $"Failed to load project statistics: {ex.Message}";
        }
    }
}

/// <summary>
/// Lazy-loading folder node for efficient tree display.
/// </summary>
public partial class LazyFolderNodeViewModel : ObservableObject
{
    private readonly FolderNodeRepository _folderNodeRepo;
    private readonly ILogger _logger;
    private bool _childrenLoaded;

    [ObservableProperty]
    private long _folderId;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _copyEnabled = true;

    [ObservableProperty]
    private int _uniqueCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicates))]
    [NotifyPropertyChangedFor(nameof(DuplicateDisplayText))]
    private int _duplicateCount;

    [ObservableProperty]
    private long _totalSizeBytes;

    [ObservableProperty]
    private string? _whyExplanation;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ObservableCollection<LazyFolderNodeViewModel> _children = new();

    public bool HasDummyChild => Children.Count == 1 && Children[0].FolderId == -1;

    public LazyFolderNodeViewModel()
    {
        _folderNodeRepo = null!;
        _logger = null!;
    }

    public LazyFolderNodeViewModel(FolderNode node, bool hasChildren, FolderNodeRepository repo, ILogger logger)
    {
        _folderNodeRepo = repo;
        _logger = logger;

        FolderId = node.FolderNodeId;
        DisplayName = node.EffectiveDisplayName;
        CopyEnabled = node.CopyEnabled;
        UniqueCount = node.UniqueCount;
        DuplicateCount = node.DuplicateCount;
        TotalSizeBytes = node.TotalSizeBytes;
        WhyExplanation = node.WhyExplanation;

        // Add dummy child to show expander
        if (hasChildren)
        {
            Children.Add(new LazyFolderNodeViewModel { FolderId = -1, DisplayName = "Loading..." });
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded && HasDummyChild)
        {
            _ = LoadChildrenAsync();
        }
    }

    private async Task LoadChildrenAsync()
    {
        try
        {
            var childNodes = await _folderNodeRepo.GetChildrenAsync(FolderId);

            Children.Clear();
            foreach (var child in childNodes)
            {
                var hasGrandchildren = await _folderNodeRepo.HasChildrenAsync(child.FolderNodeId);
                Children.Add(new LazyFolderNodeViewModel(child, hasGrandchildren, _folderNodeRepo, _logger));
            }

            _childrenLoaded = true;
            _logger.LogDebug("Loaded {Count} children for folder {FolderId}", childNodes.Count, FolderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load children for folder {FolderId}", FolderId);
            Children.Clear();
        }
    }

    public string SizeFormatted
    {
        get
        {
            if (TotalSizeBytes < 1024) return $"{TotalSizeBytes} B";
            if (TotalSizeBytes < 1024 * 1024) return $"{TotalSizeBytes / 1024.0:F1} KB";
            if (TotalSizeBytes < 1024 * 1024 * 1024) return $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    /// <summary>
    /// Gets whether this folder has any duplicates (for display purposes).
    /// </summary>
    public bool HasDuplicates => DuplicateCount > 0;

    /// <summary>
    /// Formatted string showing duplicate count for display.
    /// </summary>
    public string DuplicateDisplayText => $" [+{DuplicateCount:N0} dups]";
}

/// <summary>
/// ViewModel for displaying a unique file in the file list.
/// </summary>
public partial class UniqueFileListItem : ObservableObject
{
    [ObservableProperty]
    private long _uniqueFileId;

    [ObservableProperty]
    private long _hashId;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _extension = string.Empty;

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    private string _sizeFormatted = string.Empty;

    [ObservableProperty]
    private int _duplicateCount;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private string _hashHex = string.Empty;

    [ObservableProperty]
    private bool _copyEnabled = true;

    /// <summary>
    /// Gets the folder path (without the filename).
    /// </summary>
    public string FolderPath => Path.GetDirectoryName(FullPath) ?? string.Empty;

    /// <summary>
    /// Opens the file in the default Windows viewer.
    /// </summary>
    public void OpenInDefaultViewer()
    {
        if (File.Exists(FullPath))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = FullPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
    }

    /// <summary>
    /// Opens the containing folder in Windows Explorer with the file selected.
    /// </summary>
    public void OpenContainingFolder()
    {
        if (File.Exists(FullPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{FullPath}\"");
        }
        else if (Directory.Exists(FolderPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", FolderPath);
        }
    }

    /// <summary>
    /// Copies the full file path to the clipboard.
    /// </summary>
    public void CopyFullPathToClipboard()
    {
        if (!string.IsNullOrEmpty(FullPath))
        {
            System.Windows.Clipboard.SetText(FullPath);
        }
    }

    /// <summary>
    /// Copies the folder path (without filename) to the clipboard.
    /// </summary>
    public void CopyFolderPathToClipboard()
    {
        if (!string.IsNullOrEmpty(FolderPath))
        {
            System.Windows.Clipboard.SetText(FolderPath);
        }
    }
}

/// <summary>
/// ViewModel for displaying a duplicate location in the details panel.
/// </summary>
public partial class DuplicateLocationItem : ObservableObject
{
    [ObservableProperty]
    private long _fileInstanceId;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private string _displayPath = string.Empty;

    [ObservableProperty]
    private string _scanRootLabel = string.Empty;

    [ObservableProperty]
    private DateTime _modifiedUtc;

    /// <summary>
    /// Gets the folder path (without the filename).
    /// </summary>
    public string FolderPath => Path.GetDirectoryName(FullPath) ?? string.Empty;

    /// <summary>
    /// Opens the file in the default Windows viewer.
    /// </summary>
    public void OpenInDefaultViewer()
    {
        if (File.Exists(FullPath))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = FullPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
    }

    /// <summary>
    /// Opens the containing folder in Windows Explorer with the file selected.
    /// </summary>
    public void OpenContainingFolder()
    {
        if (File.Exists(FullPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{FullPath}\"");
        }
        else if (Directory.Exists(FolderPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", FolderPath);
        }
    }

    /// <summary>
    /// Copies the full file path to the clipboard.
    /// </summary>
    public void CopyFullPathToClipboard()
    {
        if (!string.IsNullOrEmpty(FullPath))
        {
            System.Windows.Clipboard.SetText(FullPath);
        }
    }

    /// <summary>
    /// Copies the folder path (without filename) to the clipboard.
    /// </summary>
    public void CopyFolderPathToClipboard()
    {
        if (!string.IsNullOrEmpty(FolderPath))
        {
            System.Windows.Clipboard.SetText(FolderPath);
        }
    }
}
