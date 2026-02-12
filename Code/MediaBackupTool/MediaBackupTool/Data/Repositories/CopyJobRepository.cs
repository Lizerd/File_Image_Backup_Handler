using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Implementation;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Data.Repositories;

/// <summary>
/// Repository for CopyJob entities with batch operations.
/// Tracks copy progress and handles retries.
/// Uses separate connections for concurrent operations (thread-safe).
/// </summary>
public class CopyJobRepository
{
    private readonly ProjectService _projectService;
    private readonly ILogger<CopyJobRepository> _logger;

    public CopyJobRepository(IProjectService projectService, ILogger<CopyJobRepository> logger)
    {
        _projectService = (ProjectService)projectService;
        _logger = logger;
    }

    private DatabaseContext Context => _projectService.GetContext();

    /// <summary>
    /// Creates copy jobs for all enabled unique files.
    /// Clears any existing jobs first.
    /// </summary>
    public async Task<int> CreateJobsFromPlanAsync(string targetBasePath, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();

        // Clear existing jobs
        using (var clearCmd = connection.CreateCommand())
        {
            clearCmd.CommandText = "DELETE FROM CopyJobs";
            await clearCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Create jobs for enabled unique files with their planned destinations
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO CopyJobs (UniqueFileId, DestinationFullPath, Status, AttemptCount)
            SELECT
                uf.Id,
                @TargetBasePath || '/' || fn.ProposedRelativePath || '/' || fi.FileName,
                0,
                0
            FROM UniqueFiles uf
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN FolderNodes fn ON fn.Id = uf.PlannedFolderNodeId
            WHERE uf.CopyEnabled = 1 AND fn.CopyEnabled = 1";
        cmd.Parameters.AddWithValue("@TargetBasePath", targetBasePath.TrimEnd('/', '\\'));

        var created = await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Created {Count} copy jobs", created);
        return created;
    }

    /// <summary>
    /// Gets the next batch of pending copy jobs.
    /// Note: For concurrent processing, use ClaimPendingJobsAsync instead.
    /// </summary>
    public async Task<IReadOnlyList<CopyJobDetail>> GetPendingJobsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                cj.Id, cj.UniqueFileId, cj.DestinationFullPath, cj.Status, cj.AttemptCount,
                fi.FileName, fi.SizeBytes, fi.RelativePath,
                sr.Path as ScanRootPath,
                h.HashHex, h.HashBytes
            FROM CopyJobs cj
            INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            INNER JOIN Hashes h ON h.Id = uf.HashId
            WHERE cj.Status = @PendingStatus
            ORDER BY fi.SizeBytes DESC
            LIMIT @Limit";
        cmd.Parameters.AddWithValue("@PendingStatus", (int)CopyJobStatus.Pending);
        cmd.Parameters.AddWithValue("@Limit", limit);

        var results = new List<CopyJobDetail>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapCopyJobDetail(reader));
        }
        return results;
    }

    /// <summary>
    /// Atomically claims pending jobs by marking them as InProgress and returning them.
    /// This prevents race conditions when multiple workers are fetching jobs.
    /// Uses a transaction to ensure atomicity.
    /// </summary>
    public async Task<IReadOnlyList<CopyJobDetail>> ClaimPendingJobsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        // Use a new connection for thread-safety
        using var connection = await Context.CreateConnectionAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // First, get the IDs of pending jobs we want to claim
            var jobIds = new List<long>();
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = @"
                    SELECT cj.Id
                    FROM CopyJobs cj
                    WHERE cj.Status = @PendingStatus
                    ORDER BY cj.Id
                    LIMIT @Limit";
                selectCmd.Parameters.AddWithValue("@PendingStatus", (int)CopyJobStatus.Pending);
                selectCmd.Parameters.AddWithValue("@Limit", limit);

                using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    jobIds.Add(reader.GetInt64(0));
                }
            }

            if (jobIds.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return Array.Empty<CopyJobDetail>();
            }

            // Mark these jobs as InProgress
            var idList = string.Join(",", jobIds);
            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = $@"
                    UPDATE CopyJobs
                    SET Status = @InProgressStatus, StartedUtc = @StartedUtc, AttemptCount = AttemptCount + 1
                    WHERE Id IN ({idList})";
                updateCmd.Parameters.AddWithValue("@InProgressStatus", (int)CopyJobStatus.InProgress);
                updateCmd.Parameters.AddWithValue("@StartedUtc", DateTime.UtcNow.ToString("o"));
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Now fetch the full job details
            var results = new List<CopyJobDetail>();
            using (var detailCmd = connection.CreateCommand())
            {
                detailCmd.Transaction = transaction;
                detailCmd.CommandText = $@"
                    SELECT
                        cj.Id, cj.UniqueFileId, cj.DestinationFullPath, cj.Status, cj.AttemptCount,
                        fi.FileName, fi.SizeBytes, fi.RelativePath,
                        sr.Path as ScanRootPath,
                        h.HashHex, h.HashBytes
                    FROM CopyJobs cj
                    INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
                    INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
                    INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
                    INNER JOIN Hashes h ON h.Id = uf.HashId
                    WHERE cj.Id IN ({idList})
                    ORDER BY fi.SizeBytes DESC";

                using var reader = await detailCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(MapCopyJobDetail(reader));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            _logger.LogDebug("Claimed {Count} pending jobs", results.Count);
            return results;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Gets the count of remaining pending jobs.
    /// </summary>
    public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CopyJobs WHERE Status = @PendingStatus";
        cmd.Parameters.AddWithValue("@PendingStatus", (int)CopyJobStatus.Pending);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Gets all failed copy jobs for retry.
    /// </summary>
    public async Task<IReadOnlyList<CopyJobDetail>> GetFailedJobsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                cj.Id, cj.UniqueFileId, cj.DestinationFullPath, cj.Status, cj.AttemptCount,
                fi.FileName, fi.SizeBytes, fi.RelativePath,
                sr.Path as ScanRootPath,
                h.HashHex, h.HashBytes
            FROM CopyJobs cj
            INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            INNER JOIN Hashes h ON h.Id = uf.HashId
            WHERE cj.Status = @ErrorStatus
            ORDER BY cj.AttemptCount ASC, fi.SizeBytes DESC";
        cmd.Parameters.AddWithValue("@ErrorStatus", (int)CopyJobStatus.Error);

        var results = new List<CopyJobDetail>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapCopyJobDetail(reader));
        }
        return results;
    }

    /// <summary>
    /// Gets failed jobs with error details for UI display.
    /// </summary>
    public async Task<IReadOnlyList<FailedJobInfo>> GetFailedJobsForDisplayAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                cj.Id, cj.AttemptCount, cj.LastError,
                fi.FileName, fi.RelativePath, sr.Path as ScanRootPath
            FROM CopyJobs cj
            INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            WHERE cj.Status = @ErrorStatus
            ORDER BY cj.Id";
        cmd.Parameters.AddWithValue("@ErrorStatus", (int)CopyJobStatus.Error);

        var results = new List<FailedJobInfo>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FailedJobInfo
            {
                CopyJobId = reader.GetInt64(0),
                AttemptCount = reader.GetInt32(1),
                LastError = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                FileName = reader.GetString(3),
                SourcePath = Path.Combine(reader.GetString(5), reader.GetString(4))
            });
        }
        return results;
    }

    /// <summary>
    /// Marks a job as in progress.
    /// Uses a new connection for thread-safety in concurrent workers.
    /// </summary>
    public async Task MarkInProgressAsync(long copyJobId, CancellationToken cancellationToken = default)
    {
        // Use a new connection for thread-safety when called from concurrent workers
        using var connection = await Context.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE CopyJobs
            SET Status = @Status, StartedUtc = @StartedUtc, AttemptCount = AttemptCount + 1
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", copyJobId);
        cmd.Parameters.AddWithValue("@Status", (int)CopyJobStatus.InProgress);
        cmd.Parameters.AddWithValue("@StartedUtc", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Marks a job as completed (copied or verified).
    /// Uses a new connection for thread-safety in concurrent workers.
    /// </summary>
    /// <param name="copyJobId">The copy job ID</param>
    /// <param name="verified">Whether the copy was verified</param>
    /// <param name="actualDestinationPath">The actual destination path used (may differ from planned if conflict was resolved)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task MarkCompletedAsync(long copyJobId, bool verified, string? actualDestinationPath = null, CancellationToken cancellationToken = default)
    {
        // Use a new connection for thread-safety when called from concurrent workers
        using var connection = await Context.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();

        // If actual destination path differs from planned, update it
        if (!string.IsNullOrEmpty(actualDestinationPath))
        {
            cmd.CommandText = @"
                UPDATE CopyJobs
                SET Status = @Status, CompletedUtc = @CompletedUtc, LastError = NULL, DestinationFullPath = @ActualPath
                WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@ActualPath", actualDestinationPath);
        }
        else
        {
            cmd.CommandText = @"
                UPDATE CopyJobs
                SET Status = @Status, CompletedUtc = @CompletedUtc, LastError = NULL
                WHERE Id = @Id";
        }

        cmd.Parameters.AddWithValue("@Id", copyJobId);
        cmd.Parameters.AddWithValue("@Status", (int)(verified ? CopyJobStatus.Verified : CopyJobStatus.Copied));
        cmd.Parameters.AddWithValue("@CompletedUtc", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Marks a job as failed with error message.
    /// Uses a new connection for thread-safety in concurrent workers.
    /// </summary>
    public async Task MarkFailedAsync(long copyJobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        // Use a new connection for thread-safety when called from concurrent workers
        using var connection = await Context.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE CopyJobs
            SET Status = @Status, LastError = @Error
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", copyJobId);
        cmd.Parameters.AddWithValue("@Status", (int)CopyJobStatus.Error);
        cmd.Parameters.AddWithValue("@Error", errorMessage);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Marks a job as skipped (e.g., source file missing).
    /// Uses a new connection for thread-safety in concurrent workers.
    /// </summary>
    public async Task MarkSkippedAsync(long copyJobId, string reason, CancellationToken cancellationToken = default)
    {
        // Use a new connection for thread-safety when called from concurrent workers
        using var connection = await Context.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE CopyJobs
            SET Status = @Status, LastError = @Reason
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", copyJobId);
        cmd.Parameters.AddWithValue("@Status", (int)CopyJobStatus.Skipped);
        cmd.Parameters.AddWithValue("@Reason", reason);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Resets all failed jobs to pending for retry.
    /// </summary>
    public async Task<int> ResetFailedJobsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE CopyJobs
            SET Status = @PendingStatus, LastError = NULL
            WHERE Status = @ErrorStatus";
        cmd.Parameters.AddWithValue("@PendingStatus", (int)CopyJobStatus.Pending);
        cmd.Parameters.AddWithValue("@ErrorStatus", (int)CopyJobStatus.Error);
        var count = await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Reset {Count} failed jobs for retry", count);
        return count;
    }

    /// <summary>
    /// Resets all InProgress jobs back to Pending.
    /// Called when copy is cancelled to allow resuming later.
    /// </summary>
    public async Task<int> ResetInProgressJobsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await Context.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE CopyJobs
            SET Status = @PendingStatus, AttemptCount = MAX(0, AttemptCount - 1)
            WHERE Status = @InProgressStatus";
        cmd.Parameters.AddWithValue("@PendingStatus", (int)CopyJobStatus.Pending);
        cmd.Parameters.AddWithValue("@InProgressStatus", (int)CopyJobStatus.InProgress);
        var count = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (count > 0)
        {
            _logger.LogInformation("Reset {Count} in-progress jobs back to pending (copy cancelled)", count);
        }
        return count;
    }

    /// <summary>
    /// Gets copy job statistics.
    /// </summary>
    public async Task<CopyJobStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) as Total,
                SUM(CASE WHEN Status = 0 THEN 1 ELSE 0 END) as Pending,
                SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END) as InProgress,
                SUM(CASE WHEN Status = 2 THEN 1 ELSE 0 END) as Copied,
                SUM(CASE WHEN Status = 3 THEN 1 ELSE 0 END) as Verified,
                SUM(CASE WHEN Status = 4 THEN 1 ELSE 0 END) as Skipped,
                SUM(CASE WHEN Status = 5 THEN 1 ELSE 0 END) as Error
            FROM CopyJobs";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new CopyJobStats
            {
                Total = reader.GetInt32(0),
                Pending = reader.GetInt32(1),
                InProgress = reader.GetInt32(2),
                Copied = reader.GetInt32(3),
                Verified = reader.GetInt32(4),
                Skipped = reader.GetInt32(5),
                Error = reader.GetInt32(6)
            };
        }
        return new CopyJobStats();
    }

    /// <summary>
    /// Gets total bytes to copy for progress calculation.
    /// </summary>
    public async Task<(long TotalBytes, long TotalFiles)> GetTotalBytesAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(fi.SizeBytes), 0), COUNT(*)
            FROM CopyJobs cj
            INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (reader.GetInt64(0), reader.GetInt64(1));
        }
        return (0, 0);
    }

    /// <summary>
    /// Gets file count and size grouped by extension for completed copy jobs.
    /// </summary>
    public async Task<IReadOnlyList<ExtensionStats>> GetExtensionBreakdownAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                UPPER(fi.Extension) as Ext,
                COUNT(*) as FileCount,
                SUM(fi.SizeBytes) as TotalBytes
            FROM CopyJobs cj
            INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            WHERE cj.Status IN (2, 3)  -- Copied or Verified
            GROUP BY UPPER(fi.Extension)
            ORDER BY FileCount DESC";

        var results = new List<ExtensionStats>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ExtensionStats
            {
                Extension = reader.IsDBNull(0) ? "(no ext)" : reader.GetString(0),
                FileCount = reader.GetInt32(1),
                TotalBytes = reader.GetInt64(2)
            });
        }
        return results;
    }

    /// <summary>
    /// Gets comprehensive completion statistics including extension breakdown.
    /// </summary>
    public async Task<CopyCompletionStats> GetCompletionStatsAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();

        // Get main stats
        var stats = await GetStatsAsync(cancellationToken);

        // Get total bytes copied
        long totalBytes = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COALESCE(SUM(fi.SizeBytes), 0)
                FROM CopyJobs cj
                INNER JOIN UniqueFiles uf ON uf.Id = cj.UniqueFileId
                INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
                WHERE cj.Status IN (2, 3)";  // Copied or Verified
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            totalBytes = Convert.ToInt64(result);
        }

        // Get extension breakdown
        var extensionBreakdown = await GetExtensionBreakdownAsync(cancellationToken);

        return new CopyCompletionStats
        {
            TotalFilesCopied = stats.Completed,
            TotalBytesCopied = totalBytes,
            FilesVerified = stats.Verified,
            FilesSkipped = stats.Skipped,
            FilesFailed = stats.Error,
            TotalDuration = duration,
            ExtensionBreakdown = extensionBreakdown.ToList()
        };
    }

    private static CopyJobDetail MapCopyJobDetail(SqliteDataReader reader)
    {
        var scanRootPath = reader.GetString(8);
        var relativePath = reader.GetString(7);

        return new CopyJobDetail
        {
            CopyJobId = reader.GetInt64(0),
            UniqueFileId = reader.GetInt64(1),
            DestinationFullPath = reader.GetString(2),
            Status = (CopyJobStatus)reader.GetInt32(3),
            AttemptCount = reader.GetInt32(4),
            FileName = reader.GetString(5),
            SizeBytes = reader.GetInt64(6),
            SourceFullPath = Path.Combine(scanRootPath, relativePath),
            HashHex = reader.GetString(9),
            HashBytes = (byte[])reader.GetValue(10)
        };
    }
}

/// <summary>
/// Copy job with source file details.
/// </summary>
public class CopyJobDetail
{
    public long CopyJobId { get; set; }
    public long UniqueFileId { get; set; }
    public string DestinationFullPath { get; set; } = string.Empty;
    public CopyJobStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SourceFullPath { get; set; } = string.Empty;
    public string HashHex { get; set; } = string.Empty;
    public byte[] HashBytes { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Failed job info for UI display.
/// </summary>
public class FailedJobInfo
{
    public long CopyJobId { get; set; }
    public int AttemptCount { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
}

/// <summary>
/// Copy job statistics.
/// </summary>
public class CopyJobStats
{
    public int Total { get; set; }
    public int Pending { get; set; }
    public int InProgress { get; set; }
    public int Copied { get; set; }
    public int Verified { get; set; }
    public int Skipped { get; set; }
    public int Error { get; set; }

    public int Completed => Copied + Verified;
}

/// <summary>
/// Statistics for a single file extension.
/// </summary>
public class ExtensionStats
{
    public string Extension { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }

    public string SizeFormatted
    {
        get
        {
            if (TotalBytes < 1024) return $"{TotalBytes} B";
            if (TotalBytes < 1024 * 1024) return $"{TotalBytes / 1024.0:F1} KB";
            if (TotalBytes < 1024 * 1024 * 1024) return $"{TotalBytes / (1024.0 * 1024.0):F1} MB";
            return $"{TotalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}

/// <summary>
/// Detailed copy operation statistics.
/// </summary>
public class CopyCompletionStats
{
    public int TotalFilesCopied { get; set; }
    public long TotalBytesCopied { get; set; }
    public int FilesVerified { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public List<ExtensionStats> ExtensionBreakdown { get; set; } = new();

    /// <summary>
    /// Average time per file in milliseconds.
    /// </summary>
    public double MillisecondsPerFile => TotalFilesCopied > 0 ? TotalDuration.TotalMilliseconds / TotalFilesCopied : 0;

    /// <summary>
    /// Files copied per second.
    /// </summary>
    public double FilesPerSecond => TotalDuration.TotalSeconds > 0 ? TotalFilesCopied / TotalDuration.TotalSeconds : 0;

    /// <summary>
    /// Average MB per second.
    /// </summary>
    public double MBPerSecond => TotalDuration.TotalSeconds > 0 ? (TotalBytesCopied / 1024.0 / 1024.0) / TotalDuration.TotalSeconds : 0;

    /// <summary>
    /// Formatted total size.
    /// </summary>
    public string TotalSizeFormatted
    {
        get
        {
            if (TotalBytesCopied < 1024) return $"{TotalBytesCopied} B";
            if (TotalBytesCopied < 1024 * 1024) return $"{TotalBytesCopied / 1024.0:F1} KB";
            if (TotalBytesCopied < 1024 * 1024 * 1024) return $"{TotalBytesCopied / (1024.0 * 1024.0):F1} MB";
            return $"{TotalBytesCopied / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    /// <summary>
    /// Formatted duration string.
    /// </summary>
    public string DurationFormatted
    {
        get
        {
            if (TotalDuration.TotalHours >= 1)
                return $"{(int)TotalDuration.TotalHours}h {TotalDuration.Minutes}m {TotalDuration.Seconds}s";
            if (TotalDuration.TotalMinutes >= 1)
                return $"{TotalDuration.Minutes}m {TotalDuration.Seconds}s";
            return $"{TotalDuration.Seconds}.{TotalDuration.Milliseconds / 100}s";
        }
    }

    /// <summary>
    /// Formatted time per file.
    /// </summary>
    public string TimePerFileFormatted
    {
        get
        {
            var ms = MillisecondsPerFile;
            if (ms >= 1000)
                return $"{ms / 1000.0:F2}s";
            return $"{ms:F1}ms";
        }
    }
}
