using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Data.Repositories;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Service for verifying copied files by direct byte-by-byte comparison with source files.
/// This is an independent verification that doesn't rely on database hashes.
/// </summary>
public class VerificationService : IVerificationService
{
    private readonly IProjectService _projectService;
    private readonly CopyJobRepository _copyJobRepo;
    private readonly IPowerManagementService _powerManagement;
    private readonly ILogger<VerificationService> _logger;

    private CancellationTokenSource? _verifyCts;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private VerificationProgress _currentProgress = new();
    private readonly object _progressLock = new();
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private const int ProgressUpdateIntervalMs = 150;
    private const int HashBufferSize = 1024 * 1024; // 1 MB buffer

    private readonly ConcurrentBag<VerificationMismatch> _mismatches = new();

    public VerificationProgress CurrentProgress
    {
        get
        {
            lock (_progressLock)
            {
                return _currentProgress;
            }
        }
    }

    public event EventHandler<VerificationProgress>? ProgressChanged;
    public event EventHandler<VerificationCompletedEventArgs>? VerificationCompleted;
    public event EventHandler<VerificationMismatchEventArgs>? MismatchDetected;

    public VerificationService(
        IProjectService projectService,
        CopyJobRepository copyJobRepo,
        IPowerManagementService powerManagement,
        ILogger<VerificationService> logger)
    {
        _projectService = projectService;
        _copyJobRepo = copyJobRepo;
        _powerManagement = powerManagement;
        _logger = logger;
    }

    public async Task StartVerificationAsync(CancellationToken cancellationToken = default)
    {
        if (!_projectService.IsProjectOpen)
        {
            throw new InvalidOperationException("No project is open");
        }

        _verifyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _verifyCts.Token;

        _mismatches.Clear();

        lock (_progressLock)
        {
            _currentProgress = new VerificationProgress
            {
                StartTime = DateTime.Now,
                Phase = VerificationPhase.Preparing
            };
        }
        RaiseProgressChanged();

        // Prevent system sleep during verification
        _powerManagement.BeginOperation("Verification");

        await Task.Run(async () =>
        {
            try
            {
                // Phase 1: Load completed copy jobs
                UpdatePhase(VerificationPhase.LoadingFileList);
                var filesToVerify = await LoadFilesToVerifyAsync(token);

                if (filesToVerify.Count == 0)
                {
                    _logger.LogWarning("No files to verify");
                    UpdatePhase(VerificationPhase.Completed);
                    OnVerificationCompleted(true, 0, 0, 0, 0, 0, 0, TimeSpan.Zero);
                    return; // finally block will call EndOperation
                }

                // Calculate totals
                var totalBytes = filesToVerify.Sum(f => f.SizeBytes);
                lock (_progressLock)
                {
                    _currentProgress.TotalFiles = filesToVerify.Count;
                    _currentProgress.TotalBytes = totalBytes;
                }

                _logger.LogInformation("Starting verification of {Count} files ({Bytes:N0} bytes)",
                    filesToVerify.Count, totalBytes);

                // Phase 2: Verify files
                UpdatePhase(VerificationPhase.Verifying);

                // Use multiple workers for parallel verification
                var workerCount = Math.Max(2, Environment.ProcessorCount / 2);
                _logger.LogInformation("Using {Workers} verification workers", workerCount);

                var channel = Channel.CreateBounded<FileToVerify>(new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true
                });

                // Start workers
                var workerTasks = Enumerable.Range(0, workerCount)
                    .Select(_ => VerifyWorkerAsync(channel.Reader, token))
                    .ToArray();

                // Feed files to channel
                foreach (var file in filesToVerify)
                {
                    token.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(token);
                    await channel.Writer.WriteAsync(file, token);
                }

                channel.Writer.Complete();
                await Task.WhenAll(workerTasks);

                // Complete
                UpdatePhase(VerificationPhase.Completed);
                var progress = CurrentProgress;
                var mismatchList = _mismatches.ToList();

                OnVerificationCompleted(
                    mismatchList.Count == 0,
                    progress.FilesVerified,
                    progress.FilesMatched,
                    progress.FilesMismatched,
                    progress.FilesSkipped,
                    progress.ErrorCount,
                    progress.BytesVerified,
                    progress.Elapsed,
                    mismatchList);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Verification cancelled");
                UpdatePhase(VerificationPhase.Cancelled);
                var progress = CurrentProgress;
                OnVerificationCompleted(false, progress.FilesVerified, progress.FilesMatched,
                    progress.FilesMismatched, progress.FilesSkipped, progress.ErrorCount,
                    progress.BytesVerified, progress.Elapsed, errorMessage: "Verification was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Verification failed with error");
                UpdatePhase(VerificationPhase.Failed);
                var progress = CurrentProgress;
                OnVerificationCompleted(false, progress.FilesVerified, progress.FilesMatched,
                    progress.FilesMismatched, progress.FilesSkipped, progress.ErrorCount,
                    progress.BytesVerified, progress.Elapsed, errorMessage: ex.Message);
            }
            finally
            {
                // Allow system sleep after verification completes
                _powerManagement.EndOperation("Verification");
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
        _logger.LogInformation("Verification paused");
    }

    public void Resume()
    {
        _pauseEvent.Set();
        lock (_progressLock)
        {
            _currentProgress.IsPaused = false;
        }
        RaiseProgressChanged();
        _logger.LogInformation("Verification resumed");
    }

    private async Task<List<FileToVerify>> LoadFilesToVerifyAsync(CancellationToken token)
    {
        var files = new List<FileToVerify>();

        var projectService = (ProjectService)_projectService;
        var context = projectService.GetContext();
        using var connection = await context.CreateConnectionAsync();

        // Get all completed/verified copy jobs with their source and destination paths
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                cj.Id, cj.DestinationFullPath,
                fi.RelativePath, fi.SizeBytes,
                sr.Path as ScanRootPath
            FROM CopyJobs cj
            INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            WHERE cj.Status IN (@CopiedStatus, @VerifiedStatus)
            ORDER BY fi.SizeBytes DESC";
        cmd.Parameters.AddWithValue("@CopiedStatus", (int)CopyJobStatus.Copied);
        cmd.Parameters.AddWithValue("@VerifiedStatus", (int)CopyJobStatus.Verified);

        using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var scanRootPath = reader.GetString(4);
            var relativePath = reader.GetString(2);
            var destPath = reader.GetString(1).Replace('/', Path.DirectorySeparatorChar);
            var sourcePath = Path.Combine(scanRootPath, relativePath);

            // Detect if the file was renamed due to conflict resolution
            var (wasRenamed, originalPath) = DetectRename(sourcePath, destPath);

            files.Add(new FileToVerify
            {
                CopyJobId = reader.GetInt64(0),
                SourcePath = sourcePath,
                DestPath = destPath,
                SizeBytes = reader.GetInt64(3),
                WasRenamed = wasRenamed,
                OriginalPlannedPath = originalPath
            });
        }

        _logger.LogDebug("Loaded {Count} files to verify", files.Count);
        return files;
    }

    private async Task VerifyWorkerAsync(ChannelReader<FileToVerify> reader, CancellationToken token)
    {
        try
        {
            await foreach (var file in reader.ReadAllAsync(token))
            {
                if (token.IsCancellationRequested)
                    break;

                _pauseEvent.Wait(token);

                if (token.IsCancellationRequested)
                    break;

                await VerifyFileAsync(file, token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Verification worker cancelled");
        }
    }

    private async Task VerifyFileAsync(FileToVerify file, CancellationToken token)
    {
        lock (_progressLock)
        {
            _currentProgress.CurrentSourceFile = file.SourcePath;
            _currentProgress.CurrentDestFile = file.DestPath;
            _currentProgress.CurrentFileSize = file.SizeBytes;
        }

        try
        {
            // Check source exists
            if (!File.Exists(file.SourcePath))
            {
                RecordMismatch(file, MismatchReason.SourceMissing, "Source file not found");
                IncrementSkipped();
                return;
            }

            // Check dest exists
            if (!File.Exists(file.DestPath))
            {
                RecordMismatch(file, MismatchReason.DestMissing, "Destination file not found");
                IncrementMismatch();
                return;
            }

            // Check file sizes match first (quick check)
            var sourceInfo = new FileInfo(file.SourcePath);
            var destInfo = new FileInfo(file.DestPath);

            if (sourceInfo.Length != destInfo.Length)
            {
                RecordMismatch(file, MismatchReason.SizeMismatch,
                    $"Size mismatch: source={sourceInfo.Length}, dest={destInfo.Length}");
                IncrementMismatch();
                IncrementBytesVerified(file.SizeBytes);
                return;
            }

            // Compute hashes for both files
            var (sourceHash, destHash) = await ComputeBothHashesAsync(file, token);

            if (sourceHash == destHash)
            {
                IncrementMatched();
                // Note: Per-file logging removed to avoid 300k+ log entries at scale
            }
            else
            {
                RecordMismatch(file, MismatchReason.HashMismatch,
                    $"Hash mismatch: source={sourceHash[..16]}..., dest={destHash[..16]}...",
                    sourceHash, destHash);
                IncrementMismatch();
            }

            IncrementBytesVerified(file.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error verifying file: {Source}", file.SourcePath);
            RecordMismatch(file, MismatchReason.ReadError, ex.Message);
            IncrementError();
            IncrementBytesVerified(file.SizeBytes);
        }
    }

    private async Task<(string sourceHash, string destHash)> ComputeBothHashesAsync(FileToVerify file, CancellationToken token)
    {
        // Compute both hashes concurrently
        var sourceTask = ComputeHashAsync(file.SourcePath, token);
        var destTask = ComputeHashAsync(file.DestPath, token);

        await Task.WhenAll(sourceTask, destTask);

        return (await sourceTask, await destTask);
    }

    private async Task<string> ComputeHashAsync(string filePath, CancellationToken token)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, HashBufferSize, FileOptions.SequentialScan);

        var buffer = new byte[HashBufferSize];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, token)) > 0)
        {
            token.ThrowIfCancellationRequested();
            _pauseEvent.Wait(token);
            sha256.TransformBlock(buffer, 0, bytesRead, buffer, 0);
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash!);
    }

    private void RecordMismatch(FileToVerify file, MismatchReason reason, string errorMessage,
        string? sourceHash = null, string? destHash = null)
    {
        var mismatch = new VerificationMismatch
        {
            SourcePath = file.SourcePath,
            DestPath = file.DestPath,
            FileSize = file.SizeBytes,
            Reason = reason,
            ErrorMessage = errorMessage,
            SourceHash = sourceHash ?? string.Empty,
            DestHash = destHash ?? string.Empty,
            WasRenamed = file.WasRenamed,
            OriginalPlannedPath = file.OriginalPlannedPath
        };

        _mismatches.Add(mismatch);

        MismatchDetected?.Invoke(this, new VerificationMismatchEventArgs
        {
            SourcePath = file.SourcePath,
            DestPath = file.DestPath,
            SourceHash = sourceHash ?? string.Empty,
            DestHash = destHash ?? string.Empty,
            FileSize = file.SizeBytes,
            Reason = reason,
            WasRenamed = file.WasRenamed,
            OriginalPlannedPath = file.OriginalPlannedPath
        });

        _logger.LogWarning("Mismatch detected: {Reason} - {File}{Renamed}", reason, file.DestPath,
            file.WasRenamed ? " (renamed from conflict)" : "");
    }

    private void UpdatePhase(VerificationPhase phase)
    {
        lock (_progressLock)
        {
            _currentProgress.Phase = phase;
        }
        RaiseProgressChanged();
    }

    private void IncrementMatched()
    {
        lock (_progressLock)
        {
            _currentProgress.FilesVerified++;
            _currentProgress.FilesMatched++;
        }
        ThrottledProgressUpdate();
    }

    private void IncrementMismatch()
    {
        lock (_progressLock)
        {
            _currentProgress.FilesVerified++;
            _currentProgress.FilesMismatched++;
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

    private void IncrementError()
    {
        lock (_progressLock)
        {
            _currentProgress.ErrorCount++;
        }
        ThrottledProgressUpdate();
    }

    private void IncrementBytesVerified(long bytes)
    {
        lock (_progressLock)
        {
            _currentProgress.BytesVerified += bytes;
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

    private void OnVerificationCompleted(bool success, long filesVerified, long filesMatched,
        long filesMismatched, long filesSkipped, int errorCount, long bytesVerified,
        TimeSpan duration, IReadOnlyList<VerificationMismatch>? mismatches = null, string? errorMessage = null)
    {
        _logger.LogInformation("Verification completed: Success={Success}, Verified={Verified}, Matched={Matched}, " +
            "Mismatched={Mismatched}, Skipped={Skipped}, Errors={Errors}, Duration={Duration}",
            success, filesVerified, filesMatched, filesMismatched, filesSkipped, errorCount, duration);

        RaiseProgressChanged();

        VerificationCompleted?.Invoke(this, new VerificationCompletedEventArgs
        {
            Success = success,
            TotalFilesVerified = filesVerified,
            FilesMatched = filesMatched,
            FilesMismatched = filesMismatched,
            FilesSkipped = filesSkipped,
            ErrorCount = errorCount,
            BytesVerified = bytesVerified,
            Duration = duration,
            ErrorMessage = errorMessage,
            Mismatches = mismatches ?? Array.Empty<VerificationMismatch>()
        });
    }

    private class FileToVerify
    {
        public long CopyJobId { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string DestPath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public bool WasRenamed { get; set; }
        public string OriginalPlannedPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Detects if a destination filename was renamed due to conflict resolution.
    /// Renamed files have a pattern like "filename_XXXXXXXX.ext" where XXXXXXXX is 8 hex chars.
    /// </summary>
    private static (bool wasRenamed, string originalPath) DetectRename(string sourcePath, string destPath)
    {
        var sourceFileName = Path.GetFileNameWithoutExtension(sourcePath);
        var destFileName = Path.GetFileNameWithoutExtension(destPath);
        var destExt = Path.GetExtension(destPath);
        var destDir = Path.GetDirectoryName(destPath) ?? string.Empty;

        // Check if dest filename ends with _XXXXXXXX (8 hex chars) pattern
        if (destFileName.Length > 9 && destFileName[^9] == '_')
        {
            var suffix = destFileName[^8..];
            if (System.Text.RegularExpressions.Regex.IsMatch(suffix, "^[0-9A-Fa-f]{8}$"))
            {
                // This looks like a renamed file
                var originalFileName = destFileName[..^9]; // Remove _XXXXXXXX
                var originalPath = Path.Combine(destDir, originalFileName + destExt);
                return (true, originalPath);
            }
        }

        return (false, string.Empty);
    }
}
