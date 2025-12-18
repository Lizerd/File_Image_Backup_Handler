using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Models.Domain;

/// <summary>
/// Project-level settings stored in database.
/// One record per project.
/// </summary>
public class ProjectSettings
{
    public long Id { get; set; }
    public string ProjectName { get; set; } = "New Project";
    public HashLevel HashLevel { get; set; } = HashLevel.SHA256;
    public CpuProfile CpuProfile { get; set; } = CpuProfile.Balanced;
    public string? TargetPath { get; set; }
    public AppState CurrentState { get; set; } = AppState.Idle;
    public bool VerifyByDefault { get; set; } = true;
    public bool ArchiveScanningEnabled { get; set; } = false;
    public int ArchiveMaxSizeMB { get; set; } = 500;
    public bool ArchiveNestedEnabled { get; set; } = false;
    public int ArchiveMaxDepth { get; set; } = 3;
    public int MovieHashChunkSizeMB { get; set; } = 64;
    public string EnabledCategories { get; set; } = "Image";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
}
