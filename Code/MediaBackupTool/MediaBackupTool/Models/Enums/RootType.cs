namespace MediaBackupTool.Models.Enums;

/// <summary>
/// Type of storage device for a scan root.
/// </summary>
public enum RootType
{
    Fixed = 0,
    Removable = 1,
    Network = 2,
    Optical = 3,
    Unknown = 4
}
