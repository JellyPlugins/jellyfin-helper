using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FileTransformation;

/// <summary>
///     Tests for IndexHtml - the callback the File Transformation plugin invokes when serving index.html.
/// </summary>
public class TransformationPatchesTests
{
    // Null / empty guards

    [Fact]
    public void IndexHtml_NullPayload_ReturnsEmptyString_DoesNotThrow()
    {
        // Reflection into the File Transformation plugin could still pass null in pathological cases (e.g., a Newtonsoft deserialization producing default(T)).
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

    // No </body> anchor

    [Fact]
    public void IndexHtml_NoBodyTag_ReturnsContentUnchanged_NoInjection()
    {
        // If there is no </body> to anchor against, we must NOT append the script tag
        // at some arbitrary location, which would produce invalid HTML.
        const string html = "<html><head></head></html>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });
        Assert.Equal(html, result);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
    }

    // Standard injection

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

    // Idempotence: repeated calls must not stack copies of the script tag

    [Fact]
    public void IndexHtml_CalledTwice_ProducesExactlyOneScriptTag()
    {
        // File Transformation may re-invoke the callback whenever a client reloads. Every invocation must produce a document with exactly one instance of the plugin script tag, not two, not three.
        const string html = "<html><body></body></html>";
        var first = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });
        var second = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = first });

        Assert.Equal(1, CountOccurrences(second, "plugin=\"Jellyfin Helper\""));
    }

    [Fact]
    public void IndexHtml_WithStaleOldVersionTag_ReplacesWithFreshTag()
    {
        // Simulates upgrading the plugin. An old v0.9 tag is left behind and must
        // be removed by the RemovalRegex before the new tag goes in.
        const string html = "<html><body>" +
                            "<script plugin=\"Jellyfin Helper\" version=\"0.9\" src=\"stale?v=0.9\" defer></script>" +
                            "</body></html>";
        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        Assert.DoesNotContain("stale", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"0.9\"", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "plugin=\"Jellyfin Helper\""));
    }

    // The transformation must not clobber unrelated content

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
        // Contract: the injection target is the real closing </body>, the last one. A stray literal `</body>` inside an HTML comment must NOT trap the injection inside a non-executing region.
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
        // real closing `</body>`, meaning after the comment occurrence and before the last.
        Assert.True(scriptIndex > firstBody,
            $"script must appear AFTER the </body> inside the comment (script={scriptIndex}, commentBody={firstBody})");
        Assert.True(scriptIndex < lastBody,
            $"script must appear BEFORE the real closing </body> (script={scriptIndex}, realBody={lastBody})");
    }

    [Fact]
    public void IndexHtml_LiteralBodyStringAfterRealClosingTag_InjectsBeforeRealBody()
    {
        // Contract: the </body> search is bounded to content before </html>, so a stray literal `</body>` that appears AFTER </html> (e.g.
        const string html =
            "<html>" +
            "<body>" +
            "<div>real content</div>" +
            "</body>" +
            "</html>" +
            "<script>var x=\"</body>\";</script>";

        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        var scriptIndex = result.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal);
        var htmlCloseIndex = result.IndexOf("</html>", StringComparison.OrdinalIgnoreCase);

        Assert.True(scriptIndex >= 0, "script tag must be present in the transformed output");
        Assert.True(scriptIndex < htmlCloseIndex,
            $"script must be injected before </html>, not after it; script={scriptIndex}, htmlClose={htmlCloseIndex}");
    }

    // Helpers

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

    [Fact]
    public void IndexHtml_BodyTagInsideTemplate_InjectsBeforeRealBodyTag()
    {
        const string html = "<html><body><template id=\"x\">fake</body>in template</template></body></html>";
        var payload = new PatchRequestPayload { Contents = html };

        var result = TransformationPatches.IndexHtml(payload);

        // Script must appear after </template>, not inside it.
        var templateClose = result.IndexOf("</template>", StringComparison.OrdinalIgnoreCase);
        var scriptPos = result.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        Assert.True(scriptPos > templateClose,
            "Script tag must be injected after </template>, not inside it.");
    }

    [Fact]
    public void IndexHtml_OnDiskFallbackTagThenCallback_ProducesExactlyOneTag()
    {
        // For the "always run the disk fallback" design: when File Transformation IS installed AND the disk fallback has ALSO already written the tag to index.html, the File Transformation callback runs on that already-tagged on-disk content.
        var onDiskOldVersionTag = DiscoveryScriptTag.Build("0.0.1-old");
        var diskInjected = "<html><body><div>app</div>\n" + onDiskOldVersionTag + "\n</body></html>";

        var served = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = diskInjected });

        Assert.Equal(1, CountOccurrences(served, "plugin=\"Jellyfin Helper\""));
        // The stale on-disk version must have been stripped, leaving only the freshly emitted tag.
        Assert.DoesNotContain("0.0.1-old", served, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexHtml_ClosingBodyAfterHtmlClose_NotChosenAsAnchor_ReturnsContentWithoutInjection()
    {
        // The </body> search is bounded to text BEFORE </html>. Here the only literal </body> sits after </html> (inside a trailing script string), so nothing is found in-bounds: bodyIndex is -1 and the content must come back untouched.
        const string html =
            "<html>" +
            "<head></head>" +
            "</html>" +
            "<script>var x=\"</body>\";</script>";

        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", result, StringComparison.Ordinal);
        Assert.Equal(html, result);
    }

    [Fact]
    public void IndexHtml_NoHtmlAndNoBodyAnchor_ReturnsContentVerbatim()
    {
        // A bare fragment with neither </html> nor </body>: searchEnd falls back to the full length and LastIndexOf(</body>) returns -1, so the method reaches its final return with no injection and hands the content straight back.
        const string html = "<div>just a fragment, no closing body or html here</div>";

        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        Assert.Equal(html, result);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndexHtml_LiteralBodyInsideHeadScript_NotChosenAsAnchor_InjectsBeforeRealBody()
    {
        // A literal "</body>" sits inside a script string in the head, before the real body. LastIndexOf (bounded to before </html>) must skip it and pick the real </body>, otherwise the tag lands inside a non-executing JS string in the head.
        const string html =
            "<html><head><script>var x=\"</body>\";</script></head><body><div>real</div></body></html>";

        var result = TransformationPatches.IndexHtml(new PatchRequestPayload { Contents = html });

        Assert.Equal(1, CountOccurrences(result, "plugin=\"Jellyfin Helper\""));

        var scriptIndex = result.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal);
        var headScriptIndex = result.IndexOf("var x=", StringComparison.Ordinal);
        var realBodyIndex = result.LastIndexOf("</body>", StringComparison.Ordinal);

        Assert.True(scriptIndex >= 0, "script tag must be present in the transformed output");
        // After the head script (the head "</body>" string was not the anchor).
        Assert.True(scriptIndex > headScriptIndex,
            $"script must appear after the head script (script={scriptIndex}, headScript={headScriptIndex})");
        // Before the real closing </body>.
        Assert.True(scriptIndex < realBodyIndex,
            $"script must appear before the real </body> (script={scriptIndex}, realBody={realBodyIndex})");
    }
}