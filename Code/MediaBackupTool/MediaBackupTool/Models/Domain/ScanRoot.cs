using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Models.Domain;

/// <summary>
/// A user-selected folder to scan for media files.
/// </summary>
public class ScanRoot
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public RootType RootType { get; set; } = RootType.Fixed;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastScanUtc { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the display name for the scan root.
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(Label) ? Label : Path;

    /// <summary>
    /// Gets a formatted string showing the last scan time.
    /// </summary>
    public string LastScanDisplay => LastScanUtc.HasValue
        ? $"Last scan: {LastScanUtc.Value.ToLocalTime():g}"
        : "Not scanned";

    /// <summary>
    /// Gets whether the path currently exists.
    /// </summary>
    public bool PathExists => Directory.Exists(Path);
}
