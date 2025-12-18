using System.Windows;
using MediaBackupTool.Data;
using MediaBackupTool.Data.Repositories;
using MediaBackupTool.Infrastructure.Logging;
using MediaBackupTool.Infrastructure.Navigation;
using MediaBackupTool.Infrastructure.State;
using MediaBackupTool.Services.Implementation;
using MediaBackupTool.Services.Interfaces;
using MediaBackupTool.ViewModels;
using MediaBackupTool.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace MediaBackupTool;

/// <summary>
/// Application entry point with dependency injection setup.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public IServiceProvider Services => _serviceProvider!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configure logging FIRST - write to project's Code folder so developer has access
        // This ensures we capture any startup errors
        var projectLogPath = Path.Combine(
            @"C:\GITHUB_PRIVATE\File_Image_Backup_Handler\Code\MediaBackupTool",
            "Logs");
        Directory.CreateDirectory(projectLogPath);
        LoggingService.Configure(projectLogPath, clearExisting: true);

        // Log immediately to confirm logging is working
        Log.Information("=== Media Backup Tool Starting ===");
        Log.Information("Log files located at: {LogPath}", projectLogPath);

        try
        {
            // Configure services
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            var logger = _serviceProvider.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Services configured successfully");

            // Create and show main window
            var shell = _serviceProvider.GetRequiredService<Shell>();
            shell.Show();
            logger.LogInformation("Shell window shown");

            // Navigate to Project page
            var navigation = _serviceProvider.GetRequiredService<NavigationService>();
            _ = navigation.NavigateToAsync("Project");
            logger.LogInformation("Navigated to Project page");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");
            LoggingService.Flush();
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Media Backup Tool shutting down...");
        LoggingService.Flush();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: true);
        });

        // Infrastructure
        services.AddSingleton<AppStateManager>();
        services.AddSingleton<NavigationService>();

        // Database context factory - takes full database path (e.g., C:\Projects\MyProject\Project.db)
        services.AddSingleton<Func<string, DatabaseContext>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DatabaseContext>>();
            return databasePath => new DatabaseContext(databasePath, logger);
        });

        // Services
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IFileFilterService, FileFilterService>();
        services.AddSingleton<IScanService, ScanService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<ProjectViewModel>();
        services.AddTransient<SourcesViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddTransient<PlanViewModel>();
        services.AddTransient<CopyViewModel>();
        services.AddTransient<DuplicatesViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Views
        services.AddSingleton<Shell>();
    }
}
