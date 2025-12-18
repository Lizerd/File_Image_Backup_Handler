namespace MediaBackupTool.Models.Enums;

/// <summary>
/// Hash algorithm level for duplicate detection.
/// Selected at project creation and fixed for the project.
/// </summary>
public enum HashLevel
{
    /// <summary>No cryptographic hash - file name + size only (not reliable for final dedup)</summary>
    None = 0,

    /// <summary>SHA-1 - Fast but weaker collision resistance</summary>
    SHA1 = 1,

    /// <summary>SHA-256 - Recommended default, strong and widely supported</summary>
    SHA256 = 2,

    /// <summary>SHA3-256 - Strongest, requires OS support check</summary>
    SHA3_256 = 3
}
