using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaBackupTool.Data;

/// <summary>
/// Manages SQLite database connection and initialization.
/// Uses WAL mode for better concurrent read/write performance.
/// </summary>
public class DatabaseContext : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseContext> _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    public string DatabasePath { get; }

    /// <summary>
    /// Creates a new DatabaseContext.
    /// </summary>
    /// <param name="databasePath">Full path to the database file (e.g., C:\Projects\MyProject\Project.db)</param>
    /// <param name="logger">Logger instance</param>
    public DatabaseContext(string databasePath, ILogger<DatabaseContext> logger)
    {
        _logger = logger;
        DatabasePath = databasePath;
        _connectionString = $"Data Source={DatabasePath}";
    }

    /// <summary>
    /// Gets an open database connection. Creates the database if it doesn't exist.
    /// </summary>
    public async Task<SqliteConnection> GetConnectionAsync()
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync();
            await ConfigureConnectionAsync(_connection);
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync();
            await ConfigureConnectionAsync(_connection);
        }

        return _connection;
    }

    /// <summary>
    /// Creates a new connection for bulk operations.
    /// Caller is responsible for disposal.
    /// </summary>
    public async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ConfigureConnectionAsync(connection);
        return connection;
    }

    private async Task ConfigureConnectionAsync(SqliteConnection connection)
    {
        // Configure SQLite for optimal performance
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-64000;
            PRAGMA foreign_keys=ON;
        ";
        await pragmaCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Initializes the database with the schema.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing database at {Path}", DatabasePath);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogDebug("Ensured directory exists: {Directory}", directory);
        }

        var connection = await GetConnectionAsync();
        _logger.LogDebug("Database connection opened");

        // Read and execute the schema SQL
        var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Migrations", "InitialSchema.sql");
        _logger.LogDebug("Looking for schema at: {SchemaPath}", schemaPath);

        string schemaSql;
        if (File.Exists(schemaPath))
        {
            _logger.LogDebug("Using external schema file");
            schemaSql = await File.ReadAllTextAsync(schemaPath);
        }
        else
        {
            _logger.LogDebug("Using embedded schema (external file not found)");
            schemaSql = GetEmbeddedSchema();
        }

        // Remove SQL comments and split by semicolons
        var cleanedSql = RemoveSqlComments(schemaSql);
        var statements = cleanedSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _logger.LogDebug("Executing {Count} schema statements", statements.Length);

        var successCount = 0;
        var skipCount = 0;
        var errorCount = 0;

        foreach (var statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement))
            {
                skipCount++;
                continue;
            }

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = statement;
                await cmd.ExecuteNonQueryAsync();
                successCount++;
                _logger.LogDebug("Executed: {Statement}", statement.Length > 60 ? statement[..60] + "..." : statement);
            }
            catch (SqliteException ex) when (ex.Message.Contains("already exists"))
            {
                // Ignore "already exists" errors during initialization
                skipCount++;
                _logger.LogDebug("Skipped (already exists): {Statement}", statement.Length > 60 ? statement[..60] + "..." : statement);
            }
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogError(ex, "Failed to execute schema statement: {Statement}", statement);
            }
        }

        _logger.LogInformation("Database initialized: {Success} succeeded, {Skipped} skipped, {Errors} errors",
            successCount, skipCount, errorCount);

        if (errorCount > 0)
        {
            _logger.LogWarning("Database initialization completed with {ErrorCount} errors - check logs for details", errorCount);
        }
    }

    /// <summary>
    /// Removes SQL comments from the schema.
    /// </summary>
    private static string RemoveSqlComments(string sql)
    {
        var lines = sql.Split('\n');
        var result = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip pure comment lines
            if (trimmed.StartsWith("--"))
                continue;

            // Remove inline comments
            var commentIndex = line.IndexOf("--");
            if (commentIndex >= 0)
            {
                result.AppendLine(line[..commentIndex]);
            }
            else
            {
                result.AppendLine(line);
            }
        }

        return result.ToString();
    }

    private static string GetEmbeddedSchema()
    {
        return @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-64000;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS ProjectSettings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectName TEXT NOT NULL DEFAULT 'New Project',
                HashLevel INTEGER NOT NULL DEFAULT 2,
                CpuProfile INTEGER NOT NULL DEFAULT 1,
                TargetPath TEXT,
                CurrentState INTEGER NOT NULL DEFAULT 0,
                VerifyByDefault INTEGER NOT NULL DEFAULT 1,
                ArchiveScanningEnabled INTEGER NOT NULL DEFAULT 0,
                ArchiveMaxSizeMB INTEGER NOT NULL DEFAULT 500,
                ArchiveNestedEnabled INTEGER NOT NULL DEFAULT 0,
                ArchiveMaxDepth INTEGER NOT NULL DEFAULT 3,
                MovieHashChunkSizeMB INTEGER NOT NULL DEFAULT 64,
                EnabledCategories TEXT NOT NULL DEFAULT 'Image',
                CreatedUtc TEXT NOT NULL,
                LastModifiedUtc TEXT NOT NULL,
                LastError TEXT
            );

            CREATE TABLE IF NOT EXISTS ScanRoots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE,
                Label TEXT NOT NULL,
                RootType INTEGER NOT NULL DEFAULT 0,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                LastScanUtc TEXT,
                FileCount INTEGER DEFAULT 0,
                TotalBytes INTEGER DEFAULT 0,
                AddedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Hashes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                HashAlgorithm TEXT NOT NULL,
                HashBytes BLOB NOT NULL,
                HashHex TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                PartialHashInfo TEXT,
                ComputedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS FileInstances (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScanRootId INTEGER NOT NULL,
                RelativePath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Extension TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                ModifiedUtc TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                Category INTEGER NOT NULL DEFAULT 0,
                HashId INTEGER,
                DiscoveredUtc TEXT NOT NULL,
                ErrorMessage TEXT,
                FOREIGN KEY (ScanRootId) REFERENCES ScanRoots(Id) ON DELETE CASCADE,
                FOREIGN KEY (HashId) REFERENCES Hashes(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_FileInstances_Extension ON FileInstances(Extension);
            CREATE INDEX IF NOT EXISTS IX_FileInstances_Status ON FileInstances(Status);
            CREATE INDEX IF NOT EXISTS IX_FileInstances_ScanRootId ON FileInstances(ScanRootId);
            CREATE INDEX IF NOT EXISTS IX_FileInstances_HashId ON FileInstances(HashId);

            CREATE TABLE IF NOT EXISTS FolderNodes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ParentId INTEGER,
                DisplayName TEXT NOT NULL,
                ProposedRelativePath TEXT NOT NULL,
                UserEditedName TEXT,
                CopyEnabled INTEGER NOT NULL DEFAULT 1,
                UniqueCount INTEGER DEFAULT 0,
                DuplicateCount INTEGER DEFAULT 0,
                TotalSizeBytes INTEGER DEFAULT 0,
                WhyExplanation TEXT,
                FOREIGN KEY (ParentId) REFERENCES FolderNodes(Id)
            );

            CREATE TABLE IF NOT EXISTS UniqueFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                HashId INTEGER NOT NULL UNIQUE,
                RepresentativeFileInstanceId INTEGER,
                FileTypeCategory INTEGER NOT NULL DEFAULT 0,
                CopyEnabled INTEGER NOT NULL DEFAULT 1,
                PlannedFolderNodeId INTEGER,
                PlannedFileName TEXT,
                CopiedUtc TEXT,
                VerifiedUtc TEXT,
                DuplicateCount INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (HashId) REFERENCES Hashes(Id),
                FOREIGN KEY (RepresentativeFileInstanceId) REFERENCES FileInstances(Id),
                FOREIGN KEY (PlannedFolderNodeId) REFERENCES FolderNodes(Id)
            );

            CREATE TABLE IF NOT EXISTS CopyJobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UniqueFileId INTEGER NOT NULL,
                DestinationFullPath TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                AttemptCount INTEGER NOT NULL DEFAULT 0,
                LastError TEXT,
                StartedUtc TEXT,
                CompletedUtc TEXT,
                FOREIGN KEY (UniqueFileId) REFERENCES UniqueFiles(Id)
            );

            CREATE TABLE IF NOT EXISTS Profiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                ImageExtensions TEXT NOT NULL,
                MovieExtensions TEXT,
                MinSizeBytes INTEGER,
                MaxSizeBytes INTEGER,
                MinImageWidth INTEGER,
                MinImageHeight INTEGER,
                IsDefault INTEGER NOT NULL DEFAULT 0
            );
        ";
    }

    /// <summary>
    /// Begins a transaction for bulk operations.
    /// </summary>
    public async Task<SqliteTransaction> BeginTransactionAsync()
    {
        var connection = await GetConnectionAsync();
        return (SqliteTransaction)await connection.BeginTransactionAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
    }
}
