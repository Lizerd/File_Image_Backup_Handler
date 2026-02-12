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
/// ViewModel for the Hash page - hashing progress and control.
/// </summary>
public partial class HashViewModel : ViewModelBase
{
    private readonly ILogger<HashViewModel> _logger;
    private readonly AppStateManager _stateManager;
    private readonly IHashService _hashService;
    private readonly NavigationService _navigationService;

    private CancellationTokenSource? _hashCts;

    public AppStateManager State => _stateManager;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _filesHashed;

    [ObservableProperty]
    private int _filesSkipped;

    [ObservableProperty]
    private long _totalBytesHashed;

    [ObservableProperty]
    private long _totalBytesToHash;

    [ObservableProperty]
    private string _currentFile = string.Empty;

    [ObservableProperty]
    private double _mbPerSecond;

    [ObservableProperty]
    private double _percentComplete;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _workerCount;

    [ObservableProperty]
    private bool _isHashing;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusMessage = "Ready to hash";

    [ObservableProperty]
    private TimeSpan _elapsedTime;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isHashComplete;

    [ObservableProperty]
    private string _completionMessage = string.Empty;

    [ObservableProperty]
    private int _uniqueHashesFound;

    [ObservableProperty]
    private int _duplicatesFound;

    public string TotalBytesDisplay => FormatBytes(TotalBytesHashed);
    public string TotalToHashDisplay => FormatBytes(TotalBytesToHash);

    public HashViewModel(
        ILogger<HashViewModel> logger,
        AppStateManager stateManager,
        IHashService hashService,
        NavigationService navigationService)
    {
        _logger = logger;
        _stateManager = stateManager;
        _hashService = hashService;
        _navigationService = navigationService;

        // Subscribe to hash service events
        _hashService.ProgressChanged += OnHashProgressChanged;
        _hashService.HashCompleted += OnHashCompleted;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();

        // Auto-start hashing when navigating to this page
        if (!IsHashing && _stateManager.CurrentState == AppState.Hashing)
        {
            _ = StartHashAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartHash))]
    private async Task StartHashAsync()
    {
        _logger.LogInformation("Starting hash...");
        ResetProgress();
        ErrorMessage = null;

        _hashCts = new CancellationTokenSource();
        IsHashing = true;
        StatusMessage = "Hashing...";

        try
        {
            await _hashService.StartHashingAsync(_hashCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Hash was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hash failed");
            ErrorMessage = $"Hash failed: {ex.Message}";
            StatusMessage = "Hash failed";
        }
    }

    private bool CanStartHash() => !IsHashing;

    [RelayCommand(CanExecute = nameof(CanPauseHash))]
    private void PauseHash()
    {
        _logger.LogInformation("Pausing hash...");
        _hashService.Pause();
        IsPaused = true;
        StatusMessage = "Hash paused";
        _stateManager.TryTransitionTo(AppState.HashPaused, "User paused hash");
    }

    private bool CanPauseHash() => IsHashing && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanResumeHash))]
    private void ResumeHash()
    {
        _logger.LogInformation("Resuming hash...");
        _hashService.Resume();
        IsPaused = false;
        StatusMessage = "Hashing...";
        _stateManager.TryTransitionTo(AppState.Hashing, "User resumed hash");
    }

    private bool CanResumeHash() => IsHashing && IsPaused;

    [RelayCommand(CanExecute = nameof(CanCancelHash))]
    private void CancelHash()
    {
        _logger.LogInformation("Cancelling hash...");
        _hashCts?.Cancel();
        IsHashing = false;
        IsPaused = false;
        StatusMessage = "Hash cancelled";
        _stateManager.TryTransitionTo(AppState.Idle, "User cancelled hash");
    }

    private bool CanCancelHash() => IsHashing;

    [RelayCommand(CanExecute = nameof(CanNavigateToNext))]
    private async Task NavigateToNextAsync()
    {
        await _navigationService.NavigateToAsync("Plan");
    }

    private bool CanNavigateToNext() => IsHashComplete && !IsHashing;

    [RelayCommand]
    private async Task NavigateBackAsync()
    {
        await _navigationService.NavigateToAsync("Scan");
    }

    private void OnHashProgressChanged(object? sender, HashProgress progress)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            TotalFiles = progress.TotalFiles;
            FilesHashed = progress.FilesHashed;
            FilesSkipped = progress.FilesSkipped;
            TotalBytesHashed = progress.TotalBytesHashed;
            TotalBytesToHash = progress.TotalBytesToHash;
            CurrentFile = TruncatePath(progress.CurrentFile, 80);
            MbPerSecond = progress.MBPerSecond;
            PercentComplete = progress.PercentComplete;
            ErrorCount = progress.ErrorCount;
            WorkerCount = progress.WorkerCount;
            ElapsedTime = progress.Elapsed;
            IsPaused = progress.IsPaused;

            OnPropertyChanged(nameof(TotalBytesDisplay));
            OnPropertyChanged(nameof(TotalToHashDisplay));
        });
    }

    private void OnHashCompleted(object? sender, HashCompletedEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsHashing = false;
            IsPaused = false;

            if (e.Success)
            {
                IsHashComplete = true;
                UniqueHashesFound = e.UniqueHashesFound;
                DuplicatesFound = e.DuplicatesFound;
                StatusMessage = $"Hash complete: {e.TotalFilesHashed:N0} files";
                CompletionMessage = $"Hashed {e.TotalFilesHashed:N0} files in {e.Duration:hh\\:mm\\:ss}. " +
                    $"Found {e.UniqueHashesFound:N0} unique files and {e.DuplicatesFound:N0} duplicates. " +
                    $"Next step: Generate copy plan.";
                _logger.LogInformation("Hash completed successfully: {Files} files, {Unique} unique, {Duplicates} duplicates in {Duration}",
                    e.TotalFilesHashed, e.UniqueHashesFound, e.DuplicatesFound, e.Duration);

                _stateManager.TryTransitionTo(AppState.Planning, "Hash completed");
            }
            else
            {
                IsHashComplete = false;
                StatusMessage = e.ErrorMessage ?? "Hash failed";
                ErrorMessage = e.ErrorMessage;
                _stateManager.TryTransitionTo(AppState.Idle, "Hash failed or cancelled");
            }

            // Refresh command states
            StartHashCommand.NotifyCanExecuteChanged();
            PauseHashCommand.NotifyCanExecuteChanged();
            ResumeHashCommand.NotifyCanExecuteChanged();
            CancelHashCommand.NotifyCanExecuteChanged();
            NavigateToNextCommand.NotifyCanExecuteChanged();
        });
    }

    private void ResetProgress()
    {
        TotalFiles = 0;
        FilesHashed = 0;
        FilesSkipped = 0;
        TotalBytesHashed = 0;
        TotalBytesToHash = 0;
        CurrentFile = string.Empty;
        MbPerSecond = 0;
        PercentComplete = 0;
        ErrorCount = 0;
        WorkerCount = 0;
        ElapsedTime = TimeSpan.Zero;
        IsHashComplete = false;
        CompletionMessage = string.Empty;
        UniqueHashesFound = 0;
        DuplicatesFound = 0;
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path;

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
