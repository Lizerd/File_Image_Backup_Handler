using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Infrastructure.State;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Verification page - post-copy file verification.
/// </summary>
public partial class VerificationViewModel : ViewModelBase
{
    private readonly ILogger<VerificationViewModel> _logger;
    private readonly AppStateManager _stateManager;
    private readonly IProjectService _projectService;
    private readonly IVerificationService _verificationService;
    private readonly NavigationService _navigationService;

    private CancellationTokenSource? _verifyCts;

    public AppStateManager State => _stateManager;

    [ObservableProperty]
    private long _totalFiles;

    [ObservableProperty]
    private long _filesVerified;

    [ObservableProperty]
    private long _filesMatched;

    [ObservableProperty]
    private long _filesMismatched;

    [ObservableProperty]
    private long _filesSkipped;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private long _bytesVerified;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private double _mbPerSecond;

    [ObservableProperty]
    private string? _currentSourceFile;

    [ObservableProperty]
    private string? _currentDestFile;

    [ObservableProperty]
    private long _currentFileSize;

    [ObservableProperty]
    private bool _isVerifying;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private string _statusMessage = "Ready to verify copied files";

    [ObservableProperty]
    private string? _estimatedTimeRemaining;

    [ObservableProperty]
    private string _phaseDescription = "Not started";

    [ObservableProperty]
    private ObservableCollection<MismatchItem> _mismatches = new();

    [ObservableProperty]
    private TimeSpan _elapsedTime;

    [ObservableProperty]
    private bool _verificationSuccessful;

    public VerificationViewModel(
        ILogger<VerificationViewModel> logger,
        AppStateManager stateManager,
        IProjectService projectService,
        IVerificationService verificationService,
        NavigationService navigationService)
    {
        _logger = logger;
        _stateManager = stateManager;
        _projectService = projectService;
        _verificationService = verificationService;
        _navigationService = navigationService;

        // Subscribe to verification service events
        _verificationService.ProgressChanged += OnProgressChanged;
        _verificationService.VerificationCompleted += OnVerificationCompleted;
        _verificationService.MismatchDetected += OnMismatchDetected;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();
        RefreshFromCurrentProgress();
    }

    private void RefreshFromCurrentProgress()
    {
        var progress = _verificationService.CurrentProgress;
        UpdateProgressDisplay(progress);
    }

    [RelayCommand(CanExecute = nameof(CanStartVerification))]
    private async Task StartVerificationAsync()
    {
        _logger.LogInformation("Starting verification...");

        IsVerifying = true;
        IsPaused = false;
        IsCompleted = false;
        VerificationSuccessful = false;
        StatusMessage = "Preparing verification...";
        PhaseDescription = "Preparing";
        Mismatches.Clear();

        _verifyCts = new CancellationTokenSource();

        try
        {
            await _verificationService.StartVerificationAsync(_verifyCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Verification cancelled";
            _logger.LogInformation("Verification cancelled by user");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Verification failed: {ex.Message}";
            _logger.LogError(ex, "Verification failed");
        }
        finally
        {
            IsVerifying = false;
            NotifyCommandsCanExecuteChanged();
        }
    }

    private bool CanStartVerification() =>
        _projectService.IsProjectOpen && !IsVerifying;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void PauseVerification()
    {
        _logger.LogInformation("Pausing verification...");
        _verificationService.Pause();
        IsPaused = true;
        StatusMessage = "Paused";
        NotifyCommandsCanExecuteChanged();
    }

    private bool CanPause() => IsVerifying && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void ResumeVerification()
    {
        _logger.LogInformation("Resuming verification...");
        _verificationService.Resume();
        IsPaused = false;
        StatusMessage = "Verifying...";
        NotifyCommandsCanExecuteChanged();
    }

    private bool CanResume() => IsVerifying && IsPaused;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelVerification()
    {
        _logger.LogInformation("Cancelling verification...");
        _verifyCts?.Cancel();
        StatusMessage = "Cancelling...";
    }

    private bool CanCancel() => IsVerifying;

    [RelayCommand]
    private async Task NavigateToCopyAsync()
    {
        await _navigationService.NavigateToAsync("Copy");
    }

    [RelayCommand]
    private async Task NavigateToDuplicatesAsync()
    {
        await _navigationService.NavigateToAsync("Duplicates");
    }

    private void OnProgressChanged(object? sender, VerificationProgress progress)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateProgressDisplay(progress);
        });
    }

    private void UpdateProgressDisplay(VerificationProgress progress)
    {
        TotalFiles = progress.TotalFiles;
        FilesVerified = progress.FilesVerified;
        FilesMatched = progress.FilesMatched;
        FilesMismatched = progress.FilesMismatched;
        FilesSkipped = progress.FilesSkipped;
        ErrorCount = progress.ErrorCount;
        BytesVerified = progress.BytesVerified;
        TotalBytes = progress.TotalBytes;
        Progress = progress.PercentComplete;
        MbPerSecond = progress.MBPerSecond;
        CurrentSourceFile = progress.CurrentSourceFile;
        CurrentDestFile = progress.CurrentDestFile;
        CurrentFileSize = progress.CurrentFileSize;
        IsPaused = progress.IsPaused;
        ElapsedTime = progress.Elapsed;

        PhaseDescription = progress.Phase switch
        {
            VerificationPhase.NotStarted => "Not started",
            VerificationPhase.Preparing => "Preparing...",
            VerificationPhase.LoadingFileList => "Loading file list...",
            VerificationPhase.Verifying => "Verifying files...",
            VerificationPhase.Completed => "Completed",
            VerificationPhase.Cancelled => "Cancelled",
            VerificationPhase.Failed => "Failed",
            _ => "Unknown"
        };

        EstimatedTimeRemaining = progress.EstimatedTimeRemainingText;

        if (IsPaused)
        {
            StatusMessage = "Paused";
        }
        else if (progress.Phase == VerificationPhase.Verifying)
        {
            StatusMessage = $"Verifying... {progress.FilesVerified:N0} / {progress.TotalFiles:N0} files";
        }
    }

    private void OnVerificationCompleted(object? sender, VerificationCompletedEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            IsVerifying = false;
            IsCompleted = true;
            VerificationSuccessful = e.Success;
            ElapsedTime = e.Duration;

            if (e.Success)
            {
                if (e.FilesMismatched == 0 && e.ErrorCount == 0)
                {
                    StatusMessage = $"Verification complete! All {e.FilesMatched:N0} files verified successfully.";
                }
                else
                {
                    StatusMessage = $"Verification complete. {e.FilesMatched:N0} matched, {e.FilesMismatched} mismatched, {e.ErrorCount} errors.";
                }
            }
            else
            {
                StatusMessage = e.ErrorMessage ?? "Verification failed";
            }

            // Add any mismatches from completion event if not already tracked
            foreach (var mismatch in e.Mismatches)
            {
                var existing = Mismatches.Any(m =>
                    m.SourcePath == mismatch.SourcePath &&
                    m.DestPath == mismatch.DestPath);

                if (!existing)
                {
                    Mismatches.Add(new MismatchItem
                    {
                        SourcePath = mismatch.SourcePath,
                        DestPath = mismatch.DestPath,
                        SourceHash = mismatch.SourceHash,
                        DestHash = mismatch.DestHash,
                        FileSize = mismatch.FileSize,
                        Reason = mismatch.Reason.ToString(),
                        ErrorMessage = mismatch.ErrorMessage,
                        WasRenamed = mismatch.WasRenamed,
                        OriginalPlannedPath = mismatch.OriginalPlannedPath
                    });
                }
            }

            NotifyCommandsCanExecuteChanged();
        });
    }

    private void OnMismatchDetected(object? sender, VerificationMismatchEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Mismatches.Add(new MismatchItem
            {
                SourcePath = e.SourcePath,
                DestPath = e.DestPath,
                SourceHash = e.SourceHash,
                DestHash = e.DestHash,
                FileSize = e.FileSize,
                Reason = e.Reason.ToString(),
                WasRenamed = e.WasRenamed,
                OriginalPlannedPath = e.OriginalPlannedPath
            });

            _logger.LogWarning("Mismatch detected: {Reason} - {DestPath}{Renamed}",
                e.Reason, e.DestPath, e.WasRenamed ? " (renamed)" : "");
        });
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        StartVerificationCommand.NotifyCanExecuteChanged();
        PauseVerificationCommand.NotifyCanExecuteChanged();
        ResumeVerificationCommand.NotifyCanExecuteChanged();
        CancelVerificationCommand.NotifyCanExecuteChanged();
    }
}

public class MismatchItem
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestPath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string DestHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool WasRenamed { get; set; }
    public string OriginalPlannedPath { get; set; } = string.Empty;

    public string FileName => Path.GetFileName(DestPath);
    public string SourceFileName => Path.GetFileName(SourcePath);
    public string FileSizeFormatted => FormatFileSize(FileSize);

    /// <summary>
    /// Display text showing if the file was renamed
    /// </summary>
    public string RenameInfo => WasRenamed
        ? $"Renamed (original: {Path.GetFileName(OriginalPlannedPath)})"
        : string.Empty;

    /// <summary>
    /// Opens the source file's containing folder in Windows Explorer
    /// </summary>
    public void OpenSourceFolder()
    {
        var folder = Path.GetDirectoryName(SourcePath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{SourcePath}\"");
        }
    }

    /// <summary>
    /// Opens the destination file's containing folder in Windows Explorer
    /// </summary>
    public void OpenDestFolder()
    {
        var folder = Path.GetDirectoryName(DestPath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{DestPath}\"");
        }
    }

    /// <summary>
    /// Opens the source file in the default Windows viewer
    /// </summary>
    public void OpenSourceFile()
    {
        if (File.Exists(SourcePath))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = SourcePath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
    }

    /// <summary>
    /// Opens the destination file in the default Windows viewer
    /// </summary>
    public void OpenDestFile()
    {
        if (File.Exists(DestPath))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = DestPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
