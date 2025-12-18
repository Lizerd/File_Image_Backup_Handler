using CommunityToolkit.Mvvm.ComponentModel;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Duplicates page - view and search duplicates.
/// </summary>
public partial class DuplicatesViewModel : ViewModelBase
{
    private readonly ILogger<DuplicatesViewModel> _logger;

    [ObservableProperty]
    private string? _searchQuery;

    [ObservableProperty]
    private ObservableCollection<DuplicateGroupViewModel> _duplicateGroups = new();

    [ObservableProperty]
    private DuplicateGroupViewModel? _selectedGroup;

    [ObservableProperty]
    private int _totalGroups;

    [ObservableProperty]
    private int _totalDuplicates;

    public DuplicatesViewModel(ILogger<DuplicatesViewModel> logger)
    {
        _logger = logger;
    }

    partial void OnSearchQueryChanged(string? value)
    {
        // TODO: Implement search
    }
}

public partial class DuplicateGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _hashHex = string.Empty;

    [ObservableProperty]
    private int _occurrenceCount;

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    private ObservableCollection<string> _paths = new();
}
