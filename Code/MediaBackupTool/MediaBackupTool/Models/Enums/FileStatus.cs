namespace MediaBackupTool.Models.Enums;

/// <summary>
/// Status of a file instance through the processing pipeline.
/// </summary>
public enum FileStatus
{
    Discovered = 0,
    FilteredOut = 1,
    HashPending = 2,
    Hashed = 3,
    CopyPlanned = 4,
    Copied = 5,
    Verified = 6,
    Error = 7
}
