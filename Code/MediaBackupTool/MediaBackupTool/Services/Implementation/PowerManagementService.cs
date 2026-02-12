using System.Runtime.InteropServices;
using MediaBackupTool.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Service for managing system power settings to prevent sleep during long operations.
/// Uses Windows SetThreadExecutionState API to prevent the system from sleeping.
/// </summary>
public class PowerManagementService : IPowerManagementService, IDisposable
{
    private readonly ILogger<PowerManagementService> _logger;
    private readonly object _lock = new();
    private readonly HashSet<string> _activeOperations = new();
    private bool _preventSleepEnabled = true; // Enabled by default
    private bool _isPreventingSleep;

    public PowerManagementService(ILogger<PowerManagementService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool PreventSleepEnabled
    {
        get => _preventSleepEnabled;
        set
        {
            if (_preventSleepEnabled != value)
            {
                _preventSleepEnabled = value;
                _logger.LogInformation("Prevent sleep setting changed to: {Enabled}", value);

                // If we're disabling and currently preventing sleep, allow sleep again
                if (!value && _isPreventingSleep)
                {
                    AllowSleep();
                }
                // If we're enabling and have active operations, prevent sleep
                else if (value && _activeOperations.Count > 0)
                {
                    PreventSleep();
                }
            }
        }
    }

    /// <inheritdoc />
    public bool IsPreventingSleep => _isPreventingSleep;

    /// <inheritdoc />
    public int ActiveOperationCount
    {
        get
        {
            lock (_lock)
            {
                return _activeOperations.Count;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<bool>? PreventSleepStateChanged;

    /// <inheritdoc />
    public void BeginOperation(string operationName)
    {
        lock (_lock)
        {
            _activeOperations.Add(operationName);
            _logger.LogDebug("Operation started: {Operation}. Active operations: {Count}",
                operationName, _activeOperations.Count);

            // Start preventing sleep if this is the first operation and feature is enabled
            if (_preventSleepEnabled && _activeOperations.Count == 1)
            {
                PreventSleep();
            }
        }
    }

    /// <inheritdoc />
    public void EndOperation(string operationName)
    {
        lock (_lock)
        {
            _activeOperations.Remove(operationName);
            _logger.LogDebug("Operation ended: {Operation}. Active operations: {Count}",
                operationName, _activeOperations.Count);

            // Allow sleep if no more operations are active
            if (_activeOperations.Count == 0 && _isPreventingSleep)
            {
                AllowSleep();
            }
        }
    }

    /// <summary>
    /// Prevents the system from sleeping by calling SetThreadExecutionState.
    /// </summary>
    private void PreventSleep()
    {
        if (_isPreventingSleep)
            return;

        try
        {
            // ES_CONTINUOUS | ES_SYSTEM_REQUIRED
            // This keeps the system awake but allows the display to turn off
            var result = NativeMethods.SetThreadExecutionState(
                ExecutionState.ES_CONTINUOUS | ExecutionState.ES_SYSTEM_REQUIRED);

            if (result == ExecutionState.None)
            {
                _logger.LogWarning("SetThreadExecutionState failed to prevent sleep");
            }
            else
            {
                _isPreventingSleep = true;
                _logger.LogInformation("System sleep prevention activated");
                PreventSleepStateChanged?.Invoke(this, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prevent system sleep");
        }
    }

    /// <summary>
    /// Allows the system to sleep again by clearing the execution state.
    /// </summary>
    private void AllowSleep()
    {
        if (!_isPreventingSleep)
            return;

        try
        {
            // ES_CONTINUOUS alone clears the previous flags
            var result = NativeMethods.SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);

            _isPreventingSleep = false;
            _logger.LogInformation("System sleep prevention deactivated");
            PreventSleepStateChanged?.Invoke(this, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore system sleep");
        }
    }

    /// <summary>
    /// Disposes the service and ensures system can sleep again.
    /// </summary>
    public void Dispose()
    {
        if (_isPreventingSleep)
        {
            AllowSleep();
        }
    }

    /// <summary>
    /// Execution state flags for SetThreadExecutionState.
    /// </summary>
    [Flags]
    private enum ExecutionState : uint
    {
        None = 0,
        /// <summary>
        /// Forces the system to be in the working state by resetting the system idle timer.
        /// </summary>
        ES_SYSTEM_REQUIRED = 0x00000001,
        /// <summary>
        /// Forces the display to be on by resetting the display idle timer.
        /// </summary>
        ES_DISPLAY_REQUIRED = 0x00000002,
        /// <summary>
        /// Enables away mode. This value must be specified with ES_CONTINUOUS.
        /// Away mode should be used only by media-recording and media-distribution applications.
        /// </summary>
        ES_AWAYMODE_REQUIRED = 0x00000040,
        /// <summary>
        /// Informs the system that the state being set should remain in effect until the next call
        /// that uses ES_CONTINUOUS and one of the other state flags is cleared.
        /// </summary>
        ES_CONTINUOUS = 0x80000000
    }

    /// <summary>
    /// Native Windows API methods.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// Enables an application to inform the system that it is in use,
        /// thereby preventing the system from entering sleep or turning off the display.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);
    }
}
