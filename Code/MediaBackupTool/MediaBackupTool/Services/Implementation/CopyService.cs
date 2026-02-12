using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Data.Repositories;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Service for copying unique files to the destination folder.
/// Uses safe copy pattern (temp file + atomic rename) with optional verification.
/// </summary>
public class CopyService : ICopyService
{
    private readonly IProjectService _projectService;
    private readonly CopyJobRepository _copyJobRepo;
    private readonly IPowerManagementService _powerManagement;
    private readonly ILogger<CopyService> _logger;

    private CancellationTokenSource? _copyCts;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private CopyProgress _currentProgress = new();
    private readonly object _progressLock = new();
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private const int ProgressUpdateIntervalMs = 200;
    private const int MaxRetryAttempts = 3;
    private const int CopyBufferSize = 1024 * 1024; // 1 MB buffer for efficient copying

    private string _targetPath = string.Empty;
    private bool _verifyAfterCopy = true;

    public CopyProgress CurrentProgress
    {
        get
        {
            lock (_progressLock)
            {
                return _currentProgress;
            }
        }
    }

    public event EventHandler<CopyProgress>? ProgressChanged;
    public event EventHandler<CopyCompletedEventArgs>? CopyCompleted;
    public event EventHandler<CopyJobFailedEventArgs>? JobFailed;

    public CopyService(
        IProjectService projectService,
        CopyJobRepository copyJobRepo,
        IPowerManagementService powerManagement,
        ILogger<CopyService> logger)
    {
        _projectService = projectService;
        _copyJobRepo = copyJobRepo;
        _powerManagement = powerManagement;
        _logger = logger;
    }

    public async Task StartCopyAsync(string targetPath, bool verifyAfterCopy = true, CancellationToken cancellationToken = default)
    {
        if (!_projectService.IsProjectOpen)
        {
            throw new InvalidOperationException("No project is open");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path is required", nameof(targetPath));
        }

        _targetPath = targetPath;
        _verifyAfterCopy = verifyAfterCopy;
        _copyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _copyCts.Token;

        lock (_progressLock)
        {
            _currentProgress = new CopyProgress { StartTime = DateTime.Now };
        }

        // Prevent system sleep during copy
        _powerManagement.BeginOperation("Copy");

        await Task.Run(async () =>
        {
            try
            {
                // Create target directory if it doesn't exist
                Directory.CreateDirectory(targetPath);

                // Create copy jobs from plan
                var jobCount = await _copyJobRepo.CreateJobsFromPlanAsync(targetPath, token);
                if (jobCount == 0)
                {
                    _logger.LogWarning("No files to copy");
                    await OnCopyCompletedAsync(true, 0, 0, 0, 0, 0, TimeSpan.Zero);
                    return; // finally block will call EndOperation
                }

                // Get total bytes for progress tracking
                var (totalBytes, totalFiles) = await _copyJobRepo.GetTotalBytesAsync(token);
                lock (_progressLock)
                {
                    _currentProgress.TotalFiles = totalFiles;
                    _currentProgress.TotalBytes = totalBytes;
                }

                _logger.LogInformation("Starting copy of {Count} files ({Bytes:N0} bytes) to {Path}",
                    totalFiles, totalBytes, targetPath);

                // Determine worker count (use 2 workers for I/O bound work)
                var workerCount = Math.Min(2, Environment.ProcessorCount);
                _logger.LogInformation("Using {Workers} copy workers", workerCount);

                // Create bounded channel for job queue
                var channel = Channel.CreateBounded<CopyJobDetail>(new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true
                });

                // Start worker tasks
                var workerTasks = Enumerable.Range(0, workerCount)
                    .Select(_ => CopyWorkerAsync(channel.Reader, token))
                    .ToArray();

                // Feed jobs to channel
                await FeedJobsAsync(channel.Writer, token);

                // Wait for all workers to finish
                await Task.WhenAll(workerTasks);

                var progress = CurrentProgress;
                await OnCopyCompletedAsync(true, progress.FilesCopied, progress.FilesVerified,
                    progress.FilesSkipped, progress.FailedCount, progress.BytesCopied, progress.Elapsed);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Copy cancelled");

                // Reset any jobs that were claimed but not yet completed back to Pending
                try
                {
                    await _copyJobRepo.ResetInProgressJobsAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reset in-progress jobs after cancellation");
                }

                var progress = CurrentProgress;
                await OnCopyCompletedAsync(false, progress.FilesCopied, progress.FilesVerified,
                    progress.FilesSkipped, progress.FailedCount, progress.BytesCopied, progress.Elapsed, "Copy was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Copy failed with error");
                var progress = CurrentProgress;
                await OnCopyCompletedAsync(false, progress.FilesCopied, progress.FilesVerified,
                    progress.FilesSkipped, progress.FailedCount, progress.BytesCopied, progress.Elapsed, ex.Message);
            }
            finally
            {
                // Allow system sleep after copy completes
                _powerManagement.EndOperation("Copy");
            }
        }, token);
    }

    public void Pause()
    {
        _pauseEvent.Reset();
        lock (_progressLock)
        {
            _currentProgress.IsPaused = true;
        }
        RaiseProgressChanged();
        _logger.LogInformation("Copy paused");
    }

    public void Resume()
    {
        _pauseEvent.Set();
        lock (_progressLock)
        {
            _currentProgress.IsPaused = false;
        }
        RaiseProgressChanged();
        _logger.LogInformation("Copy resumed");
    }

    public async Task RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        if (!_projectService.IsProjectOpen)
        {
            throw new InvalidOperationException("No project is open");
        }

        _copyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _copyCts.Token;

        // Reset failed jobs to pending
        var resetCount = await _copyJobRepo.ResetFailedJobsAsync(token);
        if (resetCount == 0)
        {
            _logger.LogInformation("No failed jobs to retry");
            return;
        }

        _logger.LogInformation("Retrying {Count} failed jobs", resetCount);

        // Get the failed jobs and process them
        var failedJobs = await _copyJobRepo.GetPendingJobsAsync(1000, token);

        lock (_progressLock)
        {
            _currentProgress.FailedCount = 0; // Reset failed count for retry
        }

        await Task.Run(async () =>
        {
            try
            {
                var channel = Channel.CreateBounded<CopyJobDetail>(new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true
                });

                var workerTasks = Enumerable.Range(0, 2)
                    .Select(_ => CopyWorkerAsync(channel.Reader, token))
                    .ToArray();

                foreach (var job in failedJobs)
                {
                    token.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(token);
                    await channel.Writer.WriteAsync(job, token);
                }

                channel.Writer.Complete();
                await Task.WhenAll(workerTasks);

                _logger.LogInformation("Retry completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retry failed");
            }
        }, token);
    }

    private async Task FeedJobsAsync(ChannelWriter<CopyJobDetail> writer, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Check for pause before fetching more jobs
                _pauseEvent.Wait(token);
                token.ThrowIfCancellationRequested();

                // Use atomic claim to prevent race conditions - jobs are marked InProgress immediately
                var jobs = await _copyJobRepo.ClaimPendingJobsAsync(50, token);
                if (jobs.Count == 0)
                {
                    break;
                }

                // Note: Per-batch logging removed to reduce log volume at scale

                foreach (var job in jobs)
                {
                    token.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(token);
                    await writer.WriteAsync(job, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Feed cancelled");
            throw;
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task CopyWorkerAsync(ChannelReader<CopyJobDetail> reader, CancellationToken token)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(token))
            {
                if (token.IsCancellationRequested)
                    break;

                _pauseEvent.Wait(token);

                if (token.IsCancellationRequested)
                    break;

                await ProcessJobWithRetryAsync(job, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled, don't rethrow
            _logger.LogDebug("Copy worker cancelled");
        }
    }

    private async Task ProcessJobWithRetryAsync(CopyJobDetail job, CancellationToken token)
    {
        var attempt = 0;
        var success = false;

        while (attempt < MaxRetryAttempts && !success)
        {
            attempt++;

            try
            {
                // Job is already marked InProgress by ClaimPendingJobsAsync - no need to call MarkInProgressAsync

                lock (_progressLock)
                {
                    _currentProgress.CurrentFile = job.FileName;
                    _currentProgress.CurrentFileSize = job.SizeBytes;
                }

                // Check source file exists
                if (!File.Exists(job.SourceFullPath))
                {
                    _logger.LogWarning("Source file not found: {Path}", job.SourceFullPath);
                    await _copyJobRepo.MarkSkippedAsync(job.CopyJobId, "Source file not found", token);
                    IncrementSkipped();
                    return;
                }

                // Determine destination path with conflict handling
                var destinationPath = await ResolveDestinationPathAsync(job, token);

                // Create destination directory
                var destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // Safe copy: copy to temp file, then rename
                // Use unique temp name to avoid race conditions between workers copying to same destination
                var tempPath = destinationPath + $".{job.CopyJobId}.tmp";

                try
                {
                    // Copy file with progress tracking
                    await CopyFileWithProgressAsync(job.SourceFullPath, tempPath, job.SizeBytes, token);

                    // Verify if requested
                    if (_verifyAfterCopy)
                    {
                        var verified = await VerifyFileAsync(tempPath, job.HashBytes, token);
                        if (!verified)
                        {
                            throw new InvalidOperationException("Verification failed: hash mismatch after copy");
                        }
                    }

                    // Atomic rename (overwrite if exists for retry scenarios)
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }
                    File.Move(tempPath, destinationPath);

                    // Mark as completed - pass actual destination path if it differs from planned
                    // This is important when conflicts are resolved by adding hash suffix
                    var actualPathChanged = !string.Equals(destinationPath, job.DestinationFullPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                    await _copyJobRepo.MarkCompletedAsync(job.CopyJobId, _verifyAfterCopy, actualPathChanged ? destinationPath : null, token);
                    IncrementCopied(_verifyAfterCopy);
                    success = true;

                    // Note: Per-file logging removed to avoid 300k+ log entries at scale
                }
                finally
                {
                    // Clean up temp file if it exists
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Copy attempt {Attempt} failed for {File}", attempt, job.FileName);

                if (attempt >= MaxRetryAttempts)
                {
                    await _copyJobRepo.MarkFailedAsync(job.CopyJobId, ex.Message, token);
                    IncrementFailed();
                    OnJobFailed(job, ex.Message, attempt);
                }
                else
                {
                    // Exponential backoff before retry
                    var delay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100);
                    await Task.Delay(delay, token);
                }
            }
        }
    }

    private async Task<string> ResolveDestinationPathAsync(CopyJobDetail job, CancellationToken token)
    {
        var destinationPath = job.DestinationFullPath.Replace('/', Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(destinationPath);
        var ext = Path.GetExtension(destinationPath);
        var hashSuffix = job.HashHex[..8]; // First 8 chars of hash

        // Check for conflicts:
        // 1. File already exists at destination
        // 2. Another worker is currently copying to the same destination (temp file exists)
        var needsHashSuffix = false;

        if (File.Exists(destinationPath))
        {
            try
            {
                var existingHash = await ComputeFileHashAsync(destinationPath, token);
                if (existingHash.SequenceEqual(job.HashBytes))
                {
                    // Same file already exists, can skip or overwrite
                    return destinationPath;
                }
                // Different file exists at destination
                needsHashSuffix = true;
            }
            catch (IOException)
            {
                // File is locked (another worker might be finishing), use hash suffix
                needsHashSuffix = true;
            }
        }

        // Also check if any temp file exists for this destination (another worker copying)
        // This handles race condition where two workers start copying to same destination simultaneously
        if (!needsHashSuffix && !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try
            {
                var tempPattern = $"{nameWithoutExt}{ext}.*.tmp";
                var tempFiles = Directory.GetFiles(dir, tempPattern);
                if (tempFiles.Length > 0)
                {
                    // Another worker is copying to this destination, use hash suffix to avoid conflict
                    _logger.LogDebug("Temp file exists for {Path}, using hash suffix to avoid race condition", destinationPath);
                    needsHashSuffix = true;
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Directory doesn't exist yet, no conflict possible
            }
        }

        if (needsHashSuffix)
        {
            destinationPath = Path.Combine(dir, $"{nameWithoutExt}_{hashSuffix}{ext}");
            _logger.LogDebug("Conflict detected, using: {Path}", destinationPath);
        }

        return destinationPath;
    }

    private async Task CopyFileWithProgressAsync(string source, string destination, long fileSize, CancellationToken token)
    {
        var buffer = new byte[CopyBufferSize];
        var totalBytesRead = 0L;

        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.WriteThrough);

        int bytesRead;
        while ((bytesRead = await sourceStream.ReadAsync(buffer, token)) > 0)
        {
            // Check for cancellation and pause between chunks
            token.ThrowIfCancellationRequested();
            _pauseEvent.Wait(token);

            await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
            totalBytesRead += bytesRead;

            // Update progress
            lock (_progressLock)
            {
                _currentProgress.BytesCopied += bytesRead;
            }

            ThrottledProgressUpdate();
        }

        // Preserve file timestamps
        var sourceInfo = new FileInfo(source);
        File.SetCreationTimeUtc(destination, sourceInfo.CreationTimeUtc);
        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
    }

    private async Task<bool> VerifyFileAsync(string filePath, byte[] expectedHash, CancellationToken token)
    {
        var actualHash = await ComputeFileHashAsync(filePath, token);
        return actualHash.SequenceEqual(expectedHash);
    }

    private static async Task<byte[]> ComputeFileHashAsync(string filePath, CancellationToken token)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return await sha256.ComputeHashAsync(stream, token);
    }

    private void IncrementCopied(bool verified)
    {
        lock (_progressLock)
        {
            _currentProgress.FilesCopied++;
            if (verified)
            {
                _currentProgress.FilesVerified++;
            }
        }
        ThrottledProgressUpdate();
    }

    private void IncrementSkipped()
    {
        lock (_progressLock)
        {
            _currentProgress.FilesSkipped++;
        }
        ThrottledProgressUpdate();
    }

    private void IncrementFailed()
    {
        lock (_progressLock)
        {
            _currentProgress.FailedCount++;
        }
        ThrottledProgressUpdate();
    }

    private void ThrottledProgressUpdate()
    {
        var now = DateTime.Now;
        if ((now - _lastProgressUpdate).TotalMilliseconds >= ProgressUpdateIntervalMs)
        {
            _lastProgressUpdate = now;
            RaiseProgressChanged();
        }
    }

    private void RaiseProgressChanged()
    {
        ProgressChanged?.Invoke(this, CurrentProgress);
    }

    private async Task OnCopyCompletedAsync(bool success, long filesCopied, long filesVerified, long filesSkipped,
        int failedCount, long bytesCopied, TimeSpan duration, string? errorMessage = null)
    {
        _logger.LogInformation("Copy completed: Success={Success}, Copied={Copied}, Verified={Verified}, Skipped={Skipped}, Failed={Failed}, Duration={Duration}",
            success, filesCopied, filesVerified, filesSkipped, failedCount, duration);

        RaiseProgressChanged();

        // Fetch detailed statistics if copy was successful
        CopyCompletionStats? detailedStats = null;
        if (success)
        {
            try
            {
                detailedStats = await _copyJobRepo.GetCompletionStatsAsync(duration);
                _logger.LogInformation("Detailed stats: {Files} files, {Size}, {Speed:F2} MB/s, {TimePerFile}/file",
                    detailedStats.TotalFilesCopied,
                    detailedStats.TotalSizeFormatted,
                    detailedStats.MBPerSecond,
                    detailedStats.TimePerFileFormatted);

                // Log extension breakdown
                foreach (var ext in detailedStats.ExtensionBreakdown.Take(10))
                {
                    _logger.LogDebug("  {Extension}: {Count:N0} files ({Size})",
                        ext.Extension, ext.FileCount, ext.SizeFormatted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get detailed completion stats");
            }
        }

        CopyCompleted?.Invoke(this, new CopyCompletedEventArgs
        {
            Success = success,
            TotalFilesCopied = filesCopied,
            FilesVerified = filesVerified,
            FilesSkipped = filesSkipped,
            FailedCount = failedCount,
            BytesCopied = bytesCopied,
            Duration = duration,
            ErrorMessage = errorMessage,
            DetailedStats = detailedStats
        });
    }

    private void OnJobFailed(CopyJobDetail job, string errorMessage, int attemptCount)
    {
        JobFailed?.Invoke(this, new CopyJobFailedEventArgs
        {
            CopyJobId = job.CopyJobId,
            UniqueFileId = job.UniqueFileId,
            FileName = job.FileName,
            SourcePath = job.SourceFullPath,
            DestinationPath = job.DestinationFullPath,
            ErrorMessage = errorMessage,
            AttemptCount = attemptCount
        });
    }
}
