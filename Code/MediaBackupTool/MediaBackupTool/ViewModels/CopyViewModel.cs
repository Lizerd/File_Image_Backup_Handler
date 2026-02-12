using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Data.Repositories;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Infrastructure.State;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;

// Import for CopyCompletionStats
using static MediaBackupTool.Data.Repositories.CopyJobRepository;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Copy page - copy execution and progress.
/// </summary>
public partial class CopyViewModel : ViewModelBase
{
    private readonly ILogger<CopyViewModel> _logger;
    private readonly AppStateManager _stateManager;
    private readonly IProjectService _projectService;
    private readonly ICopyService _copyService;
    private readonly CopyJobRepository _copyJobRepo;
    private readonly NavigationService _navigationService;

    private CancellationTokenSource? _copyCts;

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

    [ObservableProperty]
    private bool _isCopying;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private string _statusMessage = "Ready to copy";

    [ObservableProperty]
    private long _filesVerified;

    [ObservableProperty]
    private long _filesSkipped;

    [ObservableProperty]
    private string? _estimatedTimeRemaining;

    // Completion summary properties
    [ObservableProperty]
    private bool _showCompletionSummary;

    [ObservableProperty]
    private CopyCompletionStats? _completionStats;

    [ObservableProperty]
    private ObservableCollection<ExtensionStatsDisplay> _extensionBreakdown = new();

    [ObservableProperty]
    private string _totalDuration = string.Empty;

    [ObservableProperty]
    private string _averageSpeed = string.Empty;

    [ObservableProperty]
    private string _timePerFile = string.Empty;

    [ObservableProperty]
    private string _filesPerSecond = string.Empty;

    [ObservableProperty]
    private string _totalSizeCopied = string.Empty;

    [ObservableProperty]
    private string _secondsPerGB = string.Empty;

    public CopyViewModel(
        ILogger<CopyViewModel> logger,
        AppStateManager stateManager,
        IProjectService projectService,
        ICopyService copyService,
        CopyJobRepository copyJobRepo,
        NavigationService navigationService)
    {
        _logger = logger;
        _stateManager = stateManager;
        _projectService = projectService;
        _copyService = copyService;
        _copyJobRepo = copyJobRepo;
        _navigationService = navigationService;

        // Subscribe to copy service events
        _copyService.ProgressChanged += OnProgressChanged;
        _copyService.CopyCompleted += OnCopyCompleted;
        _copyService.JobFailed += OnJobFailed;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();
        await RefreshStatsAsync();
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
            StartCopyCommand.NotifyCanExecuteChanged();
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

        IsCopying = true;
        IsPaused = false;
        IsCompleted = false;
        StatusMessage = "Preparing copy jobs...";
        FailedJobs.Clear();
        FailedCount = 0;

        _stateManager.TryTransitionTo(AppState.Copying, "User started copy");

        _copyCts = new CancellationTokenSource();

        try
        {
            await _copyService.StartCopyAsync(TargetPath, VerifyAfterCopy, _copyCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Copy cancelled";
            _logger.LogInformation("Copy cancelled by user");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
            _logger.LogError(ex, "Copy failed");
        }
        finally
        {
            IsCopying = false;
            NotifyCommandsCanExecuteChanged();
        }
    }

    private bool CanStartCopy() =>
        _projectService.IsProjectOpen &&
        !string.IsNullOrWhiteSpace(TargetPath) &&
        !IsCopying;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void PauseCopy()
    {
        _logger.LogInformation("Pausing copy...");
        _copyService.Pause();
        IsPaused = true;
        StatusMessage = "Paused";
        _stateManager.TryTransitionTo(AppState.CopyPaused, "User paused copy");
        NotifyCommandsCanExecuteChanged();
    }

    private bool CanPause() => IsCopying && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void ResumeCopy()
    {
        _logger.LogInformation("Resuming copy...");
        _copyService.Resume();
        IsPaused = false;
        StatusMessage = "Copying...";
        _stateManager.TryTransitionTo(AppState.Copying, "User resumed copy");
        NotifyCommandsCanExecuteChanged();
    }

    private bool CanResume() => IsCopying && IsPaused;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelCopy()
    {
        _logger.LogInformation("Cancelling copy...");
        _copyCts?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private bool CanCancel() => IsCopying;

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync()
    {
        _logger.LogInformation("Retrying {Count} failed jobs...", FailedJobs.Count);

        IsCopying = true;
        StatusMessage = "Retrying failed jobs...";
        FailedJobs.Clear();

        _copyCts = new CancellationTokenSource();

        try
        {
            await _copyService.RetryFailedAsync(_copyCts.Token);
            await RefreshFailedJobsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry failed");
            StatusMessage = $"Retry failed: {ex.Message}";
        }
        finally
        {
            IsCopying = false;
            NotifyCommandsCanExecuteChanged();
        }
    }

    private bool CanRetryFailed() => !IsCopying && FailedCount > 0;

    [RelayCommand]
    private async Task NavigateToPlanAsync()
    {
        await _navigationService.NavigateToAsync("Plan");
    }

    [RelayCommand]
    private async Task NavigateToDuplicatesAsync()
    {
        await _navigationService.NavigateToAsync("Duplicates");
    }

    [RelayCommand]
    private async Task NavigateToVerificationAsync()
    {
        await _navigationService.NavigateToAsync("Verification");
    }

    private void OnProgressChanged(object? sender, CopyProgress progress)
    {
        // Marshal to UI thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            FilesCopied = progress.FilesCopied;
            TotalToCopy = progress.TotalFiles;
            BytesCopied = progress.BytesCopied;
            TotalBytes = progress.TotalBytes;
            Progress = progress.PercentComplete;
            MbPerSecond = progress.MBPerSecond;
            CurrentFile = progress.CurrentFile;
            FailedCount = progress.FailedCount;
            FilesVerified = progress.FilesVerified;
            FilesSkipped = progress.FilesSkipped;
            IsPaused = progress.IsPaused;

            if (progress.EstimatedRemaining.HasValue)
            {
                var remaining = progress.EstimatedRemaining.Value;
                if (remaining.TotalHours >= 1)
                {
                    EstimatedTimeRemaining = $"{remaining.Hours}h {remaining.Minutes}m remaining";
                }
                else if (remaining.TotalMinutes >= 1)
                {
                    EstimatedTimeRemaining = $"{remaining.Minutes}m {remaining.Seconds}s remaining";
                }
                else
                {
                    EstimatedTimeRemaining = $"{remaining.Seconds}s remaining";
                }
            }
            else
            {
                EstimatedTimeRemaining = null;
            }

            StatusMessage = IsPaused ? "Paused" : $"Copying... {progress.FilesCopied:N0} / {progress.TotalFiles:N0} files";
        });
    }

    private async void OnCopyCompleted(object? sender, CopyCompletedEventArgs e)
    {
        // Use BeginInvoke to properly handle async operations on UI thread
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsCopying = false;
            IsCompleted = true;

            _logger.LogInformation("OnCopyCompleted: Success={Success}, FilesCopied={Files}, HasDetailedStats={HasStats}",
                e.Success, e.TotalFilesCopied, e.DetailedStats != null);

            if (e.Success)
            {
                StatusMessage = $"Completed! {e.TotalFilesCopied:N0} files copied";
                if (e.FailedCount > 0)
                {
                    StatusMessage += $", {e.FailedCount} failed";
                }
                _stateManager.TryTransitionTo(AppState.Completed, "Copy completed");

                // Populate completion summary
                if (e.DetailedStats != null)
                {
                    PopulateCompletionSummary(e.DetailedStats);
                }
                else
                {
                    _logger.LogWarning("DetailedStats is null - completion summary will not be shown");
                    ShowCompletionSummary = false;
                }
            }
            else
            {
                StatusMessage = e.ErrorMessage ?? "Copy failed";
                _stateManager.TryTransitionTo(AppState.Idle, "Copy failed");
                ShowCompletionSummary = false;
            }

            NotifyCommandsCanExecuteChanged();
        });

        // Refresh failed jobs asynchronously after UI updates
        try
        {
            await RefreshFailedJobsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh failed jobs after copy completion");
        }
    }

    /// <summary>
    /// Populates the completion summary properties from detailed stats.
    /// </summary>
    private void PopulateCompletionSummary(CopyCompletionStats stats)
    {
        _logger.LogInformation("PopulateCompletionSummary called with {Files} files, {Extensions} extensions",
            stats.TotalFilesCopied, stats.ExtensionBreakdown.Count);

        CompletionStats = stats;
        ShowCompletionSummary = true;

        // Timing stats
        TotalDuration = stats.DurationFormatted;
        AverageSpeed = $"{stats.MBPerSecond:F2} MB/s";
        TimePerFile = stats.TimePerFileFormatted;
        FilesPerSecond = $"{stats.FilesPerSecond:F1} files/s";
        TotalSizeCopied = stats.TotalSizeFormatted;

        // Calculate seconds per GB (1024 MB / speed)
        if (stats.MBPerSecond > 0)
        {
            var secondsFor1GB = 1024.0 / stats.MBPerSecond;
            if (secondsFor1GB >= 60)
            {
                var minutes = (int)(secondsFor1GB / 60);
                var seconds = (int)(secondsFor1GB % 60);
                SecondsPerGB = $"{minutes}m {seconds}s";
            }
            else
            {
                SecondsPerGB = $"{secondsFor1GB:F1} seconds";
            }
        }
        else
        {
            SecondsPerGB = "N/A";
        }

        // Extension breakdown
        ExtensionBreakdown.Clear();
        foreach (var ext in stats.ExtensionBreakdown)
        {
            ExtensionBreakdown.Add(new ExtensionStatsDisplay
            {
                Extension = ext.Extension,
                FileCount = ext.FileCount,
                TotalBytes = ext.TotalBytes,
                SizeFormatted = ext.SizeFormatted
            });
        }

        _logger.LogInformation("Completion summary populated: {Duration}, {Speed}, {FilesPerSec}/s, ShowCompletionSummary={Show}",
            TotalDuration, AverageSpeed, FilesPerSecond, ShowCompletionSummary);
    }

    private void OnJobFailed(object? sender, CopyJobFailedEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            FailedJobs.Add(new FailedCopyJob
            {
                CopyJobId = e.CopyJobId,
                FileName = e.FileName,
                SourcePath = e.SourcePath,
                Error = e.ErrorMessage,
                Attempts = e.AttemptCount
            });
            FailedCount = FailedJobs.Count;
        });
    }

    private async Task RefreshStatsAsync()
    {
        if (!_projectService.IsProjectOpen)
            return;

        try
        {
            var stats = await _copyJobRepo.GetStatsAsync();
            TotalToCopy = stats.Total;
            FilesCopied = stats.Completed;
            FailedCount = stats.Error;

            if (stats.Total > 0 && stats.Completed == stats.Total)
            {
                IsCompleted = true;
                StatusMessage = $"Completed! {stats.Completed:N0} files copied";
            }
            else if (stats.Total > 0)
            {
                StatusMessage = $"Ready to copy {stats.Pending:N0} files";
            }

            await RefreshFailedJobsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh stats (may be no jobs yet)");
        }
    }

    private async Task RefreshFailedJobsAsync()
    {
        try
        {
            var failedJobs = await _copyJobRepo.GetFailedJobsForDisplayAsync();
            FailedJobs.Clear();
            foreach (var job in failedJobs)
            {
                FailedJobs.Add(new FailedCopyJob
                {
                    CopyJobId = job.CopyJobId,
                    FileName = job.FileName,
                    SourcePath = job.SourcePath,
                    Error = job.LastError,
                    Attempts = job.AttemptCount
                });
            }
            FailedCount = FailedJobs.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh failed jobs");
        }
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        StartCopyCommand.NotifyCanExecuteChanged();
        PauseCopyCommand.NotifyCanExecuteChanged();
        ResumeCopyCommand.NotifyCanExecuteChanged();
        CancelCopyCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
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

/// <summary>
/// Display model for extension statistics in the completion summary.
/// </summary>
public class ExtensionStatsDisplay
{
    public string Extension { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string SizeFormatted { get; set; } = string.Empty;

    /// <summary>
    /// Display text combining count and size.
    /// </summary>
    public string DisplayText => $"{FileCount:N0} files ({SizeFormatted})";
}
