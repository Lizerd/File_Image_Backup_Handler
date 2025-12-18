namespace MediaBackupTool.Models.Enums;

/// <summary>
/// Represents the current state of the application workflow.
/// Persisted to database for resume capability.
/// </summary>
public enum AppState
{
    Idle = 0,
    Scanning = 1,
    ScanPaused = 2,
    Hashing = 3,
    HashPaused = 4,
    Planning = 5,
    ReadyToCopy = 6,
    Copying = 7,
    CopyPaused = 8,
    Completed = 9,
    Faulted = 10
}
