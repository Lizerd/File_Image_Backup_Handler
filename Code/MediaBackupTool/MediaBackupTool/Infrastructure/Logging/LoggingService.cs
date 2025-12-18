using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace MediaBackupTool.Infrastructure.Logging;

/// <summary>
/// Manages dual logging: debug log (verbose) and warnings/errors log.
/// Both logs are cleared on application startup per spec requirements.
/// </summary>
public static class LoggingService
{
    private static string? _logFolderPath;

    public static string DebugLogPath => Path.Combine(_logFolderPath ?? ".", "Debug.log");
    public static string ErrorLogPath => Path.Combine(_logFolderPath ?? ".", "WarningsErrors.log");

    /// <summary>
    /// Configures Serilog with dual file sinks.
    /// </summary>
    /// <param name="logFolderPath">Path to store log files</param>
    /// <param name="clearExisting">If true, clears existing log files on startup</param>
    public static void Configure(string logFolderPath, bool clearExisting = true)
    {
        _logFolderPath = logFolderPath;

        // Ensure log folder exists
        Directory.CreateDirectory(logFolderPath);

        var debugLogPath = Path.Combine(logFolderPath, "Debug.log");
        var errorLogPath = Path.Combine(logFolderPath, "WarningsErrors.log");

        // Clear existing logs on startup (per spec requirement)
        if (clearExisting)
        {
            ClearLogFile(debugLogPath);
            ClearLogFile(errorLogPath);
        }

        // Configure Serilog with dual sinks - flush immediately for debugging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("Application", "MediaBackupTool")
            // Debug log - all levels, flush immediately
            .WriteTo.File(
                debugLogPath,
                rollingInterval: RollingInterval.Infinite,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            // Errors log - warnings and above only, flush immediately
            .WriteTo.File(
                errorLogPath,
                restrictedToMinimumLevel: LogEventLevel.Warning,
                rollingInterval: RollingInterval.Infinite,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Write initial log entry to confirm logging is working
        Log.Information("Logging configured. Debug: {DebugPath}, Errors: {ErrorPath}", debugLogPath, errorLogPath);
    }

    private static void ClearLogFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.WriteAllText(path, string.Empty);
            }
        }
        catch
        {
            // Ignore errors clearing logs
        }
    }

    /// <summary>
    /// Creates a Microsoft.Extensions.Logging logger factory using Serilog.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(dispose: true);
        });
    }

    /// <summary>
    /// Flushes all pending log entries.
    /// </summary>
    public static void Flush()
    {
        Log.CloseAndFlush();
    }
}
