using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Service for loading and caching image thumbnails with LRU eviction.
/// </summary>
public class ThumbnailService : IThumbnailService
{
    private readonly ILogger<ThumbnailService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly LinkedList<string> _lruList = new();
    private readonly object _lruLock = new();
    private const int MaxCacheSize = 100;

    // Supported image extensions
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp",
        ".heic", ".heif"
    };

    public ThumbnailService(ILogger<ThumbnailService> logger)
    {
        _logger = logger;
    }

    public int CacheSize => _cache.Count;

    public async Task<BitmapImage?> GetThumbnailAsync(
        string filePath,
        int maxWidth = 300,
        int maxHeight = 300,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);
        if (!SupportedExtensions.Contains(extension))
        {
            _logger.LogDebug("Unsupported image format: {Extension}", extension);
            return null;
        }

        // Create cache key including dimensions
        var cacheKey = $"{filePath}|{maxWidth}x{maxHeight}";

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            UpdateLruAccess(cacheKey);
            return entry.Image;
        }

        // Load thumbnail asynchronously
        try
        {
            var thumbnail = await Task.Run(() => LoadThumbnail(filePath, maxWidth, maxHeight), cancellationToken);

            if (thumbnail != null)
            {
                AddToCache(cacheKey, thumbnail);
            }

            return thumbnail;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load thumbnail for {FilePath}", filePath);
            return null;
        }
    }

    public void ClearCache()
    {
        lock (_lruLock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
        _logger.LogDebug("Thumbnail cache cleared");
    }

    private BitmapImage? LoadThumbnail(string filePath, int maxWidth, int maxHeight)
    {
        try
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // HEIC/HEIF requires Windows codec - graceful degradation
            if (extension is ".heic" or ".heif")
            {
                return LoadHeicThumbnail(filePath, maxWidth, maxHeight);
            }

            // Standard image formats
            return LoadStandardThumbnail(filePath, maxWidth, maxHeight);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error loading thumbnail: {FilePath}", filePath);
            return null;
        }
    }

    private BitmapImage? LoadStandardThumbnail(string filePath, int maxWidth, int maxHeight)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.UriSource = new Uri(filePath, UriKind.Absolute);

        // Decode to target size for memory efficiency
        bitmap.DecodePixelWidth = maxWidth;

        bitmap.EndInit();
        bitmap.Freeze(); // Required for cross-thread access

        return bitmap;
    }

    private BitmapImage? LoadHeicThumbnail(string filePath, int maxWidth, int maxHeight)
    {
        // Try to load HEIC using Windows codec
        // This requires the HEVC Video Extensions from the Microsoft Store
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.DecodePixelWidth = maxWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("HEIC codec not available for {FilePath}: {Message}. Install HEVC Video Extensions from Microsoft Store.",
                filePath, ex.Message);
            return CreatePlaceholderImage(maxWidth, maxHeight, "HEIC");
        }
    }

    private BitmapImage? CreatePlaceholderImage(int width, int height, string format)
    {
        // Create a simple placeholder for unsupported formats
        // In a real implementation, you might render text or an icon
        try
        {
            // Return null for now - the UI will show a placeholder
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void AddToCache(string key, BitmapImage image)
    {
        lock (_lruLock)
        {
            // Evict oldest entries if cache is full
            while (_cache.Count >= MaxCacheSize && _lruList.Count > 0)
            {
                var oldest = _lruList.First;
                if (oldest != null)
                {
                    _lruList.RemoveFirst();
                    _cache.TryRemove(oldest.Value, out _);
                }
            }

            // Add new entry
            _cache[key] = new CacheEntry { Image = image, Node = null };
            var node = _lruList.AddLast(key);
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.Node = node;
            }
        }
    }

    private void UpdateLruAccess(string key)
    {
        lock (_lruLock)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.Node != null)
            {
                _lruList.Remove(entry.Node);
                entry.Node = _lruList.AddLast(key);
            }
        }
    }

    private class CacheEntry
    {
        public BitmapImage? Image { get; set; }
        public LinkedListNode<string>? Node { get; set; }
    }
}
