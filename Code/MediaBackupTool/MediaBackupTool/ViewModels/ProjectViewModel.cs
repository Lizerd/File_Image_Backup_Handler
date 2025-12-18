using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Infrastructure.Logging;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Project page - create/open projects.
/// </summary>
public partial class ProjectViewModel : ViewModelBase
{
    private readonly ILogger<ProjectViewModel> _logger;
    private readonly NavigationService _navigationService;
    private readonly MainViewModel _mainViewModel;
    private readonly IProjectService _projectService;

    [ObservableProperty]
    private string _newProjectName = string.Empty;

    [ObservableProperty]
    private string _newProjectPath = string.Empty;

    [ObservableProperty]
    private HashLevel _selectedHashLevel = HashLevel.SHA256;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private List<RecentProject> _recentProjects = new();

    public HashLevel[] AvailableHashLevels => new[]
    {
        HashLevel.SHA1,
        HashLevel.SHA256,
        HashLevel.SHA3_256
    };

    public ProjectViewModel(
        ILogger<ProjectViewModel> logger,
        NavigationService navigationService,
        MainViewModel mainViewModel,
        IProjectService projectService)
    {
        _logger = logger;
        _navigationService = navigationService;
        _mainViewModel = mainViewModel;
        _projectService = projectService;

        // Set default project path
        NewProjectPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MediaBackupProjects");
    }

    public override Task OnNavigatedToAsync()
    {
        LoadRecentProjects();
        return base.OnNavigatedToAsync();
    }

    [RelayCommand]
    private void BrowseProjectPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Project Folder",
            InitialDirectory = NewProjectPath
        };

        if (dialog.ShowDialog() == true)
        {
            NewProjectPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        _logger.LogInformation("CreateProjectAsync called - Name: {Name}, Path: {Path}", NewProjectName, NewProjectPath);

        if (string.IsNullOrWhiteSpace(NewProjectName))
        {
            ErrorMessage = "Please enter a project name.";
            _logger.LogWarning("Project creation aborted: no project name");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewProjectPath))
        {
            ErrorMessage = "Please select a project path.";
            _logger.LogWarning("Project creation aborted: no project path");
            return;
        }

        SetBusy(true, "Creating project...");
        ErrorMessage = null;

        try
        {
            // Create project folder
            var projectFolder = Path.Combine(NewProjectPath, NewProjectName);
            _logger.LogInformation("Creating project folder: {Folder}", projectFolder);
            Directory.CreateDirectory(projectFolder);

            // Create Logs subfolder (logging already configured to dev folder, will switch when project opens)
            var logsFolder = Path.Combine(projectFolder, "Logs");
            Directory.CreateDirectory(logsFolder);
            _logger.LogInformation("Created logs folder: {LogsFolder}", logsFolder);

            // Create project database
            var dbPath = Path.Combine(projectFolder, "Project.db");
            _logger.LogInformation("Creating database at: {DbPath}", dbPath);
            await _projectService.CreateProjectAsync(dbPath);
            _logger.LogInformation("Database created successfully");

            // Update settings with selected hash level
            if (_projectService.CurrentSettings == null)
            {
                _logger.LogError("CurrentSettings is null after project creation!");
                throw new InvalidOperationException("Failed to retrieve project settings after creation");
            }

            var settings = _projectService.CurrentSettings;
            _logger.LogInformation("Updating settings - ProjectName: {Name}, HashLevel: {Level}", NewProjectName, SelectedHashLevel);
            settings.ProjectName = NewProjectName;
            settings.HashLevel = SelectedHashLevel;
            await _projectService.UpdateSettingsAsync(settings);
            _logger.LogInformation("Settings updated successfully");

            // Save to recent projects
            SaveRecentProject(projectFolder, NewProjectName);

            _mainViewModel.SetProjectLoaded(NewProjectName);
            _logger.LogInformation("Project created successfully: {Name}", NewProjectName);

            // Navigate to Sources page
            await _navigationService.NavigateToAsync("Sources");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create project: {Message}", ex.Message);
            ErrorMessage = $"Failed to create project: {ex.Message}";
            LoggingService.Flush(); // Ensure error is written to log
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            await OpenProjectFromPathAsync(dialog.FolderName);
        }
    }

    [RelayCommand]
    private async Task OpenRecentProjectAsync(RecentProject project)
    {
        await OpenProjectFromPathAsync(project.Path);
    }

    private async Task OpenProjectFromPathAsync(string projectPath)
    {
        SetBusy(true, "Opening project...");
        ErrorMessage = null;

        try
        {
            var dbPath = Path.Combine(projectPath, "Project.db");
            if (!File.Exists(dbPath))
            {
                ErrorMessage = "No project database found in selected folder.";
                return;
            }

            // Configure logging
            var logsFolder = Path.Combine(projectPath, "Logs");
            Directory.CreateDirectory(logsFolder);
            LoggingService.Configure(logsFolder, clearExisting: true);

            _logger.LogInformation("Opening project from {Path}", projectPath);

            // Open project
            await _projectService.OpenProjectAsync(dbPath);

            var projectName = _projectService.CurrentSettings?.ProjectName ?? Path.GetFileName(projectPath);
            SaveRecentProject(projectPath, projectName);

            _mainViewModel.SetProjectLoaded(projectName);
            _logger.LogInformation("Project opened successfully");

            await _navigationService.NavigateToAsync("Sources");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open project");
            ErrorMessage = $"Failed to open project: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadRecentProjects()
    {
        try
        {
            var recentFile = GetRecentProjectsFilePath();
            if (File.Exists(recentFile))
            {
                var lines = File.ReadAllLines(recentFile);
                RecentProjects = lines
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Split('|'))
                    .Where(parts => parts.Length >= 2 && Directory.Exists(parts[0]))
                    .Select(parts => new RecentProject { Path = parts[0], Name = parts[1] })
                    .Take(10)
                    .ToList();
            }
        }
        catch
        {
            RecentProjects = new List<RecentProject>();
        }
    }

    private void SaveRecentProject(string path, string name)
    {
        try
        {
            var recent = RecentProjects.Where(r => r.Path != path).ToList();
            recent.Insert(0, new RecentProject { Path = path, Name = name });
            recent = recent.Take(10).ToList();

            var recentFile = GetRecentProjectsFilePath();
            var dir = Path.GetDirectoryName(recentFile)!;
            Directory.CreateDirectory(dir);

            File.WriteAllLines(recentFile, recent.Select(r => $"{r.Path}|{r.Name}"));
            RecentProjects = recent;
        }
        catch
        {
            // Ignore errors saving recent projects
        }
    }

    private static string GetRecentProjectsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaBackupTool",
            "recent_projects.txt");
    }
}

public class RecentProject
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
