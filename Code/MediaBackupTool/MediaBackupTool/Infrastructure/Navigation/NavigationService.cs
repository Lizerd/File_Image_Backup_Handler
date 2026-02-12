using CommunityToolkit.Mvvm.ComponentModel;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBackupTool.Infrastructure.Navigation;

/// <summary>
/// Manages navigation between views/pages.
/// </summary>
public partial class NavigationService : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    [ObservableProperty]
    private string _currentPageName = "Project";

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Navigates to a view model by type.
    /// </summary>
    public async Task NavigateToAsync<TViewModel>() where TViewModel : ViewModelBase
    {
        // Notify old view model
        if (CurrentViewModel != null)
        {
            await CurrentViewModel.OnNavigatedFromAsync();
        }

        // Create and set new view model
        var viewModel = _serviceProvider.GetRequiredService<TViewModel>();
        CurrentViewModel = viewModel;
        CurrentPageName = typeof(TViewModel).Name.Replace("ViewModel", "");

        // Notify new view model
        await viewModel.OnNavigatedToAsync();
    }

    /// <summary>
    /// Navigates to a view model by name.
    /// </summary>
    public async Task NavigateToAsync(string viewModelName)
    {
        var pageMapping = new Dictionary<string, Type>
        {
            { "Project", typeof(ViewModels.ProjectViewModel) },
            { "Sources", typeof(ViewModels.SourcesViewModel) },
            { "Scan", typeof(ViewModels.ScanViewModel) },
            { "Hash", typeof(ViewModels.HashViewModel) },
            { "Plan", typeof(ViewModels.PlanViewModel) },
            { "Copy", typeof(ViewModels.CopyViewModel) },
            { "Verification", typeof(ViewModels.VerificationViewModel) },
            { "Duplicates", typeof(ViewModels.DuplicatesViewModel) },
            { "Logs", typeof(ViewModels.LogsViewModel) },
            { "Settings", typeof(ViewModels.SettingsViewModel) }
        };

        if (pageMapping.TryGetValue(viewModelName, out var vmType))
        {
            // Notify old view model
            if (CurrentViewModel != null)
            {
                await CurrentViewModel.OnNavigatedFromAsync();
            }

            // Create and set new view model
            var viewModel = (ViewModelBase)_serviceProvider.GetRequiredService(vmType);
            CurrentViewModel = viewModel;
            CurrentPageName = viewModelName;

            // Notify new view model
            await viewModel.OnNavigatedToAsync();
        }
    }
}
