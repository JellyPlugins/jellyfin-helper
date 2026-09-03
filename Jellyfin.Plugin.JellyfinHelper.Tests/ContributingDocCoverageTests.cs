using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Documentation drift guard: every tracked source/test file under the two project directories must be listed in the CONTRIBUTING.md project-structure tree.
/// </summary>
public sealed class ContributingDocCoverageTests
{
    private static readonly string[] DocExtensions = [".cs", ".html", ".css", ".js"];

    // Project directories whose files must be documented, relative to the repo root.
    private static readonly string[] ProjectDirs =
    [
        "Jellyfin.Plugin.JellyfinHelper",
        "Jellyfin.Plugin.JellyfinHelper.Tests",
    ];

    // Git-ignored / generated files that must NOT be required in the docs.
    // configPage.html is composed at build time (see .gitignore); it is not source.
    private static readonly string[] ExcludedRelativePaths =
    [
        Path.Combine("Jellyfin.Plugin.JellyfinHelper", "PluginPages", "configPage.html"),
    ];

    // Filenames that legitimately appear in the tree but have no tracked source file: they are
    // composed/generated at build time. The reverse guard must not flag these as phantom entries.
    private static readonly HashSet<string> GeneratedTreeEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "configPage.html",
    };

    [Fact]
    public void EverySourceAndTestFile_IsListedInContributingMd()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var contributingPath = Path.Combine(repoRoot!, "CONTRIBUTING.md");
        Assert.True(File.Exists(contributingPath), $"CONTRIBUTING.md not found at {contributingPath}");
        var contributing = File.ReadAllText(contributingPath);

        var undocumented = new List<string>();

        foreach (var projectDir in ProjectDirs)
        {
            var root = Path.Combine(repoRoot!, projectDir);
            Assert.True(Directory.Exists(root), $"project directory not found: {root}");

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!DocExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip build output - never source.
                var relative = Path.GetRelativePath(repoRoot!, file);
                if (IsInBuildOutput(relative) || IsExcluded(relative))
                {
                    continue;
                }

                // The tree lists files by bare filename, so match on that.
                var fileName = Path.GetFileName(file);
                if (!contributing.Contains(fileName, StringComparison.Ordinal))
                {
                    undocumented.Add(relative.Replace('\\', '/'));
                }
            }
        }

        Assert.True(
            undocumented.Count == 0,
            "These source/test files are not listed in CONTRIBUTING.md - add each to the "
            + "Project Structure / Test Structure tree (a bare `│   ├── <file>` line is enough):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, undocumented.OrderBy(p => p, StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryTreeEntry_HasABackingFile()
    {
        // Reverse of the coverage guard: a filename that appears in the project-structure tree but
        // has no file on disk is stale documentation. This is what let a renamed/removed file linger
        // in the tree unnoticed, because the forward guard only flags the opposite direction.
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var realFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectDir in ProjectDirs)
        {
            var root = Path.Combine(repoRoot!, projectDir);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot!, file);
                if (IsInBuildOutput(relative))
                {
                    continue;
                }

                realFileNames.Add(Path.GetFileName(file));
            }
        }

        var contributingLines = File.ReadAllLines(Path.Combine(repoRoot!, "CONTRIBUTING.md"));
        var phantom = new List<string>();

        foreach (var line in contributingLines)
        {
            // Only inspect actual tree rows (they carry a box-drawing connector); prose that mentions
            // a filename in backticks or a "create css/YourTab.css" example is intentionally ignored.
            var connectorIndex = line.IndexOf("├──", StringComparison.Ordinal);
            if (connectorIndex < 0)
            {
                connectorIndex = line.IndexOf("└──", StringComparison.Ordinal);
            }

            if (connectorIndex < 0)
            {
                continue;
            }

            var afterConnector = line[(connectorIndex + 3)..].Trim();
            var fileName = afterConnector.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(fileName)
                || !DocExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Glob patterns (css/*.css) and path-prefixed entries name a group, not a single file, so
            // they cannot have one backing file. Only bare filenames are verifiable against disk.
            if (fileName.Contains('*', StringComparison.Ordinal) || fileName.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            if (!realFileNames.Contains(fileName) && !GeneratedTreeEntries.Contains(fileName))
            {
                phantom.Add($"{fileName}  (line: {line.Trim()})");
            }
        }

        Assert.True(
            phantom.Count == 0,
            "These entries appear in the CONTRIBUTING.md tree but have no matching file on disk - "
            + "fix the name or remove the line:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, phantom.OrderBy(p => p, StringComparer.Ordinal)));
    }

    private static bool IsInBuildOutput(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase)
                              || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExcluded(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return ExcludedRelativePaths.Any(x => normalized.Equals(x, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
