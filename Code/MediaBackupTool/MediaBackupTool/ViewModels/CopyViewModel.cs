using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Infrastructure.State;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Copy page - copy execution and progress.
/// </summary>
public partial class CopyViewModel : ViewModelBase
{
    private readonly ILogger<CopyViewModel> _logger;
    private readonly AppStateManager _stateManager;

    public AppStateManager State => _stateManager;

    [ObservableProperty]
    private string? _targetPath;

    [ObservableProperty]
    private bool _verifyAfterCopy = true;

    [ObservableProperty]
    private long _filesCopied;

    [ObservableProperty]
    private long _totalToCopy;

    [ObservableProperty]
    private long _bytesCopied;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private double _mbPerSecond;

    [ObservableProperty]
    private string? _currentFile;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private ObservableCollection<FailedCopyJob> _failedJobs = new();

    public CopyViewModel(ILogger<CopyViewModel> logger, AppStateManager stateManager)
    {
        _logger = logger;
        _stateManager = stateManager;
    }

    [RelayCommand]
    private void BrowseTargetPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            TargetPath = dialog.FolderName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartCopy))]
    private async Task StartCopyAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            _logger.LogWarning("Cannot start copy: no target path selected");
            return;
        }

        _logger.LogInformation("Starting copy to {Path}...", TargetPath);
        _stateManager.TryTransitionTo(Models.Enums.AppState.Copying, "User started copy");

        // TODO: Implement in Phase 5
        await Task.Delay(100);
    }

    private bool CanStartCopy() => _stateManager.CanStartCopy && !string.IsNullOrWhiteSpace(TargetPath);

    [RelayCommand]
    private void PauseCopy()
    {
        _logger.LogInformation("Pausing copy...");
        _stateManager.TryTransitionTo(Models.Enums.AppState.CopyPaused, "User paused copy");
    }

    [RelayCommand]
    private void ResumeCopy()
    {
        _logger.LogInformation("Resuming copy...");
        _stateManager.TryTransitionTo(Models.Enums.AppState.Copying, "User resumed copy");
    }

    [RelayCommand]
    private void CancelCopy()
    {
        _logger.LogInformation("Cancelling copy...");
        _stateManager.TryTransitionTo(Models.Enums.AppState.Idle, "User cancelled copy");
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        _logger.LogInformation("Retrying {Count} failed jobs...", FailedJobs.Count);
        // TODO: Implement retry logic
        await Task.Delay(100);
    }
}

public class FailedCopyJob
{
    public long CopyJobId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int Attempts { get; set; }
}
