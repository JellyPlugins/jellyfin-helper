using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
///     Reusable filesystem operations with error handling.
///     All methods gracefully handle <see cref="IOException" /> and <see cref="UnauthorizedAccessException" />
///     by skipping silently (best-effort), ensuring that inaccessible directories never crash the caller.
/// </summary>
public static class FileSystemHelper
{
    /// <summary>
    ///     Calculates the total size of all files in a directory tree (iterative, no recursion).
    ///     Symlinks and junction points are skipped to prevent cycles.
    ///     Inaccessible directories are silently skipped.
    /// </summary>
    /// <param name="path">The root directory path.</param>
    /// <returns>The total size in bytes.</returns>
    public static long CalculateDirectorySize(string path)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                        // Intentionally empty: skip an unreadable file and keep summing the rest.
                    }
                }

                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    stack.Push(sub);
                }
            }
            catch (IOException)
            {
                // Intentionally empty: an unreadable directory is skipped (best-effort size scan).
            }
            catch (UnauthorizedAccessException)
            {
                // Intentionally empty: an inaccessible directory is skipped (best-effort size scan).
            }
        }

        return total;
    }

    /// <summary>
    ///     Increments a counter in a dictionary by 1.
    /// </summary>
    /// <param name="dict">The dictionary to update.</param>
    /// <param name="key">The key to increment.</param>
    internal static void IncrementCount(Dictionary<string, int> dict, string key)
    {
        if (dict.TryGetValue(key, out var current))
        {
            dict[key] = current + 1;
        }
        else
        {
            dict[key] = 1;
        }
    }

    /// <summary>
    ///     Accumulates a value in a dictionary.
    /// </summary>
    /// <param name="dict">The dictionary to update.</param>
    /// <param name="key">The key to accumulate.</param>
    /// <param name="value">The value to add.</param>
    internal static void AccumulateValue(Dictionary<string, long> dict, string key, long value)
    {
        if (dict.TryGetValue(key, out var current))
        {
            dict[key] = current + value;
        }
        else
        {
            dict[key] = value;
        }
    }

    /// <summary>
    ///     Adds a file path to a dictionary of path collections, creating the collection if needed.
    /// </summary>
    /// <param name="dict">The dictionary mapping keys to path collections.</param>
    /// <param name="key">The key (e.g. codec name) to add the path under.</param>
    /// <param name="path">The file path to add.</param>
    internal static void AddPath(Dictionary<string, Collection<string>> dict, string key, string path)
    {
        if (!dict.TryGetValue(key, out var collection))
        {
            collection = [];
            dict[key] = collection;
        }

        collection.Add(path);
    }
}