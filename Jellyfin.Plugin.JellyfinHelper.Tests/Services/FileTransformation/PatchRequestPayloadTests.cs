using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FileTransformation;

/// <summary>
///     Tests for PatchRequestPayload - a JSON DTO used by the File Transformation plugin callback.
/// </summary>
public class PatchRequestPayloadTests
{
    [Fact]
    public void Deserialize_LowercaseContentsProperty_PopulatesContents()
    {
        // The File Transformation plugin sends {"contents":"<html>..."} - property name is lowercase.
        var json = "{\"contents\":\"<html>hello</html>\"}";
        var payload = JsonSerializer.Deserialize<PatchRequestPayload>(json);
        Assert.NotNull(payload);
        Assert.Equal("<html>hello</html>", payload!.Contents);
    }

    [Fact]
    public void Deserialize_MissingContents_LeavesPropertyNull()
    {
        var payload = JsonSerializer.Deserialize<PatchRequestPayload>("{}");
        Assert.NotNull(payload);
        Assert.Null(payload!.Contents);
    }

    [Fact]
    public void Deserialize_ExplicitNullContents_LeavesPropertyNull()
    {
        var payload = JsonSerializer.Deserialize<PatchRequestPayload>("{\"contents\":null}");
        Assert.NotNull(payload);
        Assert.Null(payload!.Contents);
    }

    [Fact]
    public void Deserialize_EmptyString_ResultsInEmptyContents()
    {
        var payload = JsonSerializer.Deserialize<PatchRequestPayload>("{\"contents\":\"\"}");
        Assert.NotNull(payload);
        Assert.Equal(string.Empty, payload!.Contents);
    }

    [Fact]
    public void Serialize_UsesLowercaseContentsPropertyName()
    {
        // If the [JsonPropertyName] attribute is removed, the property would
        // serialize as "Contents" (PascalCase), breaking the file-transformation callback contract.
        var payload = new PatchRequestPayload { Contents = "abc" };
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("\"contents\":\"abc\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Contents\":", json, StringComparison.Ordinal);
    }
}