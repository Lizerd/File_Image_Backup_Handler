using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MediaBackupTool.ViewModels.Base;

/// <summary>
/// Base class for all ViewModels using CommunityToolkit.Mvvm.
/// Provides common functionality and INotifyPropertyChanged.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyMessage;

    /// <summary>
    /// Called when the view is navigated to.
    /// Override to load data or initialize state.
    /// </summary>
    public virtual Task OnNavigatedToAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the view is navigated away from.
    /// Override to save state or clean up.
    /// </summary>
    public virtual Task OnNavigatedFromAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets busy state with optional message.
    /// </summary>
    protected void SetBusy(bool busy, string? message = null)
    {
        IsBusy = busy;
        BusyMessage = message;
    }
}
