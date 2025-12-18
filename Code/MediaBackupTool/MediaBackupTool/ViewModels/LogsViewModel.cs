using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBackupTool.Infrastructure.Logging;
using MediaBackupTool.ViewModels.Base;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace MediaBackupTool.ViewModels;

/// <summary>
/// ViewModel for the Logs page - view logs and diagnostics.
/// </summary>
public partial class LogsViewModel : ViewModelBase
{
    private readonly ILogger<LogsViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<LogEntry> _recentErrors = new();

    [ObservableProperty]
    private string? _debugLogPath;

    [ObservableProperty]
    private string? _errorLogPath;

    public LogsViewModel(ILogger<LogsViewModel> logger)
    {
        _logger = logger;
        DebugLogPath = LoggingService.DebugLogPath;
        ErrorLogPath = LoggingService.ErrorLogPath;
    }

    public override Task OnNavigatedToAsync()
    {
        LoadRecentErrors();
        return base.OnNavigatedToAsync();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(DebugLogPath);
            if (folder != null && Directory.Exists(folder))
            {
                Process.Start("explorer.exe", folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log folder");
        }
    }

    [RelayCommand]
    private void CopySummaryToClipboard()
    {
        try
        {
            var summary = $"""
                Media Backup Tool - Log Summary
                Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

                Recent Errors:
                {string.Join(Environment.NewLine, RecentErrors.Select(e => $"[{e.Timestamp:HH:mm:ss}] {e.Message}"))}
                """;

            System.Windows.Clipboard.SetText(summary);
            _logger.LogInformation("Copied summary to clipboard");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy summary to clipboard");
        }
    }

    [RelayCommand]
    private void RefreshLogs()
    {
        LoadRecentErrors();
    }

    private void LoadRecentErrors()
    {
        RecentErrors.Clear();

        try
        {
            if (File.Exists(ErrorLogPath))
            {
                var lines = File.ReadLines(ErrorLogPath).TakeLast(100).ToList();
                foreach (var line in lines)
                {
                    RecentErrors.Add(new LogEntry { Message = line, Timestamp = DateTime.Now });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent errors");
        }
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
}
