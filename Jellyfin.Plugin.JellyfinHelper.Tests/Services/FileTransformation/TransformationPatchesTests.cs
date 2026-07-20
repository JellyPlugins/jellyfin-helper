using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FileTransformation;

/// <summary>
///     Tests for <see cref="TransformationPatches.IndexHtml"/> — the callback the File
///     Transformation plugin invokes when serving index.html. The method must:
///     <list type="bullet">
///         <item>Tolerate <c>null</c> payload / <c>null</c> / empty <c>Contents</c> without throwing.</item>
///         <item>Not append a script tag when there is no <c>&lt;/body&gt;</c> anchor.</item>
///         <item>Insert the script tag exactly once, right before the last (real) <c>&lt;/body&gt;</c>
///               (LastIndexOf is used so a stray literal <c>&lt;/body&gt;</c> inside an HTML comment
///               or CDATA block does not steal the injection target).</item>
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
    public void IndexHtml_WithLiteralBodyInsideComment_InjectsBeforeRealClosingBody()
    {
        // Contract: the injection target is the REAL closing </body> — the last one.
        // A stray literal `</body>` inside an HTML comment must NOT trap the injection
        // inside a non-executing region. This fixture places a literal `</body>` inside
        // an HTML comment BEFORE the real closing tag; the implementation must skip
        // past the comment occurrence and use the last one.
        const string html =
            "<html>" +
            "<body>" +
            "<!-- example: </body> -->" +
            "<div>real content</div>" +
            "</body>" +
            "</html>";

        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        var scriptIndex = result.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal);
        var firstBody = result.IndexOf("</body>", StringComparison.Ordinal);
        var lastBody = result.LastIndexOf("</body>", StringComparison.Ordinal);

        Assert.True(scriptIndex >= 0, "script tag must be present in the transformed output");
        // Sanity: our fixture must actually contain two distinct </body> positions,
        // otherwise the test degenerates to a single-occurrence check.
        Assert.NotEqual(firstBody, lastBody);
        // Locked contract: the script MUST land BETWEEN the comment's `</body>` and the
        // real closing `</body>` — i.e. after the comment occurrence, before the last.
        Assert.True(scriptIndex > firstBody,
            $"script must appear AFTER the </body> inside the comment (script={scriptIndex}, commentBody={firstBody})");
        Assert.True(scriptIndex < lastBody,
            $"script must appear BEFORE the real closing </body> (script={scriptIndex}, realBody={lastBody})");
    }

    [Fact]
    public void IndexHtml_LiteralBodyStringAfterRealClosingTag_InjectsAtLastOccurrence()
    {
        // Documented contract: TransformationPatches uses `LastIndexOf("</body>")` to
        // pick the injection point. This test PINS that contract for the pathological
        // input shape where a stray literal `</body>` string appears AFTER the real
        // closing body tag — e.g. left over inside a client-side template literal that
        // was appended to the document.
        //
        // Rationale: well-formed HTML never has content after </html>, and the real
        // </body> is always the last one in that case. The stray-after-real fixture
        // below is intentionally malformed input; we assert on the current behaviour
        // (last-occurrence wins) so that ANY future change to the injection point
        // strategy has to update this test and consciously acknowledge the change.
        // Silently switching to IndexOf would cause the script to be injected before
        // the FIRST </body>, subtly changing runtime behaviour on real-world documents
        // that contain stray `</body>` strings in inline template code.
        const string html =
            "<html>" +
            "<body>" +
            "<div>real content</div>" +
            "</body>" +
            "</html>" +
            "<script>var x=\"</body>\";</script>";

        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        var scriptIndex = result.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal);
        var firstBody = result.IndexOf("</body>", StringComparison.Ordinal);
        var lastBody = result.LastIndexOf("</body>", StringComparison.Ordinal);

        Assert.True(scriptIndex >= 0, "script tag must be present in the transformed output");
        Assert.NotEqual(firstBody, lastBody);
        // Locked contract: the injection point is the LAST occurrence. Our fixture
        // makes the last one a stray inside inline JS text — the current implementation
        // treats it as the target, so the script lands AFTER the real </body>.
        // This is a documented consequence of the LastIndexOf strategy.
        Assert.True(scriptIndex > firstBody,
            $"script must appear AFTER the real </body> (LastIndexOf semantics); script={scriptIndex}, realBody={firstBody}");
        Assert.True(scriptIndex < lastBody,
            $"script must land immediately before the LAST </body> occurrence (the stray); script={scriptIndex}, strayBody={lastBody}");
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