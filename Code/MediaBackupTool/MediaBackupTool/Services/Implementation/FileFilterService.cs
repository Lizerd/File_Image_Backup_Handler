using MediaBackupTool.Models.Enums;
using MediaBackupTool.Services.Interfaces;

namespace MediaBackupTool.Services.Implementation;

/// <summary>
/// Implementation of file filtering based on extension and size.
/// </summary>
public class FileFilterService : IFileFilterService
{
    private readonly Dictionary<FileTypeCategory, HashSet<string>> _extensionsByCategory;
    private readonly Dictionary<string, FileTypeCategory> _categoryByExtension;
    private HashSet<string> _enabledExtensions;
    private HashSet<FileTypeCategory> _enabledCategories;
    private long? _minFileSize;
    private long? _maxFileSize;

    public FileFilterService()
    {
        _extensionsByCategory = new Dictionary<FileTypeCategory, HashSet<string>>
        {
            [FileTypeCategory.Image] = new(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif",
                ".webp", ".heic", ".heif", ".raw", ".cr2", ".cr3", ".nef",
                ".arw", ".dng", ".orf", ".rw2", ".pef", ".srw", ".ico",
                ".svg", ".psd", ".ai", ".eps"
            },
            [FileTypeCategory.Movie] = new(StringComparer.OrdinalIgnoreCase)
            {
                ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
                ".m4v", ".mpg", ".mpeg", ".3gp", ".3g2", ".mts", ".m2ts",
                ".vob", ".ogv", ".divx", ".xvid", ".rm", ".rmvb", ".asf"
            },
            [FileTypeCategory.Audio] = new(StringComparer.OrdinalIgnoreCase)
            {
                ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a",
                ".aiff", ".alac", ".ape", ".opus", ".mid", ".midi"
            },
            [FileTypeCategory.Document] = new(StringComparer.OrdinalIgnoreCase)
            {
                ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
                ".odt", ".ods", ".odp", ".txt", ".rtf", ".csv"
            },
            [FileTypeCategory.Archive] = new(StringComparer.OrdinalIgnoreCase)
            {
                ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
                ".cab", ".iso", ".dmg"
            },
            [FileTypeCategory.Other] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        // Build reverse lookup
        _categoryByExtension = new Dictionary<string, FileTypeCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in _extensionsByCategory)
        {
            foreach (var ext in extensions)
            {
                _categoryByExtension[ext] = category;
            }
        }

        // Default: Enable images only for MVP
        _enabledCategories = new HashSet<FileTypeCategory> { FileTypeCategory.Image };
        _enabledExtensions = new HashSet<string>(_extensionsByCategory[FileTypeCategory.Image], StringComparer.OrdinalIgnoreCase);
    }

    public bool ShouldIncludeFile(string filePath, long? fileSize = null)
    {
        // Filter 1: Extension (cheapest check)
        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension) || !_enabledExtensions.Contains(extension))
        {
            return false;
        }

        // Filter 2: Size (if configured and size provided)
        if (_minFileSize.HasValue || _maxFileSize.HasValue)
        {
            var size = fileSize ?? GetFileSize(filePath);
            if (size < 0) return false; // Error getting size

            if (_minFileSize.HasValue && size < _minFileSize.Value)
                return false;

            if (_maxFileSize.HasValue && size > _maxFileSize.Value)
                return false;
        }

        return true;
    }

    public FileTypeCategory GetFileCategory(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return FileTypeCategory.Other;

        return _categoryByExtension.TryGetValue(extension, out var category)
            ? category
            : FileTypeCategory.Other;
    }

    public IReadOnlySet<string> GetExtensionsForCategory(FileTypeCategory category)
    {
        return _extensionsByCategory.TryGetValue(category, out var extensions)
            ? extensions
            : new HashSet<string>();
    }

    public IReadOnlySet<string> GetEnabledExtensions() => _enabledExtensions;

    public void SetEnabledCategories(params FileTypeCategory[] categories)
    {
        _enabledCategories = new HashSet<FileTypeCategory>(categories);
        RebuildEnabledExtensions();
    }

    public void SetSizeFilter(long? minBytes, long? maxBytes)
    {
        _minFileSize = minBytes;
        _maxFileSize = maxBytes;
    }

    private void RebuildEnabledExtensions()
    {
        _enabledExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in _enabledCategories)
        {
            if (_extensionsByCategory.TryGetValue(category, out var extensions))
            {
                foreach (var ext in extensions)
                {
                    _enabledExtensions.Add(ext);
                }
            }
        }
    }

    private static long GetFileSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return -1;
        }
    }
}
