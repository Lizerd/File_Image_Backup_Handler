using System.Windows.Media.Imaging;

namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for loading and caching image thumbnails.
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// Gets a thumbnail for the specified file path.
    /// Returns cached version if available.
    /// </summary>
    /// <param name="filePath">Full path to the image file</param>
    /// <param name="maxWidth">Maximum thumbnail width</param>
    /// <param name="maxHeight">Maximum thumbnail height</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>BitmapImage thumbnail or null if loading fails</returns>
    Task<BitmapImage?> GetThumbnailAsync(
        string filePath,
        int maxWidth = 300,
        int maxHeight = 300,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached thumbnails.
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Gets the current cache size (number of entries).
    /// </summary>
    int CacheSize { get; }
}
