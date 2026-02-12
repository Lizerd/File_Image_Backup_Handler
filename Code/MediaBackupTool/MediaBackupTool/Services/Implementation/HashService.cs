using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Data;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Service for computing file hashes for duplicate detection.
/// Uses parallel workers with bounded channels for backpressure.
/// </summary>
public class HashService : IHashService
{
    private readonly IProjectService _projectService;
    private readonly IPowerManagementService _powerManagement;
    private readonly ILogger<HashService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private CancellationTokenSource? _hashCts;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private HashProgress _currentProgress = new();
    private readonly object _progressLock = new();
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private const int ProgressUpdateIntervalMs = 200;

    // Track unique hashes found during this session
    private readonly ConcurrentDictionary<string, long> _hashToIdMap = new();

    public HashProgress CurrentProgress
    {
        get
        {
            lock (_progressLock)
            {
                return _currentProgress;
            }
        }
    }

    public event EventHandler<HashProgress>? ProgressChanged;
    public event EventHandler<HashCompletedEventArgs>? HashCompleted;

    public HashService(
        IProjectService projectService,
        IPowerManagementService powerManagement,
        ILogger<HashService> logger,
        ILoggerFactory loggerFactory)
    {
        _projectService = projectService;
        _powerManagement = powerManagement;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task StartHashingAsync(CancellationToken cancellationToken = default)
    {
        if (!_projectService.IsProjectOpen)
        {
            throw new InvalidOperationException("No project is open");
        }

        _hashCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _hashCts.Token;

        _hashToIdMap.Clear();

        lock (_progressLock)
        {
            _currentProgress = new HashProgress { StartTime = DateTime.Now };
        }

        // Prevent system sleep during hashing
        _powerManagement.BeginOperation("Hash");

        var projectService = (ProjectService)_projectService;
        var context = projectService.GetContext();

        // Run hashing on background thread
        await Task.Run(async () =>
        {
            try
            {
                // Get files that need hashing
                var filesToHash = await GetFilesToHashAsync(context, token);

                if (filesToHash.Count == 0)
                {
                    _logger.LogInformation("No files to hash");
                    OnHashCompleted(true, 0, 0, 0, 0, 0, TimeSpan.Zero);
                    return; // finally block will call EndOperation
                }

                lock (_progressLock)
                {
                    _currentProgress.TotalFiles = filesToHash.Count;
                    _currentProgress.TotalBytesToHash = filesToHash.Sum(f => f.SizeBytes);
                }

                _logger.LogInformation("Starting hash of {Count} files, {Bytes:N0} bytes total",
                    filesToHash.Count, _currentProgress.TotalBytesToHash);

                // Determine worker count based on CPU profile (use Balanced = 25% of cores)
                var workerCount = Math.Max(1, Environment.ProcessorCount / 4);
                lock (_progressLock)
                {
                    _currentProgress.WorkerCount = workerCount;
                }

                _logger.LogInformation("Using {Workers} hash workers", workerCount);

                // Create bounded channel for file queue
                var channel = Channel.CreateBounded<FileToHash>(new BoundedChannelOptions(1000)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true
                });

                // Start worker tasks
                var workerTasks = Enumerable.Range(0, workerCount)
                    .Select(_ => HashWorkerAsync(channel.Reader, context, token))
                    .ToArray();

                // Feed files to channel
                foreach (var file in filesToHash)
                {
                    token.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(token);
                    await channel.Writer.WriteAsync(file, token);
                }

                channel.Writer.Complete();

                // Wait for all workers to finish
                await Task.WhenAll(workerTasks);

                var progress = CurrentProgress;
                var uniqueCount = _hashToIdMap.Count;
                var duplicateCount = progress.FilesHashed - uniqueCount;

                OnHashCompleted(true, progress.FilesHashed, progress.FilesSkipped,
                    progress.ErrorCount, uniqueCount, duplicateCount, progress.Elapsed);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Hashing cancelled");
                var progress = CurrentProgress;
                OnHashCompleted(false, progress.FilesHashed, progress.FilesSkipped,
                    progress.ErrorCount, 0, 0, progress.Elapsed, "Hashing was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hashing failed with error");
                var progress = CurrentProgress;
                OnHashCompleted(false, progress.FilesHashed, progress.FilesSkipped,
                    progress.ErrorCount, 0, 0, progress.Elapsed, ex.Message);
            }
            finally
            {
                // Allow system sleep after hashing completes
                _powerManagement.EndOperation("Hash");
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
        _logger.LogInformation("Hashing paused");
    }

    public void Resume()
    {
        _pauseEvent.Set();
        lock (_progressLock)
        {
            _currentProgress.IsPaused = false;
        }
        RaiseProgressChanged();
        _logger.LogInformation("Hashing resumed");
    }

    private async Task<List<FileToHash>> GetFilesToHashAsync(DatabaseContext context, CancellationToken token)
    {
        var files = new List<FileToHash>();

        using var connection = await context.CreateConnectionAsync();

        // Get all files that don't have a hash yet
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT fi.Id, fi.ScanRootId, fi.RelativePath, fi.SizeBytes, sr.Path as RootPath
            FROM FileInstances fi
            INNER JOIN ScanRoots sr ON fi.ScanRootId = sr.Id
            WHERE fi.HashId IS NULL
            ORDER BY fi.SizeBytes DESC";

        using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            files.Add(new FileToHash
            {
                FileInstanceId = reader.GetInt64(0),
                ScanRootId = reader.GetInt64(1),
                RelativePath = reader.GetString(2),
                SizeBytes = reader.GetInt64(3),
                RootPath = reader.GetString(4)
            });
        }

        return files;
    }

    private async Task HashWorkerAsync(ChannelReader<FileToHash> reader, DatabaseContext context, CancellationToken token)
    {
        using var connection = await context.CreateConnectionAsync();
        using var sha256 = SHA256.Create();

        await foreach (var file in reader.ReadAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            _pauseEvent.Wait(token);

            var fullPath = Path.Combine(file.RootPath, file.RelativePath);

            lock (_progressLock)
            {
                _currentProgress.CurrentFile = file.RelativePath;
                _currentProgress.CurrentFileSize = file.SizeBytes;
            }

            try
            {
                if (!File.Exists(fullPath))
                {
                    _logger.LogWarning("File not found: {Path}", fullPath);
                    IncrementErrorCount();
                    continue;
                }

                // Compute hash
                byte[] hashBytes;
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                {
                    hashBytes = await sha256.ComputeHashAsync(stream, token);
                }

                var hashHex = Convert.ToHexString(hashBytes);

                // Insert or get hash record
                var hashId = await GetOrCreateHashAsync(connection, hashBytes, hashHex, file.SizeBytes, token);

                // Update file instance with hash
                await UpdateFileHashAsync(connection, file.FileInstanceId, hashId, token);

                // Track progress
                lock (_progressLock)
                {
                    _currentProgress.FilesHashed++;
                    _currentProgress.TotalBytesHashed += file.SizeBytes;
                }

                ThrottledProgressUpdate();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error hashing file: {Path}", fullPath);
                IncrementErrorCount();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied hashing file: {Path}", fullPath);
                IncrementErrorCount();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error hashing file: {Path}", fullPath);
                IncrementErrorCount();
            }
        }
    }

    private async Task<long> GetOrCreateHashAsync(SqliteConnection connection, byte[] hashBytes, string hashHex, long sizeBytes, CancellationToken token)
    {
        // Check cache first
        if (_hashToIdMap.TryGetValue(hashHex, out var cachedId))
        {
            return cachedId;
        }

        // Check database
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT Id FROM Hashes WHERE HashHex = @hashHex";
            selectCmd.Parameters.AddWithValue("@hashHex", hashHex);
            var result = await selectCmd.ExecuteScalarAsync(token);
            if (result != null)
            {
                var id = Convert.ToInt64(result);
                _hashToIdMap.TryAdd(hashHex, id);
                return id;
            }
        }

        // Insert new hash
        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.CommandText = @"
                INSERT INTO Hashes (HashAlgorithm, HashBytes, HashHex, SizeBytes, ComputedUtc)
                VALUES ('SHA256', @hashBytes, @hashHex, @sizeBytes, @computedUtc);
                SELECT last_insert_rowid();";
            insertCmd.Parameters.AddWithValue("@hashBytes", hashBytes);
            insertCmd.Parameters.AddWithValue("@hashHex", hashHex);
            insertCmd.Parameters.AddWithValue("@sizeBytes", sizeBytes);
            insertCmd.Parameters.AddWithValue("@computedUtc", DateTime.UtcNow.ToString("o"));

            var newId = Convert.ToInt64(await insertCmd.ExecuteScalarAsync(token));
            _hashToIdMap.TryAdd(hashHex, newId);
            return newId;
        }
    }

    private async Task UpdateFileHashAsync(SqliteConnection connection, long fileInstanceId, long hashId, CancellationToken token)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE FileInstances SET HashId = @hashId, Status = 1 WHERE Id = @id";
        cmd.Parameters.AddWithValue("@hashId", hashId);
        cmd.Parameters.AddWithValue("@id", fileInstanceId);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private void IncrementErrorCount()
    {
        lock (_progressLock)
        {
            _currentProgress.ErrorCount++;
        }
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

    private void OnHashCompleted(bool success, int filesHashed, int filesSkipped, int errorCount,
        int uniqueHashes, int duplicates, TimeSpan duration, string? errorMessage = null)
    {
        _logger.LogInformation("Hashing completed: Success={Success}, Hashed={Hashed}, Unique={Unique}, Duplicates={Duplicates}, Errors={Errors}, Duration={Duration}",
            success, filesHashed, uniqueHashes, duplicates, errorCount, duration);

        // Always raise a final progress update
        RaiseProgressChanged();

        HashCompleted?.Invoke(this, new HashCompletedEventArgs
        {
            Success = success,
            TotalFilesHashed = filesHashed,
            FilesSkipped = filesSkipped,
            ErrorCount = errorCount,
            UniqueHashesFound = uniqueHashes,
            DuplicatesFound = duplicates,
            Duration = duration,
            ErrorMessage = errorMessage
        });
    }

    private class FileToHash
    {
        public long FileInstanceId { get; set; }
        public long ScanRootId { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string RootPath { get; set; } = string.Empty;
    }
}
