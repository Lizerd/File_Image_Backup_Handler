using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Data.Repositories;

/// <summary>
/// Repository for ScanRoot entities.
/// </summary>
public class ScanRootRepository
{
    private readonly DatabaseContext _context;
    private readonly ILogger<ScanRootRepository> _logger;

    public ScanRootRepository(DatabaseContext context, ILogger<ScanRootRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets all scan roots for the current project.
    /// </summary>
    public async Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Path, Label, RootType, IsEnabled, LastScanUtc, FileCount, TotalBytes, AddedUtc
            FROM ScanRoots
            ORDER BY Path";

        var results = new List<ScanRoot>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapScanRoot(reader));
        }
        return results;
    }

    /// <summary>
    /// Gets a scan root by ID.
    /// </summary>
    public async Task<ScanRoot?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Path, Label, RootType, IsEnabled, LastScanUtc, FileCount, TotalBytes, AddedUtc
            FROM ScanRoots
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapScanRoot(reader);
        }
        return null;
    }

    /// <summary>
    /// Gets enabled scan roots.
    /// </summary>
    public async Task<IReadOnlyList<ScanRoot>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Path, Label, RootType, IsEnabled, LastScanUtc, FileCount, TotalBytes, AddedUtc
            FROM ScanRoots
            WHERE IsEnabled = 1
            ORDER BY Path";

        var results = new List<ScanRoot>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapScanRoot(reader));
        }
        return results;
    }

    /// <summary>
    /// Adds a new scan root.
    /// </summary>
    public async Task<ScanRoot> AddAsync(string path, string? label = null, CancellationToken cancellationToken = default)
    {
        var rootType = DetectRootType(path);
        var actualLabel = label ?? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(actualLabel))
        {
            actualLabel = path; // For drive roots like "C:\"
        }

        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ScanRoots (Path, Label, RootType, IsEnabled, AddedUtc)
            VALUES (@Path, @Label, @RootType, 1, @AddedUtc);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@Path", path);
        cmd.Parameters.AddWithValue("@Label", actualLabel);
        cmd.Parameters.AddWithValue("@RootType", (int)rootType);
        cmd.Parameters.AddWithValue("@AddedUtc", DateTime.UtcNow.ToString("o"));

        var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        _logger.LogInformation("Added scan root: {Path} (ID: {Id})", path, id);

        return new ScanRoot
        {
            Id = id,
            Path = path,
            Label = actualLabel,
            RootType = rootType,
            IsEnabled = true,
            AddedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Updates scan statistics for a root.
    /// </summary>
    public async Task UpdateStatsAsync(long id, int fileCount, long totalBytes, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE ScanRoots
            SET FileCount = @FileCount, TotalBytes = @TotalBytes, LastScanUtc = @LastScanUtc
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@FileCount", fileCount);
        cmd.Parameters.AddWithValue("@TotalBytes", totalBytes);
        cmd.Parameters.AddWithValue("@LastScanUtc", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Sets the enabled state for a scan root.
    /// </summary>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ScanRoots SET IsEnabled = @IsEnabled WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@IsEnabled", enabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Removes a scan root.
    /// </summary>
    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ScanRoots WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Removed scan root ID: {Id}", id);
    }

    /// <summary>
    /// Checks if a path is already a scan root or contained within one.
    /// </summary>
    public async Task<bool> IsPathCoveredAsync(string path, CancellationToken cancellationToken = default)
    {
        var roots = await GetAllAsync(cancellationToken);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var root in roots)
        {
            var normalizedRoot = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);

            // Check if paths are the same
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check if new path is inside an existing root
            if (normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static RootType DetectRootType(string path)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);
            return driveInfo.DriveType switch
            {
                DriveType.Fixed => RootType.Fixed,
                DriveType.Removable => RootType.Removable,
                DriveType.Network => RootType.Network,
                DriveType.CDRom => RootType.Optical,
                _ => RootType.Fixed
            };
        }
        catch
        {
            return RootType.Fixed;
        }
    }

    private static ScanRoot MapScanRoot(SqliteDataReader reader)
    {
        return new ScanRoot
        {
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            Label = reader.GetString(2),
            RootType = (RootType)reader.GetInt32(3),
            IsEnabled = reader.GetInt32(4) == 1,
            LastScanUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
            FileCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            TotalBytes = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
            AddedUtc = DateTime.Parse(reader.GetString(8))
        };
    }
}
