using CommunityToolkit.Mvvm.ComponentModel;
using MediaBackupTool.Models.Enums;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Settings page.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ILogger<SettingsViewModel> _logger;

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

    public CpuProfile[] AvailableCpuProfiles => Enum.GetValues<CpuProfile>();

    public SettingsViewModel(ILogger<SettingsViewModel> logger)
    {
        _logger = logger;
    }

    partial void OnSelectedCpuProfileChanged(CpuProfile value)
    {
        _logger.LogInformation("CPU profile changed to {Profile}", value);
        // TODO: Persist to database
    }
}
