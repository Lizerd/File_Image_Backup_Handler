namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for verifying copied files by direct comparison with source files.
/// This is an optional final validation step that doesn't rely on database hashes.
/// </summary>
public interface IVerificationService
{
    /// <summary>
    /// Starts verifying all copied files by comparing with their source files.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for stopping the operation</param>
    Task StartVerificationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the current verification operation.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes a paused verification operation.
    /// </summary>
    void Resume();

    /// <summary>
    /// Current verification progress information.
    /// </summary>
    VerificationProgress CurrentProgress { get; }

    /// <summary>
    /// Event raised when verification progress is updated.
    /// </summary>
    event EventHandler<VerificationProgress>? ProgressChanged;

    /// <summary>
    /// Event raised when verification is complete.
    /// </summary>
    event EventHandler<VerificationCompletedEventArgs>? VerificationCompleted;

    /// <summary>
    /// Event raised when a file mismatch is detected.
    /// </summary>
    event EventHandler<VerificationMismatchEventArgs>? MismatchDetected;
}

/// <summary>
/// Progress information for a verification operation.
/// </summary>
public class VerificationProgress
{
    public long TotalFiles { get; set; }
    public long FilesVerified { get; set; }
    public long FilesMatched { get; set; }
    public long FilesMismatched { get; set; }
    public long FilesSkipped { get; set; }
    public int ErrorCount { get; set; }
    public string CurrentSourceFile { get; set; } = string.Empty;
    public string CurrentDestFile { get; set; } = string.Empty;
    public long CurrentFileSize { get; set; }
    public long BytesVerified { get; set; }
    public long TotalBytes { get; set; }
    public bool IsPaused { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Elapsed => DateTime.Now - StartTime;

    /// <summary>
    /// Verification speed in MB/s (reading both source and dest, so effective read speed is 2x).
    /// </summary>
    public double MBPerSecond => Elapsed.TotalSeconds > 0 ? (BytesVerified / 1024.0 / 1024.0) / Elapsed.TotalSeconds : 0;

    /// <summary>
    /// Percentage of bytes verified.
    /// </summary>
    public double PercentComplete => TotalBytes > 0 ? (BytesVerified * 100.0 / TotalBytes) : 0;

    /// <summary>
    /// Estimated time remaining based on current speed.
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (MBPerSecond <= 0 || BytesVerified == 0) return null;
            var remainingBytes = TotalBytes - BytesVerified;
            var remainingSeconds = remainingBytes / (MBPerSecond * 1024 * 1024);
            return TimeSpan.FromSeconds(remainingSeconds);
        }
    }

    /// <summary>
    /// Human-readable ETA string.
    /// </summary>
    public string EstimatedTimeRemainingText
    {
        get
        {
            var eta = EstimatedRemaining;
            if (!eta.HasValue) return "Calculating...";
            var remaining = eta.Value;
            if (remaining.TotalHours >= 1)
                return $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining";
            if (remaining.TotalMinutes >= 1)
                return $"{remaining.Minutes}m {remaining.Seconds}s remaining";
            return $"{remaining.Seconds}s remaining";
        }
    }

    /// <summary>
    /// Current phase of verification.
    /// </summary>
    public VerificationPhase Phase { get; set; } = VerificationPhase.NotStarted;
}

/// <summary>
/// Phases of the verification process.
/// </summary>
public enum VerificationPhase
{
    NotStarted,
    Preparing,
    LoadingFileList,
    Verifying,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Event arguments for verification completion.
/// </summary>
public class VerificationCompletedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public long TotalFilesVerified { get; set; }
    public long FilesMatched { get; set; }
    public long FilesMismatched { get; set; }
    public long FilesSkipped { get; set; }
    public int ErrorCount { get; set; }
    public long BytesVerified { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public IReadOnlyList<VerificationMismatch> Mismatches { get; set; } = Array.Empty<VerificationMismatch>();
}

/// <summary>
/// Event arguments for a detected mismatch.
/// </summary>
public class VerificationMismatchEventArgs : EventArgs
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestPath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string DestHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public MismatchReason Reason { get; set; }
    public bool WasRenamed { get; set; }
    public string OriginalPlannedPath { get; set; } = string.Empty;
}

/// <summary>
/// Information about a mismatched file.
/// </summary>
public class VerificationMismatch
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestPath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string DestHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public MismatchReason Reason { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// True if the destination filename was changed due to a conflict during copy.
    /// </summary>
    public bool WasRenamed { get; set; }

    /// <summary>
    /// The original planned destination path before conflict resolution.
    /// Only set if WasRenamed is true.
    /// </summary>
    public string OriginalPlannedPath { get; set; } = string.Empty;
}

/// <summary>
/// Reason for a verification mismatch.
/// </summary>
public enum MismatchReason
{
    HashMismatch,
    SourceMissing,
    DestMissing,
    SizeMismatch,
    ReadError
}
