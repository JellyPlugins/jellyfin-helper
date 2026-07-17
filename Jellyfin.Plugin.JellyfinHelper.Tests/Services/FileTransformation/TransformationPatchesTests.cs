using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FileTransformation;

/// <summary>
///     Tests for <see cref="TransformationPatches.IndexHtml"/> — the callback the File
///     Transformation plugin invokes when serving index.html. The method must:
///     <list type="bullet">
///         <item>Tolerate <c>null</c> payload / <c>null</c> / empty <c>Contents</c> without throwing.</item>
///         <item>Not append a script tag when there is no <c>&lt;/body&gt;</c> anchor.</item>
///         <item>Insert the script tag exactly once, right before the first <c>&lt;/body&gt;</c>.</item>
///         <item>Remove old versions of the plugin script tag before re-inserting the current one
///               (idempotent re-serving).</item>
///         <item>Handle case-insensitive <c>&lt;/body&gt;</c> variants (some HTML minifiers uppercase tags).</item>
///     </list>
///     Note: <see cref="Plugin.Instance"/> may be null during these tests — the code path guards
///     with <c>?.Version.ToString() ?? "unknown"</c>, so the resulting script tag will contain
///     <c>version="unknown"</c>. Assertions target invariants, not the specific version string.
/// </summary>
public class TransformationPatchesTests
{
    // -----------------------------------------------------------------------
    // Null / empty guards
    // -----------------------------------------------------------------------

    [Fact]
    public void IndexHtml_NullPayload_ReturnsEmptyString_DoesNotThrow()
    {
        // Reflection into the File Transformation plugin could still pass null in
        // pathological cases (e.g., a Newtonsoft deserialization producing default(T)).
        // The transformation callback must not tear the pipeline down.
        var result = TransformationPatches.IndexHtml(null!);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void IndexHtml_NullContents_ReturnsEmptyString()
    {
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = null });
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void IndexHtml_EmptyContents_ReturnsEmptyString()
    {
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = string.Empty });
        Assert.Equal(string.Empty, result);
    }

    // -----------------------------------------------------------------------
    // No </body> anchor
    // -----------------------------------------------------------------------

    [Fact]
    public void IndexHtml_NoBodyTag_ReturnsContentUnchanged_NoInjection()
    {
        // If there is no </body> to anchor against, we must NOT append the script tag
        // at some arbitrary location — that would produce invalid HTML.
        const string html = "<html><head></head></html>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });
        Assert.Equal(html, result);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Standard injection
    // -----------------------------------------------------------------------

    [Fact]
    public void IndexHtml_WithBodyTag_InjectsScriptExactlyOnce_BeforeClosingBody()
    {
        const string html = "<html><body><div>content</div></body></html>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        // Exactly one plugin script tag, positioned before </body>.
        var scriptOccurrences = CountOccurrences(result, "plugin=\"Jellyfin Helper\"");
        Assert.Equal(1, scriptOccurrences);

        var scriptIndex = result.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal);
        var closingBodyIndex = result.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(scriptIndex < closingBodyIndex,
            "The plugin script tag must appear before </body>.");
    }

    [Fact]
    public void IndexHtml_WithUppercaseClosingBody_StillInjectsCorrectly()
    {
        // Some HTML minifiers uppercase tag names. IndexOf uses OrdinalIgnoreCase, so
        // this must still succeed.
        const string html = "<HTML><BODY><div>x</div></BODY></HTML>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });
        Assert.Contains("plugin=\"Jellyfin Helper\"", result, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Idempotence: repeated calls must not stack copies of the script tag
    // -----------------------------------------------------------------------

    [Fact]
    public void IndexHtml_CalledTwice_ProducesExactlyOneScriptTag()
    {
        // Regression: File Transformation may re-invoke the callback whenever a client
        // reloads. Every invocation must produce a document with exactly one instance
        // of the plugin script tag — not two, not three.
        const string html = "<html><body></body></html>";
        var first = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });
        var second = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = first });

        Assert.Equal(1, CountOccurrences(second, "plugin=\"Jellyfin Helper\""));
    }

    [Fact]
    public void IndexHtml_WithStaleOldVersionTag_ReplacesWithFreshTag()
    {
        // Simulates upgrading the plugin — an old v0.9 tag is left behind and must
        // be removed by the RemovalRegex before the new tag goes in.
        const string html = "<html><body>" +
                            "<script plugin=\"Jellyfin Helper\" version=\"0.9\" src=\"stale?v=0.9\" defer></script>" +
                            "</body></html>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        Assert.DoesNotContain("stale", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"0.9\"", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "plugin=\"Jellyfin Helper\""));
    }

    // -----------------------------------------------------------------------
    // The transformation must not clobber unrelated content
    // -----------------------------------------------------------------------

    [Fact]
    public void IndexHtml_PreservesUnrelatedScriptTagsAndBodyContent()
    {
        const string html = "<html><body>" +
                            "<script src=\"other-plugin.js\"></script>" +
                            "<div id=\"content\">Very important content</div>" +
                            "</body></html>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        // The other-plugin script must still be there.
        Assert.Contains("other-plugin.js", result, StringComparison.Ordinal);
        Assert.Contains("Very important content", result, StringComparison.Ordinal);
        // Our tag is added.
        Assert.Contains("plugin=\"Jellyfin Helper\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexHtml_WithMultipleBodyTags_InjectsBeforeFirstOccurrence()
    {
        // Documents the current contract: the code uses IndexOf (first occurrence).
        // If a downstream template ever contained multiple </body> substrings (e.g.
        // inside a code sample), we inject before the first one. This test locks that
        // behaviour so it doesn't silently change.
        const string html = "<html><body>outer<pre>&lt;/body&gt;\n</pre></body></html>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        var scriptIndex = result.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal);
        var firstBody = result.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(scriptIndex >= 0);
        Assert.True(scriptIndex < firstBody);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}