namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for computing file hashes for duplicate detection.
/// Uses parallel workers for efficient processing of large file sets.
/// </summary>
public interface IHashService
{
    /// <summary>
    /// Starts hashing all discovered files that haven't been hashed yet.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for stopping the operation</param>
    Task StartHashingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the current hashing operation.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes a paused hashing operation.
    /// </summary>
    void Resume();

    /// <summary>
    /// Current hash progress information.
    /// </summary>
    HashProgress CurrentProgress { get; }

    /// <summary>
    /// Event raised when hash progress is updated.
    /// </summary>
    event EventHandler<HashProgress>? ProgressChanged;

    /// <summary>
    /// Event raised when hashing is complete.
    /// </summary>
    event EventHandler<HashCompletedEventArgs>? HashCompleted;
}

/// <summary>
/// Progress information for a hash operation.
/// </summary>
public class HashProgress
{
    public int TotalFiles { get; set; }
    public int FilesHashed { get; set; }
    public int FilesSkipped { get; set; }
    public int ErrorCount { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public long CurrentFileSize { get; set; }
    public long TotalBytesHashed { get; set; }
    public long TotalBytesToHash { get; set; }
    public bool IsPaused { get; set; }
    public DateTime StartTime { get; set; }
    public int WorkerCount { get; set; }
    public TimeSpan Elapsed => DateTime.Now - StartTime;
    public double FilesPerSecond => Elapsed.TotalSeconds > 0 ? FilesHashed / Elapsed.TotalSeconds : 0;
    public double MBPerSecond => Elapsed.TotalSeconds > 0 ? (TotalBytesHashed / 1024.0 / 1024.0) / Elapsed.TotalSeconds : 0;
    public double PercentComplete => TotalBytesToHash > 0 ? (TotalBytesHashed * 100.0 / TotalBytesToHash) : 0;
}

/// <summary>
/// Event arguments for hash completion.
/// </summary>
public class HashCompletedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public int TotalFilesHashed { get; set; }
    public int FilesSkipped { get; set; }
    public int ErrorCount { get; set; }
    public int UniqueHashesFound { get; set; }
    public int DuplicatesFound { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
}
