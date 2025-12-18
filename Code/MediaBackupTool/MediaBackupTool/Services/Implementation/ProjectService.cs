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
