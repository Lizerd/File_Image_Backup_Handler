namespace MediaBackupTool.Models.Domain;

/// <summary>
/// A node in the proposed destination folder tree.
/// Supports inline rename and per-folder enable/disable.
/// </summary>
public class FolderNode
{
    public long FolderNodeId { get; set; }
    public long? ParentFolderNodeId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ProposedRelativePath { get; set; } = string.Empty;
    public string? UserEditedName { get; set; }
    public bool CopyEnabled { get; set; } = true;
    public int UniqueCount { get; set; } = 0;
    public int DuplicateCount { get; set; } = 0;
    public long TotalSizeBytes { get; set; } = 0;
    public string? WhyExplanation { get; set; }

    /// <summary>
    /// Gets the effective display name (user-edited if available).
    /// </summary>
    public string EffectiveDisplayName => UserEditedName ?? DisplayName;
}
