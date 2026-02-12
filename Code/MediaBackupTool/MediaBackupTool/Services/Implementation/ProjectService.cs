using Microsoft.Extensions.Logging;
using MediaBackupTool.Data;
using MediaBackupTool.Data.Repositories;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Service for managing project files and settings.
/// </summary>
public class ProjectService : IProjectService
{
    private readonly Func<string, DatabaseContext> _contextFactory;
    private readonly ILogger<ProjectService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private DatabaseContext? _context;
    private ScanRootRepository? _scanRootRepo;
    private ProjectSettingsRepository? _settingsRepo;
    private ProjectSettings? _currentSettings;

    public bool IsProjectOpen => _context != null;
    public string? CurrentProjectPath { get; private set; }
    public ProjectSettings? CurrentSettings => _currentSettings;

    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    public ProjectService(Func<string, DatabaseContext> contextFactory, ILogger<ProjectService> logger, ILoggerFactory loggerFactory)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task CreateProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("CreateProjectAsync called with path: {Path}", projectPath);

        // Close any existing project
        await CloseProjectAsync();
        _logger.LogDebug("Closed any existing project");

        // Ensure directory exists
        var directory = Path.GetDirectoryName(projectPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogDebug("Ensured directory exists: {Directory}", directory);
        }

        // Delete existing file if present
        if (File.Exists(projectPath))
        {
            File.Delete(projectPath);
            _logger.LogDebug("Deleted existing database file");
        }

        _logger.LogInformation("Creating new project database: {Path}", projectPath);

        try
        {
            // Create and initialize database
            _context = _contextFactory(projectPath);
            _logger.LogDebug("Created database context");

            await _context.InitializeAsync();
            _logger.LogDebug("Database initialized");

            CurrentProjectPath = projectPath;
            InitializeRepositories();
            _logger.LogDebug("Repositories initialized");

            // Create default settings
            _logger.LogDebug("Creating default settings...");
            _currentSettings = await _settingsRepo!.GetOrCreateAsync(cancellationToken);
            _logger.LogDebug("Default settings created with ID: {Id}", _currentSettings?.Id);

            OnProjectChanged(true, projectPath);
            _logger.LogInformation("Project created successfully at {Path}", projectPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create project at {Path}", projectPath);
            throw;
        }
    }

    public async Task OpenProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Project file not found", projectPath);
        }

        // Close any existing project
        await CloseProjectAsync();

        _logger.LogInformation("Opening project: {Path}", projectPath);

        _context = _contextFactory(projectPath);
        await _context.InitializeAsync();

        CurrentProjectPath = projectPath;
        InitializeRepositories();

        // Load settings
        _currentSettings = await _settingsRepo!.GetOrCreateAsync(cancellationToken);

        OnProjectChanged(true, projectPath);
        _logger.LogInformation("Project opened successfully");
    }

    public Task CloseProjectAsync()
    {
        if (_context == null)
            return Task.CompletedTask;

        _logger.LogInformation("Closing project: {Path}", CurrentProjectPath);

        _context.Dispose();
        _context = null;
        _scanRootRepo = null;
        _settingsRepo = null;
        _currentSettings = null;
        CurrentProjectPath = null;

        OnProjectChanged(false, null);
        return Task.CompletedTask;
    }

    public async Task UpdateSettingsAsync(ProjectSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureProjectOpen();
        await _settingsRepo!.UpdateAsync(settings, cancellationToken);
        _currentSettings = settings;
    }

    public async Task<IReadOnlyList<ScanRoot>> GetScanRootsAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectOpen();
        return await _scanRootRepo!.GetAllAsync(cancellationToken);
    }

    public async Task<ScanRoot> AddScanRootAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureProjectOpen();

        // Validate path exists
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        // Check if path is already covered
        if (await _scanRootRepo!.IsPathCoveredAsync(path, cancellationToken))
        {
            throw new InvalidOperationException($"Path is already covered by an existing scan root: {path}");
        }

        return await _scanRootRepo.AddAsync(path, null, cancellationToken);
    }

    public async Task RemoveScanRootAsync(long scanRootId, CancellationToken cancellationToken = default)
    {
        EnsureProjectOpen();
        await _scanRootRepo!.RemoveAsync(scanRootId, cancellationToken);
    }

    public async Task<ProjectStats> GetProjectStatsAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectOpen();

        var stats = new ProjectStats();

        try
        {
            using var connection = await _context!.CreateConnectionAsync();

            // Get total file count
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM FileInstances";
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                stats.TotalFiles = Convert.ToInt32(result);
            }

            // Get hashed file count (files with a HashId)
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM FileInstances WHERE HashId IS NOT NULL";
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                stats.HashedFiles = Convert.ToInt32(result);
            }

            // Get unique file count
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM UniqueFiles";
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                stats.UniqueFiles = Convert.ToInt32(result);
            }

            // Get total bytes
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(SizeBytes), 0) FROM FileInstances";
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                stats.TotalBytes = Convert.ToInt64(result);
            }

            // Get scan root count
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM ScanRoots";
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                stats.ScanRootCount = Convert.ToInt32(result);
            }

            // Calculate duplicates (files - unique files, when both are available)
            if (stats.HashedFiles > 0 && stats.UniqueFiles > 0)
            {
                stats.DuplicateFiles = stats.HashedFiles - stats.UniqueFiles;
            }

            _logger.LogDebug("Project stats: {TotalFiles} files, {HashedFiles} hashed, {UniqueFiles} unique, {TotalBytes} bytes",
                stats.TotalFiles, stats.HashedFiles, stats.UniqueFiles, stats.TotalBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get project stats");
            throw;
        }

        return stats;
    }

    public async Task<PlanGenerationResult> GeneratePlanAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectOpen();

        var result = new PlanGenerationResult();

        try
        {
            using var connection = await _context!.CreateConnectionAsync();

            _logger.LogInformation("Starting plan generation...");

            // Step 1: Clear existing data (in case of regeneration)
            using (var clearCmd = connection.CreateCommand())
            {
                clearCmd.CommandText = "DELETE FROM FolderNodes";
                await clearCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            using (var clearCmd = connection.CreateCommand())
            {
                clearCmd.CommandText = "DELETE FROM UniqueFiles";
                await clearCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Step 2: Create UniqueFiles entries
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO UniqueFiles (HashId, RepresentativeFileInstanceId, FileTypeCategory, CopyEnabled, DuplicateCount)
                    SELECT
                        h.Id as HashId,
                        (SELECT fi.Id FROM FileInstances fi WHERE fi.HashId = h.Id ORDER BY LENGTH(fi.RelativePath) ASC LIMIT 1) as RepresentativeFileInstanceId,
                        (SELECT fi.Category FROM FileInstances fi WHERE fi.HashId = h.Id LIMIT 1) as FileTypeCategory,
                        1 as CopyEnabled,
                        COUNT(fi.Id) as DuplicateCount
                    FROM Hashes h
                    INNER JOIN FileInstances fi ON fi.HashId = h.Id
                    GROUP BY h.Id";

                var rowsInserted = await cmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("Created {Count} unique file entries", rowsInserted);
            }

            // Step 3: Create date-based folder structure from file modified dates
            // Get distinct year/month combinations from representative files
            var folderMap = new Dictionary<string, long>(); // path -> FolderId

            using (var dateCmd = connection.CreateCommand())
            {
                dateCmd.CommandText = @"
                    SELECT DISTINCT
                        strftime('%Y', fi.ModifiedUtc) as Year,
                        strftime('%Y-%m', fi.ModifiedUtc) as YearMonth
                    FROM UniqueFiles uf
                    INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
                    WHERE fi.ModifiedUtc IS NOT NULL
                    ORDER BY Year, YearMonth";

                using var reader = await dateCmd.ExecuteReaderAsync(cancellationToken);
                var yearFolders = new Dictionary<string, long>(); // year -> FolderId

                while (await reader.ReadAsync(cancellationToken))
                {
                    var year = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0);
                    var yearMonth = reader.IsDBNull(1) ? null : reader.GetString(1);

                    // Create year folder if not exists
                    if (!yearFolders.ContainsKey(year))
                    {
                        var yearFolderId = await CreateFolderNodeAsync(connection, null, year, year,
                            $"Files from {year}", cancellationToken);
                        yearFolders[year] = yearFolderId;
                        folderMap[year] = yearFolderId;
                    }

                    // Create month folder under year
                    if (!string.IsNullOrEmpty(yearMonth) && year != "Unknown")
                    {
                        var monthPath = $"{year}/{yearMonth}";
                        if (!folderMap.ContainsKey(monthPath))
                        {
                            var monthFolderId = await CreateFolderNodeAsync(connection, yearFolders[year],
                                yearMonth, monthPath, $"Files from {yearMonth}", cancellationToken);
                            folderMap[monthPath] = monthFolderId;
                        }
                    }
                }
            }

            // Ensure "Unknown" folder exists for files with no valid date
            if (!folderMap.ContainsKey("Unknown"))
            {
                var unknownFolderId = await CreateFolderNodeAsync(connection, null, "Unknown", "Unknown",
                    "Files with unknown or invalid dates", cancellationToken);
                folderMap["Unknown"] = unknownFolderId;
            }

            _logger.LogInformation("Created {Count} folder nodes", folderMap.Count);
            result.FoldersCreated = folderMap.Count;

            // Step 4: Assign UniqueFiles to their folders based on date
            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.CommandText = @"
                    UPDATE UniqueFiles
                    SET PlannedFolderNodeId = (
                        SELECT fn.Id
                        FROM FolderNodes fn
                        WHERE fn.ProposedRelativePath = (
                            SELECT
                                CASE
                                    WHEN fi.ModifiedUtc IS NULL OR fi.ModifiedUtc = '' THEN 'Unknown'
                                    ELSE strftime('%Y', fi.ModifiedUtc) || '/' || strftime('%Y-%m', fi.ModifiedUtc)
                                END
                            FROM FileInstances fi
                            WHERE fi.Id = UniqueFiles.RepresentativeFileInstanceId
                        )
                        LIMIT 1
                    )";
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Assign files without a folder to Unknown (fallback for edge cases)
            using (var fallbackCmd = connection.CreateCommand())
            {
                fallbackCmd.CommandText = @"
                    UPDATE UniqueFiles
                    SET PlannedFolderNodeId = (SELECT Id FROM FolderNodes WHERE ProposedRelativePath = 'Unknown' LIMIT 1)
                    WHERE PlannedFolderNodeId IS NULL";
                await fallbackCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Step 5: Update folder counts (including duplicate counts)
            // DuplicateCount = sum of (DuplicateCount - 1) for each UniqueFile in the folder
            // This counts extra copies beyond the first one
            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = @"
                    UPDATE FolderNodes
                    SET UniqueCount = (
                        SELECT COUNT(*) FROM UniqueFiles WHERE PlannedFolderNodeId = FolderNodes.Id
                    ),
                    TotalSizeBytes = (
                        SELECT COALESCE(SUM(fi.SizeBytes), 0)
                        FROM UniqueFiles uf
                        INNER JOIN FileInstances fi ON fi.Id = uf.RepresentativeFileInstanceId
                        WHERE uf.PlannedFolderNodeId = FolderNodes.Id
                    ),
                    DuplicateCount = (
                        SELECT COALESCE(SUM(uf.DuplicateCount - 1), 0)
                        FROM UniqueFiles uf
                        WHERE uf.PlannedFolderNodeId = FolderNodes.Id AND uf.DuplicateCount > 1
                    )";
                await countCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Also update parent folder counts (year folders with sum of month folders)
            using (var parentCountCmd = connection.CreateCommand())
            {
                parentCountCmd.CommandText = @"
                    UPDATE FolderNodes
                    SET UniqueCount = (
                        SELECT COALESCE(SUM(child.UniqueCount), 0) + FolderNodes.UniqueCount
                        FROM FolderNodes child
                        WHERE child.ParentId = FolderNodes.Id
                    ),
                    TotalSizeBytes = (
                        SELECT COALESCE(SUM(child.TotalSizeBytes), 0) + FolderNodes.TotalSizeBytes
                        FROM FolderNodes child
                        WHERE child.ParentId = FolderNodes.Id
                    ),
                    DuplicateCount = (
                        SELECT COALESCE(SUM(child.DuplicateCount), 0) + FolderNodes.DuplicateCount
                        FROM FolderNodes child
                        WHERE child.ParentId = FolderNodes.Id
                    )
                    WHERE EXISTS (SELECT 1 FROM FolderNodes child WHERE child.ParentId = FolderNodes.Id)";
                await parentCountCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Get final statistics
            using (var statsCmd = connection.CreateCommand())
            {
                statsCmd.CommandText = "SELECT COUNT(*) FROM UniqueFiles";
                result.UniqueFilesCreated = Convert.ToInt32(await statsCmd.ExecuteScalarAsync(cancellationToken));
            }

            using (var dupCmd = connection.CreateCommand())
            {
                dupCmd.CommandText = "SELECT COALESCE(SUM(DuplicateCount) - COUNT(*), 0) FROM UniqueFiles";
                result.DuplicatesFound = Convert.ToInt32(await dupCmd.ExecuteScalarAsync(cancellationToken));
            }

            result.Success = true;
            _logger.LogInformation("Plan generation completed: {Unique} unique files, {Duplicates} duplicates, {Folders} folders",
                result.UniqueFilesCreated, result.DuplicatesFound, result.FoldersCreated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plan generation failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<long> CreateFolderNodeAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long? parentId,
        string displayName,
        string relativePath,
        string whyExplanation,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO FolderNodes (ParentId, DisplayName, ProposedRelativePath, CopyEnabled, WhyExplanation)
            VALUES (@ParentId, @DisplayName, @RelativePath, 1, @WhyExplanation);
            SELECT last_insert_rowid();";

        cmd.Parameters.AddWithValue("@ParentId", parentId.HasValue ? parentId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@DisplayName", displayName);
        cmd.Parameters.AddWithValue("@RelativePath", relativePath);
        cmd.Parameters.AddWithValue("@WhyExplanation", whyExplanation);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Gets the database context for the current project.
    /// </summary>
    internal DatabaseContext GetContext()
    {
        EnsureProjectOpen();
        return _context!;
    }

    /// <summary>
    /// Gets the scan root repository.
    /// </summary>
    internal ScanRootRepository GetScanRootRepository()
    {
        EnsureProjectOpen();
        return _scanRootRepo!;
    }

    /// <summary>
    /// Gets the project settings repository.
    /// </summary>
    internal ProjectSettingsRepository GetSettingsRepository()
    {
        EnsureProjectOpen();
        return _settingsRepo!;
    }

    private void InitializeRepositories()
    {
        _scanRootRepo = new ScanRootRepository(_context!, _loggerFactory.CreateLogger<ScanRootRepository>());
        _settingsRepo = new ProjectSettingsRepository(_context!, _loggerFactory.CreateLogger<ProjectSettingsRepository>());
    }

    private void EnsureProjectOpen()
    {
        if (_context == null)
        {
            throw new InvalidOperationException("No project is currently open");
        }
    }

    private void OnProjectChanged(bool isOpen, string? path)
    {
        ProjectChanged?.Invoke(this, new ProjectChangedEventArgs
        {
            IsOpen = isOpen,
            ProjectPath = path
        });
    }
}
