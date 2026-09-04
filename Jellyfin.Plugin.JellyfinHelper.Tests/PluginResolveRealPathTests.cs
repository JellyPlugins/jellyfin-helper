using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Tests for <see cref="Plugin.ResolveRealPathCore(string, Func{string, string?})"/> - the bounded
///     symlink-resolution helper used to canonicalize the File Transformation assembly location before
///     the plugins-directory origin check. Covers plain paths, single links, cycle termination, and the
///     max-hops cap using an injectable resolver so the bounded traversal is exercised without real symlinks.
/// </summary>
public sealed class PluginResolveRealPathTests
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Rooted(params string[] segments)
    {
        var root = OperatingSystem.IsWindows() ? "C:\\" : "/";
        return Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
    }

    [Fact]
    public void ResolveRealPathCore_PlainPath_ReturnsNormalizedPath()
    {
        var input = Rooted("media", "movies", "file.mkv");

        // No component is a link, so nothing is followed.
        var result = Plugin.ResolveRealPathCore(input, _ => null);

        Assert.Equal(input, result, PathComparer);
    }

    [Fact]
    public void ResolveRealPathCore_SingleSymlink_ResolvesToTarget()
    {
        var link = Rooted("links", "movies");
        var target = Rooted("real", "movies");

        var result = Plugin.ResolveRealPathCore(
            link,
            candidate => PathComparer.Equals(candidate, link) ? target : null);

        Assert.Equal(target, result, PathComparer);
    }

    [Fact]
    public async System.Threading.Tasks.Task SymlinkCycle_TerminatesWithoutThrowing()
    {
        var a = Rooted("cycle", "a");
        var b = Rooted("cycle", "b");

        string Resolver(string candidate)
        {
            if (PathComparer.Equals(candidate, a))
            {
                return b;
            }

            if (PathComparer.Equals(candidate, b))
            {
                return a;
            }

            return null!;
        }

        // A short timeout guards against a regression that reintroduces unbounded recursion.
        string? result = null;
        var task = System.Threading.Tasks.Task.Run(() => result = Plugin.ResolveRealPathCore(a, Resolver));

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(result);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Resolution of a symlink cycle did not terminate.");
        }

        // The last resolved candidate is one of the two cycle nodes; the important guarantee is that
        // it returns a value rather than throwing or hanging.
        Assert.True(
            PathComparer.Equals(result, a) || PathComparer.Equals(result, b),
            $"Unexpected terminal path '{result}'.");
    }

    [Fact]
    public async System.Threading.Tasks.Task AncestorLinkTargetingItsOwnChild_TerminatesWithoutThrowing()
    {
        // An ancestor component links to one of its own descendants (/plugins/link -> /plugins/link/child).
        // Resolving the child canonicalizes the parent, whose leaf resolves back to a path under the same
        // link, so a per-level traversal state would recurse forever and overflow the stack. The shared
        // traversal context must bound this across the ancestor recursion, not just within one level.
        var link = Rooted("plugins", "link");
        var child = Rooted("plugins", "link", "child");

        string Resolver(string candidate) =>
            PathComparer.Equals(candidate, link) ? child : null!;

        string? result = null;
        var task = System.Threading.Tasks.Task.Run(() => result = Plugin.ResolveRealPathCore(child, Resolver));

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Assert.Fail("Resolution of an ancestor-link cycle did not terminate.");
        }

        // The guarantee is termination with a value rather than a stack overflow or a hang.
        Assert.NotNull(result);
    }

    [Fact]
    public void ResolveRealPathCore_ExceedingMaxHops_StopsAtCap()
    {
        // A chain that always points one component deeper never cycles, so only the hop cap can stop it.
        var visitedCandidates = new List<string>();
        var start = Rooted("chain", "start");

        string? Resolver(string candidate)
        {
            // Only the leaf chain follows links; ancestors resolve to themselves so the count reflects
            // the bounded leaf traversal alone.
            if (!candidate.StartsWith(start, StringComparison.Ordinal))
            {
                return null;
            }

            visitedCandidates.Add(candidate);
            return candidate + "x";
        }

        var result = Plugin.ResolveRealPathCore(start, Resolver);

        Assert.NotNull(result);

        // The traversal follows at most MaxLinkHops links before returning, so the number of link
        // components it resolves cannot exceed the cap by more than one (the final candidate that
        // triggers the stop).
        Assert.True(
            visitedCandidates.Count <= Plugin.MaxLinkHops + 1,
            $"Followed {visitedCandidates.Count} links, expected at most {Plugin.MaxLinkHops + 1}.");
    }

    [Fact]
    public void ResolveRealPathCore_IOExceptionFromResolver_Propagates()
    {
        // An OS ELOOP surfaces as IOException from ResolveLinkTarget; the helper must not swallow it so
        // the caller can fail closed.
        var start = Rooted("loop", "node");

        Assert.Throws<IOException>(() =>
            Plugin.ResolveRealPathCore(start, _ => throw new IOException("ELOOP")));
    }

    [Fact]
    public void ResolveRealPath_RealSymlinkChain_ResolvesToFinalTarget()
    {
        // Creating symlinks on Windows typically needs elevation or developer mode; skip there when it
        // is not permitted and rely on the injectable-resolver tests for the bounded traversal logic.
        var tempRoot = Path.Combine(Path.GetTempPath(), "JhResolveRealPath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var targetDir = Path.Combine(tempRoot, "real");
            Directory.CreateDirectory(targetDir);
            var linkPath = Path.Combine(tempRoot, "link");

            try
            {
                Directory.CreateSymbolicLink(linkPath, targetDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Symlink creation requires elevated privileges on some Windows configurations, and
                // xUnit 2.x has no Assert.Skip. Returning early keeps the test green but un-asserted,
                // which is acceptable because the bounded-traversal logic is fully covered by the
                // injectable-resolver tests above; this test only adds coverage where symlinks work.
                return;
            }

            var result = Plugin.ResolveRealPathCore(linkPath, Plugin.RealLeafResolver);

            Assert.Equal(Path.GetFullPath(targetDir), result, PathComparer);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void RealLeafResolver_MultiLinkChain_ResolvesOnlyOneHop()
    {
        // The production resolver must follow a single link hop per call, so the caller's hop counter
        // and cycle set bound a chain the same way on every platform. If it returned the final target
        // instead, a chain longer than MaxLinkHops would slip past the documented bound entirely.
        var tempRoot = Path.Combine(Path.GetTempPath(), "JhLeafResolver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var targetDir = Path.Combine(tempRoot, "real");
            Directory.CreateDirectory(targetDir);
            var firstLink = Path.Combine(tempRoot, "link1");
            var secondLink = Path.Combine(tempRoot, "link2");

            try
            {
                Directory.CreateSymbolicLink(firstLink, targetDir);
                Directory.CreateSymbolicLink(secondLink, firstLink);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Symlink creation needs elevation on some Windows configurations, and xUnit 2.x has no
                // Assert.Skip. Returning early keeps the test green where symlinks are unavailable.
                return;
            }

            // One hop from the outer link lands on the inner link, not on the real directory.
            var oneHop = Plugin.RealLeafResolver(secondLink);

            Assert.Equal(Path.GetFullPath(firstLink), Path.GetFullPath(oneHop!), PathComparer);
            Assert.NotEqual(Path.GetFullPath(targetDir), Path.GetFullPath(oneHop!), PathComparer);

            // The bounded traversal then follows the remaining hops to the final target.
            var full = Plugin.ResolveRealPathCore(secondLink, Plugin.RealLeafResolver);
            Assert.Equal(Path.GetFullPath(targetDir), full, PathComparer);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
