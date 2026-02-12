namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for copying unique files to the destination folder.
/// Uses safe copy pattern (temp file + rename) with optional verification.
/// </summary>
public interface ICopyService
{
    /// <summary>
    /// Starts copying all enabled unique files to the target destination.
    /// </summary>
    /// <param name="targetPath">Base destination folder path</param>
    /// <param name="verifyAfterCopy">Whether to verify each copy by re-hashing</param>
    /// <param name="cancellationToken">Cancellation token for stopping the operation</param>
    Task StartCopyAsync(string targetPath, bool verifyAfterCopy = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the current copy operation.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes a paused copy operation.
    /// </summary>
    void Resume();

    /// <summary>
    /// Retries all failed copy jobs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RetryFailedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Current copy progress information.
    /// </summary>
    CopyProgress CurrentProgress { get; }

    /// <summary>
    /// Event raised when copy progress is updated.
    /// </summary>
    event EventHandler<CopyProgress>? ProgressChanged;

    /// <summary>
    /// Event raised when copying is complete.
    /// </summary>
    event EventHandler<CopyCompletedEventArgs>? CopyCompleted;

    /// <summary>
    /// Event raised when a single file copy fails.
    /// </summary>
    event EventHandler<CopyJobFailedEventArgs>? JobFailed;
}

/// <summary>
/// Progress information for a copy operation.
/// </summary>
public class CopyProgress
{
    public long TotalFiles { get; set; }
    public long FilesCopied { get; set; }
    public long FilesVerified { get; set; }
    public long FilesSkipped { get; set; }
    public int FailedCount { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public long CurrentFileSize { get; set; }
    public long BytesCopied { get; set; }
    public long TotalBytes { get; set; }
    public bool IsPaused { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Elapsed => DateTime.Now - StartTime;
    public double MBPerSecond => Elapsed.TotalSeconds > 0 ? (BytesCopied / 1024.0 / 1024.0) / Elapsed.TotalSeconds : 0;
    public double PercentComplete => TotalBytes > 0 ? (BytesCopied * 100.0 / TotalBytes) : 0;

    /// <summary>
    /// Estimated time remaining based on current speed.
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (MBPerSecond <= 0 || BytesCopied == 0) return null;
            var remainingBytes = TotalBytes - BytesCopied;
            var remainingSeconds = remainingBytes / (MBPerSecond * 1024 * 1024);
            return TimeSpan.FromSeconds(remainingSeconds);
        }
    }
}

/// <summary>
/// Event arguments for copy completion.
/// </summary>
public class CopyCompletedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public long TotalFilesCopied { get; set; }
    public long FilesVerified { get; set; }
    public long FilesSkipped { get; set; }
    public int FailedCount { get; set; }
    public long BytesCopied { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Detailed completion statistics including extension breakdown.
    /// Only populated on successful completion.
    /// </summary>
    public Data.Repositories.CopyCompletionStats? DetailedStats { get; set; }
}

/// <summary>
/// Event arguments for a failed copy job.
/// </summary>
public class CopyJobFailedEventArgs : EventArgs
{
    public long CopyJobId { get; set; }
    public long UniqueFileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
}
