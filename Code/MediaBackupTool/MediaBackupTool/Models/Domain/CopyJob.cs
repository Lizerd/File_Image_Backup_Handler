using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Models.Domain;

/// <summary>
/// Tracks the copy operation for a unique file.
/// </summary>
public class CopyJob
{
    public long CopyJobId { get; set; }
    public long UniqueFileId { get; set; }
    public string DestinationFullPath { get; set; } = string.Empty;
    public CopyJobStatus Status { get; set; } = CopyJobStatus.Pending;
    public int AttemptCount { get; set; } = 0;
    public string? LastError { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
