namespace MediaBackupTool.Models.Domain;

/// <summary>
/// Computed hash value for duplicate detection.
/// </summary>
public class HashInfo
{
    public long HashId { get; set; }
    public string HashAlgorithm { get; set; } = "SHA256";
    public byte[] HashBytes { get; set; } = Array.Empty<byte>();
    public string HashHex { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? PartialHashInfo { get; set; } // For movie partial hash parameters
    public DateTime ComputedUtc { get; set; } = DateTime.UtcNow;
}
