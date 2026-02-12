using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Services.Implementation;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Data.Repositories;

/// <summary>
/// Repository for FolderNode entities with hierarchy support.
/// Optimized for lazy-loading tree structures.
/// </summary>
public class FolderNodeRepository
{
    private readonly ProjectService _projectService;
    private readonly ILogger<FolderNodeRepository> _logger;

    public FolderNodeRepository(IProjectService projectService, ILogger<FolderNodeRepository> logger)
    {
        _projectService = (ProjectService)projectService;
        _logger = logger;
    }

    private DatabaseContext Context => _projectService.GetContext();

    /// <summary>
    /// Gets all root folder nodes (ParentId IS NULL).
    /// </summary>
    public async Task<IReadOnlyList<FolderNode>> GetRootNodesAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ParentId, DisplayName, ProposedRelativePath, UserEditedName,
                   CopyEnabled, UniqueCount, DuplicateCount, TotalSizeBytes, WhyExplanation
            FROM FolderNodes
            WHERE ParentId IS NULL
            ORDER BY DisplayName";

        var results = new List<FolderNode>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapFolderNode(reader));
        }

        _logger.LogDebug("Loaded {Count} root folder nodes", results.Count);
        return results;
    }

    /// <summary>
    /// Gets child folder nodes for a parent.
    /// </summary>
    public async Task<IReadOnlyList<FolderNode>> GetChildrenAsync(long parentId, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ParentId, DisplayName, ProposedRelativePath, UserEditedName,
                   CopyEnabled, UniqueCount, DuplicateCount, TotalSizeBytes, WhyExplanation
            FROM FolderNodes
            WHERE ParentId = @ParentId
            ORDER BY DisplayName";
        cmd.Parameters.AddWithValue("@ParentId", parentId);

        var results = new List<FolderNode>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapFolderNode(reader));
        }
        return results;
    }

    /// <summary>
    /// Checks if a folder has any children.
    /// </summary>
    public async Task<bool> HasChildrenAsync(long folderId, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM FolderNodes WHERE ParentId = @ParentId)";
        cmd.Parameters.AddWithValue("@ParentId", folderId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result) == 1;
    }

    /// <summary>
    /// Gets a folder node by ID.
    /// </summary>
    public async Task<FolderNode?> GetByIdAsync(long folderId, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ParentId, DisplayName, ProposedRelativePath, UserEditedName,
                   CopyEnabled, UniqueCount, DuplicateCount, TotalSizeBytes, WhyExplanation
            FROM FolderNodes
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", folderId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapFolderNode(reader);
        }
        return null;
    }

    /// <summary>
    /// Inserts a single folder node and returns its ID.
    /// </summary>
    public async Task<long> InsertAsync(FolderNode node, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO FolderNodes (ParentId, DisplayName, ProposedRelativePath, CopyEnabled, UniqueCount, TotalSizeBytes, WhyExplanation)
            VALUES (@ParentId, @DisplayName, @ProposedRelativePath, @CopyEnabled, @UniqueCount, @TotalSizeBytes, @WhyExplanation);
            SELECT last_insert_rowid();";

        cmd.Parameters.AddWithValue("@ParentId", node.ParentFolderNodeId.HasValue ? node.ParentFolderNodeId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayName", node.DisplayName);
        cmd.Parameters.AddWithValue("@ProposedRelativePath", node.ProposedRelativePath);
        cmd.Parameters.AddWithValue("@CopyEnabled", node.CopyEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@UniqueCount", node.UniqueCount);
        cmd.Parameters.AddWithValue("@TotalSizeBytes", node.TotalSizeBytes);
        cmd.Parameters.AddWithValue("@WhyExplanation", node.WhyExplanation ?? (object)DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Updates a folder node's display name and copy enabled status.
    /// </summary>
    public async Task UpdateAsync(FolderNode node, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE FolderNodes
            SET UserEditedName = @UserEditedName, CopyEnabled = @CopyEnabled
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", node.FolderNodeId);
        cmd.Parameters.AddWithValue("@UserEditedName", node.UserEditedName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@CopyEnabled", node.CopyEnabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Updates the file counts for a folder.
    /// </summary>
    public async Task UpdateCountsAsync(long folderId, int uniqueCount, long totalSizeBytes, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE FolderNodes
            SET UniqueCount = @UniqueCount, TotalSizeBytes = @TotalSizeBytes
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", folderId);
        cmd.Parameters.AddWithValue("@UniqueCount", uniqueCount);
        cmd.Parameters.AddWithValue("@TotalSizeBytes", totalSizeBytes);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes all folder nodes (for plan regeneration).
    /// </summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM FolderNodes";
        var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Cleared {Count} folder nodes", deleted);
    }

    /// <summary>
    /// Gets the total count of folder nodes.
    /// </summary>
    public async Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM FolderNodes";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Gets folder nodes by path prefix (for finding existing folders).
    /// </summary>
    public async Task<FolderNode?> GetByPathAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ParentId, DisplayName, ProposedRelativePath, UserEditedName,
                   CopyEnabled, UniqueCount, DuplicateCount, TotalSizeBytes, WhyExplanation
            FROM FolderNodes
            WHERE ProposedRelativePath = @Path";
        cmd.Parameters.AddWithValue("@Path", relativePath);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapFolderNode(reader);
        }
        return null;
    }

    /// <summary>
    /// Gets the total size of all enabled folders (where CopyEnabled = 1).
    /// </summary>
    public async Task<long> GetTotalEnabledSizeAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(TotalSizeBytes), 0) FROM FolderNodes WHERE CopyEnabled = 1";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Gets the total file count of all enabled folders.
    /// </summary>
    public async Task<int> GetTotalEnabledFileCountAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(UniqueCount), 0) FROM FolderNodes WHERE CopyEnabled = 1";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Updates the CopyEnabled status for a folder.
    /// </summary>
    public async Task UpdateCopyEnabledAsync(long folderId, bool enabled, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE FolderNodes SET CopyEnabled = @Enabled WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", folderId);
        cmd.Parameters.AddWithValue("@Enabled", enabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Updated folder {Id} CopyEnabled to {Enabled}", folderId, enabled);
    }

    /// <summary>
    /// Updates the CopyEnabled status for a folder and all its descendants.
    /// </summary>
    public async Task UpdateCopyEnabledCascadeAsync(long folderId, bool enabled, CancellationToken cancellationToken = default)
    {
        var connection = await Context.GetConnectionAsync();

        // Use a recursive CTE to find all descendant folders
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            WITH RECURSIVE descendants AS (
                SELECT Id FROM FolderNodes WHERE Id = @Id
                UNION ALL
                SELECT fn.Id FROM FolderNodes fn
                INNER JOIN descendants d ON fn.ParentId = d.Id
            )
            UPDATE FolderNodes SET CopyEnabled = @Enabled WHERE Id IN (SELECT Id FROM descendants)";
        cmd.Parameters.AddWithValue("@Id", folderId);
        cmd.Parameters.AddWithValue("@Enabled", enabled ? 1 : 0);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Updated {Count} folders (including descendants) CopyEnabled to {Enabled}", affected, enabled);
    }

    private static FolderNode MapFolderNode(SqliteDataReader reader)
    {
        return new FolderNode
        {
            FolderNodeId = reader.GetInt64(0),
            ParentFolderNodeId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            DisplayName = reader.GetString(2),
            ProposedRelativePath = reader.GetString(3),
            UserEditedName = reader.IsDBNull(4) ? null : reader.GetString(4),
            CopyEnabled = reader.GetInt32(5) == 1,
            UniqueCount = reader.GetInt32(6),
            DuplicateCount = reader.GetInt32(7),
            TotalSizeBytes = reader.GetInt64(8),
            WhyExplanation = reader.IsDBNull(9) ? null : reader.GetString(9)
        };
    }
}
