using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Models.Domain;

/// <summary>
/// Represents a single file occurrence discovered during scan.
/// The main table - will have millions of rows.
/// </summary>
public class FileInstance
{
    public long Id { get; set; }
    public long ScanRootId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public int Attributes { get; set; }
    public bool IsFromArchive { get; set; } = false;
    public string? ArchiveContainerPath { get; set; }
    public string? ArchiveEntryPath { get; set; }
    public FileStatus Status { get; set; } = FileStatus.Discovered;
    public FileTypeCategory Category { get; set; } = FileTypeCategory.Image;
    public int? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long? HashId { get; set; }
    public bool PreferredForUniqueFile { get; set; } = false;
    public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Reconstructs the full path from root path and relative path.
    /// </summary>
    public string GetFullPath(string rootPath)
    {
        return Path.Combine(rootPath, RelativePath);
    }
}
