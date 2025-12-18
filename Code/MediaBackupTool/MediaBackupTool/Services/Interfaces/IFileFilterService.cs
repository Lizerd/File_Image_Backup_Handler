using MediaBackupTool.Models.Enums;

namespace MediaBackupTool.Services.Interfaces;

/// <summary>
/// Service for filtering files based on extension, size, and other criteria.
/// Filter order: extension → size (cheapest checks first).
/// </summary>
public interface IFileFilterService
{
    /// <summary>
    /// Checks if a file should be included based on current filter settings.
    /// </summary>
    /// <param name="filePath">Full path to the file</param>
    /// <param name="fileSize">File size in bytes (optional, will be read if not provided)</param>
    /// <returns>True if file passes all filters</returns>
    bool ShouldIncludeFile(string filePath, long? fileSize = null);

    /// <summary>
    /// Gets the file type category for a file extension.
    /// </summary>
    /// <param name="extension">File extension including the dot (e.g., ".jpg")</param>
    /// <returns>The category of the file type</returns>
    FileTypeCategory GetFileCategory(string extension);

    /// <summary>
    /// Gets all supported extensions for a file category.
    /// </summary>
    IReadOnlySet<string> GetExtensionsForCategory(FileTypeCategory category);

    /// <summary>
    /// Gets all currently enabled file extensions.
    /// </summary>
    IReadOnlySet<string> GetEnabledExtensions();

    /// <summary>
    /// Sets the enabled file type categories.
    /// </summary>
    void SetEnabledCategories(params FileTypeCategory[] categories);

    /// <summary>
    /// Sets minimum and maximum file size filters.
    /// </summary>
    /// <param name="minBytes">Minimum file size in bytes (null for no minimum)</param>
    /// <param name="maxBytes">Maximum file size in bytes (null for no maximum)</param>
    void SetSizeFilter(long? minBytes, long? maxBytes);
}
