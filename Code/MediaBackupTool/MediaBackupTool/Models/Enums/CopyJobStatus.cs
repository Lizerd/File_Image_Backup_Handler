namespace MediaBackupTool.Models.Enums;

/// <summary>
/// Status of a copy job operation.
/// </summary>
public enum CopyJobStatus
{
    Pending = 0,
    InProgress = 1,
    Copied = 2,
    Verified = 3,
    Skipped = 4,
    Error = 5
}
