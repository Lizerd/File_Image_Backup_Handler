using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Models.Domain;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Sources page - manage scan roots and filters.
/// </summary>
public partial class SourcesViewModel : ViewModelBase
{
    private readonly ILogger<SourcesViewModel> _logger;
    private readonly IProjectService _projectService;
    private readonly IFileFilterService _filterService;
    private readonly NavigationService _navigationService;

    [ObservableProperty]
    private ObservableCollection<ScanRoot> _scanRoots = new();

    [ObservableProperty]
    private ScanRoot? _selectedRoot;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _includeImages = true;

    [ObservableProperty]
    private bool _includeMovies;

    [ObservableProperty]
    private bool _includeAudio;

    [ObservableProperty]
    private long _totalFiles;

    [ObservableProperty]
    private long _totalBytes;

    public bool HasScanRoots => ScanRoots.Count > 0;
    public bool HasEnabledRoots => ScanRoots.Any(r => r.IsEnabled);

    public SourcesViewModel(
        ILogger<SourcesViewModel> logger,
        IProjectService projectService,
        IFileFilterService filterService,
        NavigationService navigationService)
    {
        _logger = logger;
        _projectService = projectService;
        _filterService = filterService;
        _navigationService = navigationService;

        ScanRoots.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasScanRoots));
            OnPropertyChanged(nameof(HasEnabledRoots));
        };
    }

    public override async Task OnNavigatedToAsync()
    {
        await LoadScanRootsAsync();
        await base.OnNavigatedToAsync();
    }

    private async Task LoadScanRootsAsync()
    {
        if (!_projectService.IsProjectOpen)
        {
            ErrorMessage = "No project is open. Please create or open a project first.";
            return;
        }

        SetBusy(true, "Loading scan roots...");
        ErrorMessage = null;

        try
        {
            var roots = await _projectService.GetScanRootsAsync();
            ScanRoots.Clear();
            foreach (var root in roots)
            {
                ScanRoots.Add(root);
            }

            UpdateTotals();
            _logger.LogDebug("Loaded {Count} scan roots", roots.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load scan roots");
            ErrorMessage = $"Failed to load scan roots: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task AddScanRootAsync()
    {
        if (!_projectService.IsProjectOpen)
        {
            ErrorMessage = "No project is open.";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select Folder to Scan"
        };

        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FolderName;
        if (string.IsNullOrEmpty(path))
            return;

        SetBusy(true, "Adding scan root...");
        ErrorMessage = null;

        try
        {
            var root = await _projectService.AddScanRootAsync(path);
            ScanRoots.Add(root);
            _logger.LogInformation("Added scan root: {Path}", path);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogWarning("Cannot add scan root: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add scan root");
            ErrorMessage = $"Failed to add scan root: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private void AddDrive()
    {
        // Get available drives
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
            .Select(d => d.RootDirectory.FullName)
            .ToList();

        // For now, just open folder browser. Could show drive picker dialog later.
        AddScanRootCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RemoveScanRootAsync()
    {
        if (SelectedRoot == null)
            return;

        SetBusy(true, "Removing scan root...");
        ErrorMessage = null;

        try
        {
            await _projectService.RemoveScanRootAsync(SelectedRoot.Id);
            _logger.LogInformation("Removed scan root: {Path}", SelectedRoot.Path);
            ScanRoots.Remove(SelectedRoot);
            SelectedRoot = null;
            UpdateTotals();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove scan root");
            ErrorMessage = $"Failed to remove scan root: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private void ToggleRootEnabled(ScanRoot? root)
    {
        if (root == null) return;

        root.IsEnabled = !root.IsEnabled;
        OnPropertyChanged(nameof(HasEnabledRoots));
        _logger.LogDebug("Toggled scan root {Path} enabled: {Enabled}", root.Path, root.IsEnabled);
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (!HasEnabledRoots)
        {
            ErrorMessage = "Please add and enable at least one scan root.";
            return;
        }

        // Update filter service with selected categories
        var categories = new List<FileTypeCategory>();
        if (IncludeImages) categories.Add(FileTypeCategory.Image);
        if (IncludeMovies) categories.Add(FileTypeCategory.Movie);
        if (IncludeAudio) categories.Add(FileTypeCategory.Audio);

        if (categories.Count == 0)
        {
            ErrorMessage = "Please select at least one file type to scan.";
            return;
        }

        _filterService.SetEnabledCategories(categories.ToArray());

        // Navigate to scan page
        await _navigationService.NavigateToAsync("Scan");
    }

    partial void OnIncludeImagesChanged(bool value)
    {
        _logger.LogDebug("Include images: {Value}", value);
    }

    partial void OnIncludeMoviesChanged(bool value)
    {
        _logger.LogDebug("Include movies: {Value}", value);
    }

    partial void OnIncludeAudioChanged(bool value)
    {
        _logger.LogDebug("Include audio: {Value}", value);
    }

    private void UpdateTotals()
    {
        TotalFiles = ScanRoots.Sum(r => r.FileCount);
        TotalBytes = ScanRoots.Sum(r => r.TotalBytes);
    }
}
