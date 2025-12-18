using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Infrastructure.State;
using MediaBackupTool.ViewModels.Base;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// Main view model for the shell/main window.
/// Manages navigation and global state display.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly NavigationService _navigationService;
    private readonly AppStateManager _stateManager;

    public NavigationService Navigation => _navigationService;
    public AppStateManager State => _stateManager;

    [ObservableProperty]
    private string _windowTitle = "Media Backup Tool";

    [ObservableProperty]
    private bool _isProjectLoaded;

    [ObservableProperty]
    private string? _projectName;

    public MainViewModel(NavigationService navigationService, AppStateManager stateManager)
    {
        _navigationService = navigationService;
        _stateManager = stateManager;
    }

    [RelayCommand]
    private async Task NavigateToAsync(string pageName)
    {
        await _navigationService.NavigateToAsync(pageName);
    }

    public void SetProjectLoaded(string projectName)
    {
        IsProjectLoaded = true;
        ProjectName = projectName;
        WindowTitle = $"Media Backup Tool - {projectName}";
    }

    public void ClearProject()
    {
        IsProjectLoaded = false;
        ProjectName = null;
        WindowTitle = "Media Backup Tool";
    }
}
