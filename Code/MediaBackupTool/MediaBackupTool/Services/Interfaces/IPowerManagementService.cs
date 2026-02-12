namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for managing system power settings to prevent sleep during long operations.
/// </summary>
public interface IPowerManagementService
{
    /// <summary>
    /// Gets or sets whether the prevent sleep feature is enabled.
    /// When enabled, the system will not go to sleep while work is in progress.
    /// </summary>
    bool PreventSleepEnabled { get; set; }

    /// <summary>
    /// Gets whether the system is currently being kept awake.
    /// </summary>
    bool IsPreventingSleep { get; }

    /// <summary>
    /// Gets the number of active operations currently preventing sleep.
    /// </summary>
    int ActiveOperationCount { get; }

    /// <summary>
    /// Begins an operation that should prevent sleep.
    /// Call EndOperation when the operation completes.
    /// </summary>
    /// <param name="operationName">A name for the operation (for logging)</param>
    void BeginOperation(string operationName);

    /// <summary>
    /// Ends an operation that was preventing sleep.
    /// </summary>
    /// <param name="operationName">The name used when BeginOperation was called</param>
    void EndOperation(string operationName);

    /// <summary>
    /// Event raised when the prevent sleep state changes.
    /// </summary>
    event EventHandler<bool>? PreventSleepStateChanged;
}
