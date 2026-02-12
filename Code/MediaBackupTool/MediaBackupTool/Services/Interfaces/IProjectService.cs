using MediaBackupTool.Models.Domain;

namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for managing project files (SQLite databases) and settings.
/// </summary>
public interface IProjectService
{
    /// <summary>
    /// Gets whether a project is currently open.
    /// </summary>
    bool IsProjectOpen { get; }

    /// <summary>
    /// Gets the current project path, or null if no project is open.
    /// </summary>
    string? CurrentProjectPath { get; }

    /// <summary>
    /// Gets the current project settings, or null if no project is open.
    /// </summary>
    ProjectSettings? CurrentSettings { get; }

    /// <summary>
    /// Creates a new project at the specified path.
    /// </summary>
    /// <param name="projectPath">Path for the new project file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CreateProjectAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing project from the specified path.
    /// </summary>
    /// <param name="projectPath">Path to the project file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task OpenProjectAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the current project.
    /// </summary>
    Task CloseProjectAsync();

    /// <summary>
    /// Updates project settings.
    /// </summary>
    /// <param name="settings">Updated settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpdateSettingsAsync(ProjectSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all scan roots for the current project.
    /// </summary>
    Task<IReadOnlyList<ScanRoot>> GetScanRootsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a scan root to the current project.
    /// </summary>
    Task<ScanRoot> AddScanRootAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a scan root from the current project.
    /// </summary>
    Task RemoveScanRootAsync(long scanRootId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics about the current project (file counts, etc.).
    /// </summary>
    Task<ProjectStats> GetProjectStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the plan by creating UniqueFile entries from hashed files.
    /// </summary>
    Task<PlanGenerationResult> GeneratePlanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when project is opened or closed.
    /// </summary>
    event EventHandler<ProjectChangedEventArgs>? ProjectChanged;
}

/// <summary>
/// Event arguments for project changes.
/// </summary>
public class ProjectChangedEventArgs : EventArgs
{
    public bool IsOpen { get; set; }
    public string? ProjectPath { get; set; }
}

/// <summary>
/// Statistics about the current project.
/// </summary>
public class ProjectStats
{
    public int TotalFiles { get; set; }
    public int HashedFiles { get; set; }
    public int UniqueFiles { get; set; }
    public int DuplicateFiles { get; set; }
    public long TotalBytes { get; set; }
    public int ScanRootCount { get; set; }
}

/// <summary>
/// Result of plan generation.
/// </summary>
public class PlanGenerationResult
{
    public bool Success { get; set; }
    public int UniqueFilesCreated { get; set; }
    public int DuplicatesFound { get; set; }
    public int FoldersCreated { get; set; }
    public string? ErrorMessage { get; set; }
}
