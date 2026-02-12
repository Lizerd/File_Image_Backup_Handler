using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Infrastructure.State;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Scan page - scanning progress and control.
/// </summary>
public partial class ScanViewModel : ViewModelBase
{
    private readonly ILogger<ScanViewModel> _logger;
    private readonly AppStateManager _stateManager;
    private readonly IScanService _scanService;
    private readonly NavigationService _navigationService;

    private CancellationTokenSource? _scanCts;

    public AppStateManager State => _stateManager;

    [ObservableProperty]
    private int _filesFound;

    [ObservableProperty]
    private int _directoriesScanned;

    [ObservableProperty]
    private long _totalBytesFound;

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _currentRoot = string.Empty;

    [ObservableProperty]
    private double _filesPerSecond;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusMessage = "Ready to scan";

    [ObservableProperty]
    private TimeSpan _elapsedTime;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isScanComplete;

    [ObservableProperty]
    private string _completionMessage = string.Empty;

    public string TotalBytesDisplay => FormatBytes(TotalBytesFound);

    public ScanViewModel(
        ILogger<ScanViewModel> logger,
        AppStateManager stateManager,
        IScanService scanService,
        NavigationService navigationService)
    {
        _logger = logger;
        _stateManager = stateManager;
        _scanService = scanService;
        _navigationService = navigationService;

        // Subscribe to scan service events
        _scanService.ProgressChanged += OnScanProgressChanged;
        _scanService.ScanCompleted += OnScanCompleted;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();

        // Auto-start scanning when navigating to this page (fire and forget)
        if (!IsScanning && _stateManager.CanStartScan)
        {
            // Don't await - let it run in background so UI stays responsive
            _ = StartScanAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        _logger.LogInformation("Starting scan...");
        ResetProgress();
        ErrorMessage = null;

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        StatusMessage = "Scanning...";

        if (!_stateManager.TryTransitionTo(AppState.Scanning, "User started scan"))
        {
            _logger.LogWarning("Failed to transition to Scanning state");
            IsScanning = false;
            return;
        }

        try
        {
            await _scanService.StartScanAsync(_scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scan was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            ErrorMessage = $"Scan failed: {ex.Message}";
            StatusMessage = "Scan failed";
        }
    }

    private bool CanStartScan() => !IsScanning && _stateManager.CanStartScan;

    [RelayCommand(CanExecute = nameof(CanPauseScan))]
    private void PauseScan()
    {
        _logger.LogInformation("Pausing scan...");
        _scanService.Pause();
        IsPaused = true;
        StatusMessage = "Scan paused";
        _stateManager.TryTransitionTo(AppState.ScanPaused, "User paused scan");
    }

    private bool CanPauseScan() => IsScanning && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanResumeScan))]
    private void ResumeScan()
    {
        _logger.LogInformation("Resuming scan...");
        _scanService.Resume();
        IsPaused = false;
        StatusMessage = "Scanning...";
        _stateManager.TryTransitionTo(AppState.Scanning, "User resumed scan");
    }

    private bool CanResumeScan() => IsScanning && IsPaused;

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        _logger.LogInformation("Cancelling scan...");
        _scanCts?.Cancel();
        IsScanning = false;
        IsPaused = false;
        StatusMessage = "Scan cancelled";
        _stateManager.TryTransitionTo(AppState.Idle, "User cancelled scan");
    }

    private bool CanCancelScan() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanNavigateToNext))]
    private async Task NavigateToNextAsync()
    {
        // Go to Hash page for the next step
        await _navigationService.NavigateToAsync("Hash");
    }

    private bool CanNavigateToNext() => IsScanComplete && !IsScanning;

    [RelayCommand]
    private async Task NavigateBackAsync()
    {
        await _navigationService.NavigateToAsync("Sources");
    }

    private void OnScanProgressChanged(object? sender, ScanProgress progress)
    {
        // Update on UI thread (non-blocking)
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            FilesFound = progress.TotalFilesFound;
            DirectoriesScanned = progress.DirectoriesScanned;
            TotalBytesFound = progress.TotalBytesFound;
            CurrentPath = TruncatePath(progress.CurrentPath, 80);
            CurrentRoot = progress.CurrentRoot;
            FilesPerSecond = progress.FilesPerSecond;
            ErrorCount = progress.ErrorCount;
            ElapsedTime = progress.Elapsed;
            IsPaused = progress.IsPaused;

            OnPropertyChanged(nameof(TotalBytesDisplay));
        });
    }

    private void OnScanCompleted(object? sender, ScanCompletedEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsScanning = false;
            IsPaused = false;

            if (e.Success)
            {
                IsScanComplete = true;
                StatusMessage = $"Scan complete: {e.TotalFilesFound:N0} files found";
                CompletionMessage = $"Found {e.TotalFilesFound:N0} media files in {e.Duration:hh\\:mm\\:ss}. " +
                    $"Click 'Next: Hash Files' to compute hashes and detect duplicates.";
                _logger.LogInformation("Scan completed successfully: {Files} files in {Duration}",
                    e.TotalFilesFound, e.Duration);

                // Stay in Hashing state (was transitioned when scan completed)
                _stateManager.TryTransitionTo(AppState.Hashing, "Scan completed");
            }
            else
            {
                IsScanComplete = false;
                StatusMessage = e.ErrorMessage ?? "Scan failed";
                ErrorMessage = e.ErrorMessage;
                _stateManager.TryTransitionTo(AppState.Idle, "Scan failed or cancelled");
            }

            // Refresh command states
            StartScanCommand.NotifyCanExecuteChanged();
            PauseScanCommand.NotifyCanExecuteChanged();
            ResumeScanCommand.NotifyCanExecuteChanged();
            CancelScanCommand.NotifyCanExecuteChanged();
            NavigateToNextCommand.NotifyCanExecuteChanged();
        });
    }

    private void ResetProgress()
    {
        FilesFound = 0;
        DirectoriesScanned = 0;
        TotalBytesFound = 0;
        CurrentPath = string.Empty;
        CurrentRoot = string.Empty;
        FilesPerSecond = 0;
        ErrorCount = 0;
        ElapsedTime = TimeSpan.Zero;
        IsScanComplete = false;
        CompletionMessage = string.Empty;
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path;

        // Keep start and end, replace middle with ...
        var keepLength = (maxLength - 3) / 2;
        return $"{path[..keepLength]}...{path[^keepLength..]}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
