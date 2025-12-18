using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Models.Domain;

/// <summary>
/// Represents a unique file by hash - one per duplicate group.
/// Links to the preferred file instance for naming/copying.
/// </summary>
public class UniqueFile
{
    public long UniqueFileId { get; set; }
    public long HashId { get; set; }
    public long? RepresentativeFileInstanceId { get; set; }
    public FileTypeCategory FileTypeCategory { get; set; } = FileTypeCategory.Image;
    public bool CopyEnabled { get; set; } = true;
    public long? PlannedFolderNodeId { get; set; }
    public string? PlannedFileName { get; set; }
    public DateTime? CopiedUtc { get; set; }
    public DateTime? VerifiedUtc { get; set; }
    public int DuplicateCount { get; set; } = 1;
}
