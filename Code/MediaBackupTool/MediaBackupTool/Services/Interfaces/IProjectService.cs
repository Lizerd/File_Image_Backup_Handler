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
