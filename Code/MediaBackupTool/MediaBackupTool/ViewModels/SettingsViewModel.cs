using CommunityToolkit.Mvvm.ComponentModel;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Settings page.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IPowerManagementService _powerManagement;

    [ObservableProperty]
    private CpuProfile _selectedCpuProfile = CpuProfile.Balanced;

    [ObservableProperty]
    private bool _archiveScanningEnabled = false;

    [ObservableProperty]
    private int _archiveMaxSizeMB = 100;

    [ObservableProperty]
    private bool _archiveNestedEnabled = false;

    [ObservableProperty]
    private int _archiveMaxDepth = 3;

    [ObservableProperty]
    private int _movieHashChunkSizeMB = 16;

    [ObservableProperty]
    private bool _verifyByDefault = true;

    [ObservableProperty]
    private bool _preventSleepEnabled = true;

    [ObservableProperty]
    private bool _isPreventingSleep;

    [ObservableProperty]
    private int _activeOperationCount;

    public CpuProfile[] AvailableCpuProfiles => Enum.GetValues<CpuProfile>();

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IPowerManagementService powerManagement)
    {
        _logger = logger;
        _powerManagement = powerManagement;

        // Initialize from service
        _preventSleepEnabled = _powerManagement.PreventSleepEnabled;
        _isPreventingSleep = _powerManagement.IsPreventingSleep;
        _activeOperationCount = _powerManagement.ActiveOperationCount;

        // Subscribe to state changes
        _powerManagement.PreventSleepStateChanged += OnPreventSleepStateChanged;
    }

    private void OnPreventSleepStateChanged(object? sender, bool isPreventing)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsPreventingSleep = isPreventing;
            ActiveOperationCount = _powerManagement.ActiveOperationCount;
        });
    }

    partial void OnSelectedCpuProfileChanged(CpuProfile value)
    {
        _logger.LogInformation("CPU profile changed to {Profile}", value);
        // TODO: Persist to database
    }

    partial void OnPreventSleepEnabledChanged(bool value)
    {
        _powerManagement.PreventSleepEnabled = value;
        _logger.LogInformation("Prevent sleep setting changed to {Enabled}", value);
    }
}
