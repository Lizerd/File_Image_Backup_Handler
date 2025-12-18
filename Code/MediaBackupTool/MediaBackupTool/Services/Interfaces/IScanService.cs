namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for scanning directories and discovering media files.
/// Uses streaming enumeration to handle millions of files without loading all into RAM.
/// </summary>
public interface IScanService
{
    /// <summary>
    /// Starts scanning all configured scan roots.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for stopping the scan</param>
    Task StartScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the current scan operation.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes a paused scan operation.
    /// </summary>
    void Resume();

    /// <summary>
    /// Current scan progress information.
    /// </summary>
    ScanProgress CurrentProgress { get; }

    /// <summary>
    /// Event raised when scan progress is updated.
    /// </summary>
    event EventHandler<ScanProgress>? ProgressChanged;

    /// <summary>
    /// Event raised when scanning is complete.
    /// </summary>
    event EventHandler<ScanCompletedEventArgs>? ScanCompleted;
}

/// <summary>
/// Progress information for a scan operation.
/// </summary>
public class ScanProgress
{
    public int TotalFilesFound { get; set; }
    public int FilesProcessed { get; set; }
    public int DirectoriesScanned { get; set; }
    public int ErrorCount { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public string CurrentRoot { get; set; } = string.Empty;
    public long TotalBytesFound { get; set; }
    public bool IsPaused { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Elapsed => DateTime.Now - StartTime;
    public double FilesPerSecond => Elapsed.TotalSeconds > 0 ? FilesProcessed / Elapsed.TotalSeconds : 0;
}

/// <summary>
/// Event arguments for scan completion.
/// </summary>
public class ScanCompletedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public int TotalFilesFound { get; set; }
    public int ErrorCount { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
}
