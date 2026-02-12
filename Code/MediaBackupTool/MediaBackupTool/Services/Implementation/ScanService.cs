using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Data;
using MediaBackupTool.Data.Repositories;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Service for scanning directories and discovering media files.
/// Uses streaming enumeration with bounded channels for backpressure.
/// </summary>
public class ScanService : IScanService
{
    private readonly IFileFilterService _filterService;
    private readonly IProjectService _projectService;
    private readonly IPowerManagementService _powerManagement;
    private readonly ILogger<ScanService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private CancellationTokenSource? _scanCts;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private ScanProgress _currentProgress = new();
    private readonly object _progressLock = new();
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private const int ProgressUpdateIntervalMs = 200; // Throttle UI updates

    public ScanProgress CurrentProgress
    {
        get
        {
            lock (_progressLock)
            {
                return _currentProgress;
            }
        }
    }

    public event EventHandler<ScanProgress>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public ScanService(
        IFileFilterService filterService,
        IProjectService projectService,
        IPowerManagementService powerManagement,
        ILogger<ScanService> logger,
        ILoggerFactory loggerFactory)
    {
        _filterService = filterService;
        _projectService = projectService;
        _powerManagement = powerManagement;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        if (!_projectService.IsProjectOpen)
        {
            throw new InvalidOperationException("No project is open");
        }

        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _scanCts.Token;

        lock (_progressLock)
        {
            _currentProgress = new ScanProgress { StartTime = DateTime.Now };
        }

        // Prevent system sleep during scan
        _powerManagement.BeginOperation("Scan");

        // Get repositories from project service
        var projectServiceImpl = (ProjectService)_projectService;
        var scanRootRepo = projectServiceImpl.GetScanRootRepository();
        var context = projectServiceImpl.GetContext();
        var fileInstanceRepo = new FileInstanceRepository(context, _loggerFactory.CreateLogger<FileInstanceRepository>());

        // Run the scan on a background thread to keep UI responsive
        await Task.Run(async () =>
        {
            try
            {
                var scanRoots = await scanRootRepo.GetEnabledAsync(token);
                if (scanRoots.Count == 0)
                {
                    _logger.LogWarning("No scan roots configured");
                    OnScanCompleted(true, 0, 0, TimeSpan.Zero);
                    return; // finally block will call EndOperation
                }

                _logger.LogInformation("Starting scan of {Count} root(s)", scanRoots.Count);

                // Create bounded channel for backpressure
                var channel = Channel.CreateBounded<FileInstance>(new BoundedChannelOptions(50000)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

                // Start database writer task on its own thread
                var writerTask = Task.Run(() => StartDatabaseWriterAsync(channel.Reader, fileInstanceRepo, token), token);

                // Scan all roots
                foreach (var root in scanRoots)
                {
                    if (token.IsCancellationRequested) break;
                    await ScanRootAsync(root, channel.Writer, scanRootRepo, token);
                }

                // Signal completion
                channel.Writer.Complete();

                // Wait for writer to finish
                await writerTask;

                // Raise final progress event to ensure UI is updated with final counts
                // (progress updates are throttled, so last update may not reflect final count)
                RaiseProgressChanged();

                var progress = CurrentProgress;
                OnScanCompleted(true, progress.TotalFilesFound, progress.ErrorCount, progress.Elapsed);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Scan cancelled");
                RaiseProgressChanged(); // Ensure UI has final counts
                var progress = CurrentProgress;
                OnScanCompleted(false, progress.TotalFilesFound, progress.ErrorCount, progress.Elapsed, "Scan was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan failed with error");
                RaiseProgressChanged(); // Ensure UI has final counts
                var progress = CurrentProgress;
                OnScanCompleted(false, progress.TotalFilesFound, progress.ErrorCount, progress.Elapsed, ex.Message);
            }
            finally
            {
                // Allow system sleep after scan completes
                _powerManagement.EndOperation("Scan");
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
        _logger.LogInformation("Scan paused");
    }

    public void Resume()
    {
        _pauseEvent.Set();
        lock (_progressLock)
        {
            _currentProgress.IsPaused = false;
        }
        RaiseProgressChanged();
        _logger.LogInformation("Scan resumed");
    }

    private async Task ScanRootAsync(ScanRoot root, ChannelWriter<FileInstance> writer, ScanRootRepository scanRootRepo, CancellationToken token)
    {
        _logger.LogInformation("Scanning root: {Path}", root.Path);

        lock (_progressLock)
        {
            _currentProgress.CurrentRoot = root.Path;
        }

        if (!Directory.Exists(root.Path))
        {
            _logger.LogWarning("Root path does not exist: {Path}", root.Path);
            return;
        }

        // Clear existing files for this root before rescanning to avoid duplicates
        var projectServiceImpl = (ProjectService)_projectService;
        var context = projectServiceImpl.GetContext();
        await ClearExistingFilesForRootAsync(context, root.Id, token);

        var rootFileCount = 0;
        long rootTotalBytes = 0;

        await foreach (var file in EnumerateFilesAsync(root, token))
        {
            // Handle pause
            _pauseEvent.Wait(token);

            await writer.WriteAsync(file, token);
            rootFileCount++;
            rootTotalBytes += file.SizeBytes;

            UpdateProgress(file.RelativePath, 1, file.SizeBytes);
        }

        // Update root statistics
        await scanRootRepo.UpdateStatsAsync(root.Id, rootFileCount, rootTotalBytes, token);
        _logger.LogInformation("Root {Path}: found {Count} files, {Bytes} bytes", root.Path, rootFileCount, rootTotalBytes);
    }

    private async IAsyncEnumerable<FileInstance> EnumerateFilesAsync(ScanRoot root, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root.Path);
        var enabledExtensions = _filterService.GetEnabledExtensions();

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var currentDir = stack.Pop();

            // Skip reparse points (junctions, symlinks)
            try
            {
                var dirInfo = new DirectoryInfo(currentDir);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Note: Per-reparse-point logging removed to reduce log volume
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot access directory: {Path}", currentDir);
                IncrementErrorCount();
                continue;
            }

            IncrementDirectoriesScanned();

            // Enumerate files in current directory
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Access denied: {Path}", currentDir);
                IncrementErrorCount();
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error enumerating files in: {Path}", currentDir);
                IncrementErrorCount();
                continue;
            }

            foreach (var filePath in files)
            {
                token.ThrowIfCancellationRequested();

                // Quick extension check first (cheapest filter)
                var extension = Path.GetExtension(filePath);
                if (!enabledExtensions.Contains(extension))
                    continue;

                FileInstance? instance = null;
                try
                {
                    var fileInfo = new FileInfo(filePath);

                    // Skip if filtered by size
                    if (!_filterService.ShouldIncludeFile(filePath, fileInfo.Length))
                        continue;

                    var relativePath = GetRelativePath(root.Path, filePath);

                    instance = new FileInstance
                    {
                        ScanRootId = root.Id,
                        RelativePath = relativePath,
                        FileName = fileInfo.Name,
                        Extension = extension.ToLowerInvariant(),
                        SizeBytes = fileInfo.Length,
                        ModifiedUtc = fileInfo.LastWriteTimeUtc,
                        Status = FileStatus.Discovered,
                        Category = _filterService.GetFileCategory(extension),
                        DiscoveredUtc = DateTime.UtcNow
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing file: {Path}", filePath);
                    IncrementErrorCount();
                    continue;
                }

                yield return instance;
            }

            // Push subdirectories onto stack
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error enumerating subdirectories in: {Path}", currentDir);
                IncrementErrorCount();
                continue;
            }

            foreach (var subdir in subdirs)
            {
                // Skip reparse points
                try
                {
                    var subdirInfo = new DirectoryInfo(subdir);
                    if ((subdirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    stack.Push(subdir);
                }
                catch
                {
                    // Skip directories we can't access
                }
            }
        }
    }

    private async Task StartDatabaseWriterAsync(ChannelReader<FileInstance> reader, FileInstanceRepository fileInstanceRepo, CancellationToken token)
    {
        var batch = new List<FileInstance>(10000);
        var totalWritten = 0;

        try
        {
            await foreach (var file in reader.ReadAllAsync(token))
            {
                batch.Add(file);

                if (batch.Count >= 10000)
                {
                    await fileInstanceRepo.InsertBatchAsync(batch, token);
                    totalWritten += batch.Count;
                    _logger.LogDebug("Written {Total} files to database", totalWritten);
                    batch.Clear();
                }
            }

            // Write remaining files
            if (batch.Count > 0)
            {
                await fileInstanceRepo.InsertBatchAsync(batch, token);
                totalWritten += batch.Count;
            }

            _logger.LogInformation("Database writer completed: {Total} files written", totalWritten);
        }
        catch (OperationCanceledException)
        {
            // Write what we have on cancellation
            if (batch.Count > 0)
            {
                try
                {
                    await fileInstanceRepo.InsertBatchAsync(batch, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write final batch on cancellation");
                }
            }
            throw;
        }
    }

    private void UpdateProgress(string currentPath, int filesFound, long bytesFound)
    {
        lock (_progressLock)
        {
            _currentProgress.TotalFilesFound += filesFound;
            _currentProgress.FilesProcessed += filesFound;
            _currentProgress.TotalBytesFound += bytesFound;
            _currentProgress.CurrentPath = currentPath;
        }

        // Throttle UI updates
        var now = DateTime.Now;
        if ((now - _lastProgressUpdate).TotalMilliseconds >= ProgressUpdateIntervalMs)
        {
            _lastProgressUpdate = now;
            RaiseProgressChanged();
        }
    }

    private void IncrementDirectoriesScanned()
    {
        lock (_progressLock)
        {
            _currentProgress.DirectoriesScanned++;
        }
    }

    private void IncrementErrorCount()
    {
        lock (_progressLock)
        {
            _currentProgress.ErrorCount++;
        }
    }

    private void RaiseProgressChanged()
    {
        ProgressChanged?.Invoke(this, CurrentProgress);
    }

    private void OnScanCompleted(bool success, int totalFiles, int errorCount, TimeSpan duration, string? errorMessage = null)
    {
        _logger.LogInformation("Scan completed: Success={Success}, Files={Files}, Errors={Errors}, Duration={Duration}",
            success, totalFiles, errorCount, duration);

        ScanCompleted?.Invoke(this, new ScanCompletedEventArgs
        {
            Success = success,
            TotalFilesFound = totalFiles,
            ErrorCount = errorCount,
            Duration = duration,
            ErrorMessage = errorMessage
        });
    }

    private static string GetRelativePath(string rootPath, string fullPath)
    {
        var rootUri = new Uri(rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var fileUri = new Uri(fullPath);
        return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString().Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Clears existing file instances for a scan root before rescanning.
    /// Also clears related plan data (UniqueFiles, FolderNodes) since they will be invalid.
    /// </summary>
    private async Task ClearExistingFilesForRootAsync(Data.DatabaseContext context, long scanRootId, CancellationToken token)
    {
        using var connection = await context.CreateConnectionAsync();

        // First, clear the plan data since it depends on FileInstances
        // The user will need to regenerate the plan after rescanning
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM UniqueFiles";
            var uniqueDeleted = await cmd.ExecuteNonQueryAsync(token);
            if (uniqueDeleted > 0)
            {
                _logger.LogInformation("Cleared {Count} UniqueFiles entries (plan will need regeneration)", uniqueDeleted);
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM FolderNodes";
            var foldersDeleted = await cmd.ExecuteNonQueryAsync(token);
            if (foldersDeleted > 0)
            {
                _logger.LogInformation("Cleared {Count} FolderNodes entries", foldersDeleted);
            }
        }

        // Now clear existing file instances for this scan root
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM FileInstances WHERE ScanRootId = @ScanRootId";
            cmd.Parameters.AddWithValue("@ScanRootId", scanRootId);
            var filesDeleted = await cmd.ExecuteNonQueryAsync(token);
            _logger.LogInformation("Cleared {Count} existing FileInstances for ScanRoot {Id}", filesDeleted, scanRootId);
        }

        // Clean up orphaned hashes (hashes no longer referenced by any FileInstance)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                DELETE FROM Hashes
                WHERE Id NOT IN (SELECT DISTINCT HashId FROM FileInstances WHERE HashId IS NOT NULL)";
            var hashesDeleted = await cmd.ExecuteNonQueryAsync(token);
            if (hashesDeleted > 0)
            {
                _logger.LogInformation("Cleaned up {Count} orphaned hash entries", hashesDeleted);
            }
        }
    }
}
