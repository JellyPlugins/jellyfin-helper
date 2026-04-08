using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Service that calculates media file statistics per library type.
/// </summary>
public class MediaStatisticsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MediaStatisticsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStatisticsService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public MediaStatisticsService(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<MediaStatisticsService> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Calculates statistics for all configured libraries.
    /// </summary>
    /// <returns>The aggregated media statistics.</returns>
    public MediaStatisticsResult CalculateStatistics()
    {
        var result = new MediaStatisticsResult();

        var virtualFolders = _libraryManager.GetVirtualFolders();

        foreach (var vf in virtualFolders)
        {
            var collectionType = vf.CollectionType;
            var isMovies = collectionType is CollectionTypeOptions.movies
                or CollectionTypeOptions.homevideos
                or CollectionTypeOptions.musicvideos;
            var isTvShows = collectionType is CollectionTypeOptions.tvshows;
            var isMusic = collectionType is CollectionTypeOptions.music;

            var libraryStats = new LibraryStatistics
            {
                LibraryName = vf.Name ?? "Unknown",
                CollectionType = collectionType?.ToString() ?? "mixed"
            };

            foreach (var location in vf.Locations)
            {
                _logger.LogDebug("Scanning library location: {Location} (type: {Type})", location, collectionType);
                AnalyzeDirectoryRecursive(location, libraryStats);
            }

            result.Libraries.Add(libraryStats);

            // Aggregate into category totals
            if (isTvShows)
            {
                result.TvShows.Add(libraryStats);
            }
            else if (isMusic)
            {
                result.Music.Add(libraryStats);
            }
            else if (isMovies)
            {
                result.Movies.Add(libraryStats);
            }
            else
            {
                result.Other.Add(libraryStats);
            }
        }

        return result;
    }

    /// <summary>
    /// Recursively analyzes a directory and accumulates file size statistics.
    /// </summary>
    /// <param name="directoryPath">The directory to analyze.</param>
    /// <param name="stats">The statistics accumulator.</param>
    private void AnalyzeDirectoryRecursive(string directoryPath, LibraryStatistics stats)
    {
        try
        {
            var files = _fileSystem.GetFiles(directoryPath, false);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file.FullName);
                var size = file.Length;

                if (MediaExtensions.VideoExtensions.Contains(ext))
                {
                    stats.VideoSize += size;
                    stats.VideoFileCount++;
                }
                else if (MediaExtensions.SubtitleExtensions.Contains(ext))
                {
                    stats.SubtitleSize += size;
                    stats.SubtitleFileCount++;
                }
                else if (MediaExtensions.ImageExtensions.Contains(ext))
                {
                    stats.ImageSize += size;
                    stats.ImageFileCount++;
                }
                else if (MediaExtensions.NfoExtensions.Contains(ext))
                {
                    stats.NfoSize += size;
                    stats.NfoFileCount++;
                }
                else if (MediaExtensions.AudioExtensions.Contains(ext))
                {
                    stats.AudioSize += size;
                    stats.AudioFileCount++;
                }
                else
                {
                    stats.OtherSize += size;
                    stats.OtherFileCount++;
                }
            }

            // Check for trickplay folders
            var subDirs = _fileSystem.GetDirectories(directoryPath, false);
            foreach (var subDir in subDirs)
            {
                if (subDir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    var trickplaySize = CalculateDirectorySize(subDir.FullName);
                    stats.TrickplaySize += trickplaySize;
                    stats.TrickplayFolderCount++;
                }
                else
                {
                    AnalyzeDirectoryRecursive(subDir.FullName, stats);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not access directory {Path}", directoryPath);
        }
    }

    /// <summary>
    /// Calculates the total size of all files in a directory tree.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <returns>The total size in bytes.</returns>
    private long CalculateDirectorySize(string directoryPath)
    {
        long totalSize = 0;

        try
        {
            var files = _fileSystem.GetFiles(directoryPath, false);
            totalSize += files.Sum(f => f.Length);

            var subDirs = _fileSystem.GetDirectories(directoryPath, false);
            foreach (var subDir in subDirs)
            {
                totalSize += CalculateDirectorySize(subDir.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not access directory {Path}", directoryPath);
        }

        return totalSize;
    }
}