using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Implementation;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Data.Repositories;

/// <summary>
/// Repository for UniqueFile entities with duplicate tracking support.
/// Provides paginated access for large datasets.
/// </summary>
public class UniqueFileRepository
{
    private readonly ProjectService _projectService;
    private readonly ILogger<UniqueFileRepository> _logger;
    private const int DefaultPageSize = 100;

    public UniqueFileRepository(IProjectService projectService, ILogger<UniqueFileRepository> logger)
    {
        _projectService = (ProjectService)projectService;
        _logger = logger;
    }

    private DatabaseContext Context => _projectService.GetContext();

    /// <summary>
    /// Gets unique files for a folder with pagination.
    /// Includes representative file info for display.
    /// </summary>
    public async Task<IReadOnlyList<UniqueFileDetail>> GetByFolderIdAsync(
        long folderId,
        int offset = 0,
        int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                uf.Id, uf.HashId, uf.RepresentativeFileInstanceId, uf.FileTypeCategory,
                uf.CopyEnabled, uf.PlannedFolderNodeId, uf.PlannedFileName, uf.DuplicateCount,
                fi.FileName, fi.Extension, fi.SizeBytes, fi.RelativePath,
                sr.Path as ScanRootPath,
                h.HashHex
            FROM UniqueFiles uf
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            INNER JOIN Hashes h ON h.Id = uf.HashId
            WHERE uf.PlannedFolderNodeId = @FolderId
            ORDER BY fi.FileName
            LIMIT @Limit OFFSET @Offset";

        cmd.Parameters.AddWithValue("@FolderId", folderId);
        cmd.Parameters.AddWithValue("@Limit", limit);
        cmd.Parameters.AddWithValue("@Offset", offset);

        var results = new List<UniqueFileDetail>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapUniqueFileDetail(reader));
        }
        return results;
    }

    /// <summary>
    /// Gets all unique files with pagination (no folder filter).
    /// </summary>
    public async Task<IReadOnlyList<UniqueFileDetail>> GetAllAsync(
        int offset = 0,
        int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                uf.Id, uf.HashId, uf.RepresentativeFileInstanceId, uf.FileTypeCategory,
                uf.CopyEnabled, uf.PlannedFolderNodeId, uf.PlannedFileName, uf.DuplicateCount,
                fi.FileName, fi.Extension, fi.SizeBytes, fi.RelativePath,
                sr.Path as ScanRootPath,
                h.HashHex
            FROM UniqueFiles uf
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            INNER JOIN Hashes h ON h.Id = uf.HashId
            ORDER BY fi.FileName
            LIMIT @Limit OFFSET @Offset";

        cmd.Parameters.AddWithValue("@Limit", limit);
        cmd.Parameters.AddWithValue("@Offset", offset);

        var results = new List<UniqueFileDetail>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapUniqueFileDetail(reader));
        }
        return results;
    }

    /// <summary>
    /// Gets the total count of unique files in a folder.
    /// </summary>
    public async Task<int> GetCountByFolderIdAsync(long folderId, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM UniqueFiles WHERE PlannedFolderNodeId = @FolderId";
        cmd.Parameters.AddWithValue("@FolderId", folderId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Gets the total count of all unique files.
    /// </summary>
    public async Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM UniqueFiles";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Gets a unique file by ID with full details.
    /// </summary>
    public async Task<UniqueFileDetail?> GetByIdAsync(long uniqueFileId, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                uf.Id, uf.HashId, uf.RepresentativeFileInstanceId, uf.FileTypeCategory,
                uf.CopyEnabled, uf.PlannedFolderNodeId, uf.PlannedFileName, uf.DuplicateCount,
                fi.FileName, fi.Extension, fi.SizeBytes, fi.RelativePath,
                sr.Path as ScanRootPath,
                h.HashHex
            FROM UniqueFiles uf
            INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            INNER JOIN Hashes h ON h.Id = uf.HashId
            WHERE uf.Id = @Id";
        cmd.Parameters.AddWithValue("@Id", uniqueFileId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapUniqueFileDetail(reader);
        }
        return null;
    }

    /// <summary>
    /// Gets all FileInstance locations that share the same hash (duplicates).
    /// </summary>
    public async Task<IReadOnlyList<DuplicateInstance>> GetDuplicateInstancesAsync(
        long hashId,
        CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                fi.Id, fi.FileName, fi.RelativePath, fi.SizeBytes, fi.ModifiedUtc,
                sr.Path as ScanRootPath, sr.Label as ScanRootLabel
            FROM FileInstances fi
            INNER JOIN ScanRoots sr ON sr.Id = fi.ScanRootId
            WHERE fi.HashId = @HashId
            ORDER BY sr.Label, fi.RelativePath";
        cmd.Parameters.AddWithValue("@HashId", hashId);

        var results = new List<DuplicateInstance>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DuplicateInstance
            {
                FileInstanceId = reader.GetInt64(0),
                FileName = reader.GetString(1),
                RelativePath = reader.GetString(2),
                SizeBytes = reader.GetInt64(3),
                ModifiedUtc = DateTime.Parse(reader.GetString(4)),
                ScanRootPath = reader.GetString(5),
                ScanRootLabel = reader.GetString(6),
                FullPath = Path.Combine(reader.GetString(5), reader.GetString(2))
            });
        }
        return results;
    }

    /// <summary>
    /// Updates the CopyEnabled flag for a unique file.
    /// </summary>
    public async Task UpdateCopyEnabledAsync(long uniqueFileId, bool copyEnabled, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE UniqueFiles SET CopyEnabled = @CopyEnabled WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", uniqueFileId);
        cmd.Parameters.AddWithValue("@CopyEnabled", copyEnabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Updates the planned folder assignment for a unique file.
    /// </summary>
    public async Task UpdatePlannedFolderAsync(long uniqueFileId, long folderId, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE UniqueFiles SET PlannedFolderNodeId = @FolderId WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", uniqueFileId);
        cmd.Parameters.AddWithValue("@FolderId", folderId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Clears all folder assignments (for plan regeneration).
    /// </summary>
    public async Task ClearAllFolderAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE UniqueFiles SET PlannedFolderNodeId = NULL, PlannedFileName = NULL";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Cleared all folder assignments");
    }

    private static UniqueFileDetail MapUniqueFileDetail(SqliteDataReader reader)
    {
        var scanRootPath = reader.GetString(12);
        var relativePath = reader.GetString(11);

        return new UniqueFileDetail
        {
            UniqueFileId = reader.GetInt64(0),
            HashId = reader.GetInt64(1),
            RepresentativeFileInstanceId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            FileTypeCategory = (FileTypeCategory)reader.GetInt32(3),
            CopyEnabled = reader.GetInt32(4) == 1,
            PlannedFolderNodeId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            PlannedFileName = reader.IsDBNull(6) ? null : reader.GetString(6),
            DuplicateCount = reader.GetInt32(7),
            FileName = reader.GetString(8),
            Extension = reader.GetString(9),
            SizeBytes = reader.GetInt64(10),
            RelativePath = relativePath,
            ScanRootPath = scanRootPath,
            HashHex = reader.GetString(13),
            FullPath = Path.Combine(scanRootPath, relativePath)
        };
    }
}

/// <summary>
/// Extended unique file info including representative file details.
/// </summary>
public class UniqueFileDetail
{
    public long UniqueFileId { get; set; }
    public long HashId { get; set; }
    public long? RepresentativeFileInstanceId { get; set; }
    public FileTypeCategory FileTypeCategory { get; set; }
    public bool CopyEnabled { get; set; }
    public long? PlannedFolderNodeId { get; set; }
    public string? PlannedFileName { get; set; }
    public int DuplicateCount { get; set; }

    // Representative file info
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string ScanRootPath { get; set; } = string.Empty;
    public string HashHex { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets a human-readable size string.
    /// </summary>
    public string SizeFormatted
    {
        get
        {
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
            if (SizeBytes < 1024 * 1024 * 1024) return $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}

/// <summary>
/// Represents a duplicate file instance location.
/// </summary>
public class DuplicateInstance
{
    public long FileInstanceId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string ScanRootPath { get; set; } = string.Empty;
    public string ScanRootLabel { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// Display text showing source location.
    /// </summary>
    public string DisplayPath => $"{ScanRootLabel}: {RelativePath}";
}
