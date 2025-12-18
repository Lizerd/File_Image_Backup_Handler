using CommunityToolkit.Mvvm.ComponentModel;
using MediaBackupTool.Models.Enums;
using Microsoft.Extensions.Logging;

namespace MediaBackupTool.Infrastructure.State;

/// <summary>
/// Manages application state transitions and persistence.
/// Ensures valid state transitions and notifies subscribers.
/// </summary>
public partial class AppStateManager : ObservableObject
{
    private readonly ILogger<AppStateManager> _logger;

    [ObservableProperty]
    private AppState _currentState = AppState.Idle;

    [ObservableProperty]
    private string? _currentOperation;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _statusMessage;

    public event EventHandler<AppStateChangedEventArgs>? StateChanged;

    public AppStateManager(ILogger<AppStateManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to transition to a new state.
    /// Returns true if transition was valid and successful.
    /// </summary>
    public bool TryTransitionTo(AppState newState, string? reason = null)
    {
        if (!IsValidTransition(CurrentState, newState))
        {
            _logger.LogWarning("Invalid state transition from {From} to {To}", CurrentState, newState);
            return false;
        }

        var oldState = CurrentState;
        CurrentState = newState;

        _logger.LogInformation("State transition: {From} -> {To}. Reason: {Reason}",
            oldState, newState, reason ?? "N/A");

        StateChanged?.Invoke(this, new AppStateChangedEventArgs(oldState, newState, reason));
        return true;
    }

    /// <summary>
    /// Forces a state transition (for recovery scenarios).
    /// </summary>
    public void ForceState(AppState newState, string reason)
    {
        var oldState = CurrentState;
        CurrentState = newState;

        _logger.LogWarning("Forced state transition: {From} -> {To}. Reason: {Reason}",
            oldState, newState, reason);

        StateChanged?.Invoke(this, new AppStateChangedEventArgs(oldState, newState, reason));
    }

    /// <summary>
    /// Checks if a transition is valid based on the state machine rules.
    /// </summary>
    public static bool IsValidTransition(AppState from, AppState to)
    {
        // Same state is always valid (no-op)
        if (from == to) return true;

        return (from, to) switch
        {
            // From Idle
            (AppState.Idle, AppState.Scanning) => true,
            (AppState.Idle, AppState.Hashing) => true, // Resume hashing
            (AppState.Idle, AppState.Planning) => true,
            (AppState.Idle, AppState.ReadyToCopy) => true,
            (AppState.Idle, AppState.Copying) => true, // Resume copying

            // From Scanning
            (AppState.Scanning, AppState.ScanPaused) => true,
            (AppState.Scanning, AppState.Hashing) => true,
            (AppState.Scanning, AppState.Idle) => true, // Cancel
            (AppState.Scanning, AppState.Faulted) => true,

            // From ScanPaused
            (AppState.ScanPaused, AppState.Scanning) => true, // Resume
            (AppState.ScanPaused, AppState.Idle) => true, // Cancel

            // From Hashing
            (AppState.Hashing, AppState.HashPaused) => true,
            (AppState.Hashing, AppState.Planning) => true,
            (AppState.Hashing, AppState.Idle) => true, // Cancel
            (AppState.Hashing, AppState.Faulted) => true,

            // From HashPaused
            (AppState.HashPaused, AppState.Hashing) => true, // Resume
            (AppState.HashPaused, AppState.Idle) => true, // Cancel

            // From Planning
            (AppState.Planning, AppState.ReadyToCopy) => true,
            (AppState.Planning, AppState.Idle) => true,

            // From ReadyToCopy
            (AppState.ReadyToCopy, AppState.Copying) => true,
            (AppState.ReadyToCopy, AppState.Planning) => true, // Go back
            (AppState.ReadyToCopy, AppState.Idle) => true,

            // From Copying
            (AppState.Copying, AppState.CopyPaused) => true,
            (AppState.Copying, AppState.Completed) => true,
            (AppState.Copying, AppState.Idle) => true, // Cancel
            (AppState.Copying, AppState.Faulted) => true,

            // From CopyPaused
            (AppState.CopyPaused, AppState.Copying) => true, // Resume
            (AppState.CopyPaused, AppState.Idle) => true, // Cancel

            // From Completed
            (AppState.Completed, AppState.Idle) => true, // Start over

            // From Faulted
            (AppState.Faulted, AppState.Idle) => true, // Recovery

            _ => false
        };
    }

    /// <summary>
    /// Gets whether the current state allows starting a scan.
    /// </summary>
    public bool CanStartScan => CurrentState == AppState.Idle;

    /// <summary>
    /// Gets whether the current state allows starting hashing.
    /// </summary>
    public bool CanStartHash => CurrentState == AppState.Idle ||
                                CurrentState == AppState.Scanning;

    /// <summary>
    /// Gets whether the current state allows copying.
    /// </summary>
    public bool CanStartCopy => CurrentState == AppState.ReadyToCopy;

    /// <summary>
    /// Gets whether an operation is currently active (not paused or idle).
    /// </summary>
    public bool IsOperationActive => CurrentState switch
    {
        AppState.Scanning => true,
        AppState.Hashing => true,
        AppState.Copying => true,
        _ => false
    };

    /// <summary>
    /// Gets whether the current state is paused.
    /// </summary>
    public bool IsPaused => CurrentState switch
    {
        AppState.ScanPaused => true,
        AppState.HashPaused => true,
        AppState.CopyPaused => true,
        _ => false
    };

    /// <summary>
    /// Updates progress information.
    /// </summary>
    public void UpdateProgress(double progress, string? message = null)
    {
        Progress = Math.Clamp(progress, 0, 100);
        if (message != null)
            StatusMessage = message;
    }
}

public class AppStateChangedEventArgs : EventArgs
{
    public AppState OldState { get; }
    public AppState NewState { get; }
    public string? Reason { get; }

    public AppStateChangedEventArgs(AppState oldState, AppState newState, string? reason = null)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
    }
}
