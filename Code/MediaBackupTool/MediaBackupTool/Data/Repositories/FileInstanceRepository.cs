using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Data.Repositories;

/// <summary>
/// Repository for FileInstance entities with batch insert support.
/// Optimized for handling millions of records.
/// </summary>
public class FileInstanceRepository
{
    private readonly DatabaseContext _context;
    private readonly ILogger<FileInstanceRepository> _logger;
    private const int BatchSize = 10000;

    public FileInstanceRepository(DatabaseContext context, ILogger<FileInstanceRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Inserts file instances in batches for optimal performance.
    /// Uses prepared statements and transactions.
    /// </summary>
    public async Task<int> InsertBatchAsync(IEnumerable<FileInstance> files, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        var insertedCount = 0;
        var batch = new List<FileInstance>(BatchSize);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Add(file);
            if (batch.Count >= BatchSize)
            {
                insertedCount += await InsertBatchInternalAsync(connection, batch, cancellationToken);
                batch.Clear();
            }
        }

        // Insert remaining files
        if (batch.Count > 0)
        {
            insertedCount += await InsertBatchInternalAsync(connection, batch, cancellationToken);
        }

        return insertedCount;
    }

    private async Task<int> InsertBatchInternalAsync(SqliteConnection connection, List<FileInstance> batch, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            // Use INSERT OR IGNORE to silently skip duplicates if unique constraint is violated
            cmd.CommandText = @"
                INSERT OR IGNORE INTO FileInstances (ScanRootId, RelativePath, FileName, Extension, SizeBytes,
                    ModifiedUtc, Status, Category, DiscoveredUtc)
                VALUES (@ScanRootId, @RelativePath, @FileName, @Extension, @SizeBytes,
                    @ModifiedUtc, @Status, @Category, @DiscoveredUtc)";

            var scanRootIdParam = cmd.Parameters.Add("@ScanRootId", SqliteType.Integer);
            var relativePathParam = cmd.Parameters.Add("@RelativePath", SqliteType.Text);
            var fileNameParam = cmd.Parameters.Add("@FileName", SqliteType.Text);
            var extensionParam = cmd.Parameters.Add("@Extension", SqliteType.Text);
            var sizeBytesParam = cmd.Parameters.Add("@SizeBytes", SqliteType.Integer);
            var modifiedUtcParam = cmd.Parameters.Add("@ModifiedUtc", SqliteType.Text);
            var statusParam = cmd.Parameters.Add("@Status", SqliteType.Integer);
            var categoryParam = cmd.Parameters.Add("@Category", SqliteType.Integer);
            var discoveredUtcParam = cmd.Parameters.Add("@DiscoveredUtc", SqliteType.Text);

            foreach (var file in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanRootIdParam.Value = file.ScanRootId;
                relativePathParam.Value = file.RelativePath;
                fileNameParam.Value = file.FileName;
                extensionParam.Value = file.Extension;
                sizeBytesParam.Value = file.SizeBytes;
                modifiedUtcParam.Value = file.ModifiedUtc.ToString("o");
                statusParam.Value = (int)file.Status;
                categoryParam.Value = (int)file.Category;
                discoveredUtcParam.Value = file.DiscoveredUtc.ToString("o");

                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _logger.LogDebug("Inserted batch of {Count} file instances", batch.Count);
            return batch.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert batch of {Count} files", batch.Count);
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Gets the total count of file instances for a scan root.
    /// </summary>
    public async Task<int> GetCountByScanRootAsync(long scanRootId, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM FileInstances WHERE ScanRootId = @ScanRootId";
        cmd.Parameters.AddWithValue("@ScanRootId", scanRootId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Gets the total count of all file instances.
    /// </summary>
    public async Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM FileInstances";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Gets file instances by status with pagination.
    /// </summary>
    public async Task<IReadOnlyList<FileInstance>> GetByStatusAsync(
        FileStatus status,
        int offset = 0,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ScanRootId, RelativePath, FileName, Extension, SizeBytes,
                   ModifiedUtc, Status, Category, HashId, DiscoveredUtc, ErrorMessage
            FROM FileInstances
            WHERE Status = @Status
            ORDER BY Id
            LIMIT @Limit OFFSET @Offset";

        cmd.Parameters.AddWithValue("@Status", (int)status);
        cmd.Parameters.AddWithValue("@Limit", limit);
        cmd.Parameters.AddWithValue("@Offset", offset);

        var results = new List<FileInstance>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapFileInstance(reader));
        }
        return results;
    }

    /// <summary>
    /// Updates the status of a file instance.
    /// </summary>
    public async Task UpdateStatusAsync(long fileId, FileStatus status, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE FileInstances
            SET Status = @Status, ErrorMessage = @ErrorMessage
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", fileId);
        cmd.Parameters.AddWithValue("@Status", (int)status);
        cmd.Parameters.AddWithValue("@ErrorMessage", errorMessage ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Updates the hash reference for a file instance.
    /// </summary>
    public async Task UpdateHashAsync(long fileId, long hashId, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE FileInstances
            SET HashId = @HashId, Status = @Status
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", fileId);
        cmd.Parameters.AddWithValue("@HashId", hashId);
        cmd.Parameters.AddWithValue("@Status", (int)FileStatus.Hashed);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes all file instances for a scan root.
    /// </summary>
    public async Task DeleteByScanRootAsync(long scanRootId, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM FileInstances WHERE ScanRootId = @ScanRootId";
        cmd.Parameters.AddWithValue("@ScanRootId", scanRootId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Gets statistics for file instances.
    /// </summary>
    public async Task<FileInstanceStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) as TotalFiles,
                COALESCE(SUM(SizeBytes), 0) as TotalBytes,
                COUNT(CASE WHEN Status = @Discovered THEN 1 END) as DiscoveredCount,
                COUNT(CASE WHEN Status = @Hashed THEN 1 END) as HashedCount,
                COUNT(CASE WHEN Status = @Error THEN 1 END) as ErrorCount
            FROM FileInstances";
        cmd.Parameters.AddWithValue("@Discovered", (int)FileStatus.Discovered);
        cmd.Parameters.AddWithValue("@Hashed", (int)FileStatus.Hashed);
        cmd.Parameters.AddWithValue("@Error", (int)FileStatus.Error);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new FileInstanceStats
            {
                TotalFiles = reader.GetInt32(0),
                TotalBytes = reader.GetInt64(1),
                DiscoveredCount = reader.GetInt32(2),
                HashedCount = reader.GetInt32(3),
                ErrorCount = reader.GetInt32(4)
            };
        }
        return new FileInstanceStats();
    }

    private static FileInstance MapFileInstance(SqliteDataReader reader)
    {
        return new FileInstance
        {
            Id = reader.GetInt64(0),
            ScanRootId = reader.GetInt64(1),
            RelativePath = reader.GetString(2),
            FileName = reader.GetString(3),
            Extension = reader.GetString(4),
            SizeBytes = reader.GetInt64(5),
            ModifiedUtc = DateTime.Parse(reader.GetString(6)),
            Status = (FileStatus)reader.GetInt32(7),
            Category = (FileTypeCategory)reader.GetInt32(8),
            HashId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            DiscoveredUtc = DateTime.Parse(reader.GetString(10)),
            ErrorMessage = reader.IsDBNull(11) ? null : reader.GetString(11)
        };
    }
}

/// <summary>
/// Statistics for file instances.
/// </summary>
public class FileInstanceStats
{
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public int DiscoveredCount { get; set; }
    public int HashedCount { get; set; }
    public int ErrorCount { get; set; }
}
