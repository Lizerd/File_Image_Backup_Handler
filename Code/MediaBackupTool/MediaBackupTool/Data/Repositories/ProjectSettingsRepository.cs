using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Data.Repositories;

/// <summary>
/// Repository for ProjectSettings entity.
/// </summary>
public class ProjectSettingsRepository
{
    private readonly DatabaseContext _context;
    private readonly ILogger<ProjectSettingsRepository> _logger;

    public ProjectSettingsRepository(DatabaseContext context, ILogger<ProjectSettingsRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets the project settings. Creates default settings if none exist.
    /// </summary>
    public async Task<ProjectSettings> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetOrCreateAsync called");

        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ProjectName, HashLevel, CpuProfile, TargetPath, CurrentState,
                   VerifyByDefault, ArchiveScanningEnabled, ArchiveMaxSizeMB, ArchiveNestedEnabled, ArchiveMaxDepth,
                   MovieHashChunkSizeMB, EnabledCategories, CreatedUtc, LastModifiedUtc
            FROM ProjectSettings
            LIMIT 1";

        try
        {
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                _logger.LogDebug("Found existing project settings");
                return MapSettings(reader);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading project settings from database");
            throw;
        }

        // Create default settings
        _logger.LogDebug("No existing settings found, creating defaults");
        return await CreateDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Updates project settings.
    /// </summary>
    public async Task UpdateAsync(ProjectSettings settings, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE ProjectSettings SET
                ProjectName = @ProjectName,
                HashLevel = @HashLevel,
                CpuProfile = @CpuProfile,
                TargetPath = @TargetPath,
                CurrentState = @CurrentState,
                VerifyByDefault = @VerifyByDefault,
                ArchiveScanningEnabled = @ArchiveScanningEnabled,
                ArchiveMaxSizeMB = @ArchiveMaxSizeMB,
                ArchiveNestedEnabled = @ArchiveNestedEnabled,
                ArchiveMaxDepth = @ArchiveMaxDepth,
                MovieHashChunkSizeMB = @MovieHashChunkSizeMB,
                EnabledCategories = @EnabledCategories,
                LastModifiedUtc = @LastModifiedUtc
            WHERE Id = @Id";

        cmd.Parameters.AddWithValue("@Id", settings.Id);
        cmd.Parameters.AddWithValue("@ProjectName", settings.ProjectName);
        cmd.Parameters.AddWithValue("@HashLevel", (int)settings.HashLevel);
        cmd.Parameters.AddWithValue("@CpuProfile", (int)settings.CpuProfile);
        cmd.Parameters.AddWithValue("@TargetPath", settings.TargetPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@CurrentState", (int)settings.CurrentState);
        cmd.Parameters.AddWithValue("@VerifyByDefault", settings.VerifyByDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("@ArchiveScanningEnabled", settings.ArchiveScanningEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@ArchiveMaxSizeMB", settings.ArchiveMaxSizeMB);
        cmd.Parameters.AddWithValue("@ArchiveNestedEnabled", settings.ArchiveNestedEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@ArchiveMaxDepth", settings.ArchiveMaxDepth);
        cmd.Parameters.AddWithValue("@MovieHashChunkSizeMB", settings.MovieHashChunkSizeMB);
        cmd.Parameters.AddWithValue("@EnabledCategories", settings.EnabledCategories);
        cmd.Parameters.AddWithValue("@LastModifiedUtc", DateTime.UtcNow.ToString("o"));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Updated project settings");
    }

    /// <summary>
    /// Updates only the current state.
    /// </summary>
    public async Task UpdateStateAsync(AppState state, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE ProjectSettings SET CurrentState = @CurrentState, LastModifiedUtc = @LastModifiedUtc";
        cmd.Parameters.AddWithValue("@CurrentState", (int)state);
        cmd.Parameters.AddWithValue("@LastModifiedUtc", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ProjectSettings> CreateDefaultAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("CreateDefaultAsync called");

        var connection = await _context.GetConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ProjectSettings (ProjectName, HashLevel, CpuProfile, CurrentState,
                VerifyByDefault, ArchiveScanningEnabled, ArchiveMaxSizeMB, ArchiveNestedEnabled, ArchiveMaxDepth,
                MovieHashChunkSizeMB, EnabledCategories, CreatedUtc, LastModifiedUtc)
            VALUES (@ProjectName, @HashLevel, @CpuProfile, @CurrentState,
                @VerifyByDefault, @ArchiveScanningEnabled, @ArchiveMaxSizeMB, @ArchiveNestedEnabled, @ArchiveMaxDepth,
                @MovieHashChunkSizeMB, @EnabledCategories, @CreatedUtc, @LastModifiedUtc);
            SELECT last_insert_rowid();";

        var settings = new ProjectSettings
        {
            ProjectName = "New Project",
            HashLevel = HashLevel.SHA256,
            CpuProfile = CpuProfile.Balanced,
            CurrentState = AppState.Idle,
            VerifyByDefault = true,
            ArchiveScanningEnabled = false,
            ArchiveMaxSizeMB = 500,
            ArchiveNestedEnabled = false,
            ArchiveMaxDepth = 3,
            MovieHashChunkSizeMB = 64,
            EnabledCategories = "Image",
            CreatedUtc = DateTime.UtcNow,
            LastModifiedUtc = DateTime.UtcNow
        };

        cmd.Parameters.AddWithValue("@ProjectName", settings.ProjectName);
        cmd.Parameters.AddWithValue("@HashLevel", (int)settings.HashLevel);
        cmd.Parameters.AddWithValue("@CpuProfile", (int)settings.CpuProfile);
        cmd.Parameters.AddWithValue("@CurrentState", (int)settings.CurrentState);
        cmd.Parameters.AddWithValue("@VerifyByDefault", settings.VerifyByDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("@ArchiveScanningEnabled", settings.ArchiveScanningEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@ArchiveMaxSizeMB", settings.ArchiveMaxSizeMB);
        cmd.Parameters.AddWithValue("@ArchiveNestedEnabled", settings.ArchiveNestedEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@ArchiveMaxDepth", settings.ArchiveMaxDepth);
        cmd.Parameters.AddWithValue("@MovieHashChunkSizeMB", settings.MovieHashChunkSizeMB);
        cmd.Parameters.AddWithValue("@EnabledCategories", settings.EnabledCategories);
        cmd.Parameters.AddWithValue("@CreatedUtc", settings.CreatedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@LastModifiedUtc", settings.LastModifiedUtc.ToString("o"));

        _logger.LogDebug("Executing INSERT for default settings...");

        try
        {
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result == null)
            {
                _logger.LogError("ExecuteScalarAsync returned null - INSERT may have failed");
                throw new InvalidOperationException("Failed to insert default project settings");
            }
            settings.Id = (long)result;
            _logger.LogInformation("Created default project settings with ID: {Id}", settings.Id);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create default project settings");
            throw;
        }
    }

    private static ProjectSettings MapSettings(SqliteDataReader reader)
    {
        return new ProjectSettings
        {
            Id = reader.GetInt64(0),
            ProjectName = reader.GetString(1),
            HashLevel = (HashLevel)reader.GetInt32(2),
            CpuProfile = (CpuProfile)reader.GetInt32(3),
            TargetPath = reader.IsDBNull(4) ? null : reader.GetString(4),
            CurrentState = (AppState)reader.GetInt32(5),
            VerifyByDefault = reader.GetInt32(6) == 1,
            ArchiveScanningEnabled = reader.GetInt32(7) == 1,
            ArchiveMaxSizeMB = reader.GetInt32(8),
            ArchiveNestedEnabled = reader.GetInt32(9) == 1,
            ArchiveMaxDepth = reader.GetInt32(10),
            MovieHashChunkSizeMB = reader.GetInt32(11),
            EnabledCategories = reader.GetString(12),
            CreatedUtc = DateTime.Parse(reader.GetString(13)),
            LastModifiedUtc = DateTime.Parse(reader.GetString(14))
        };
    }
}
