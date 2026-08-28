using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
///     Provides shared JsonSerializerOptions for consistent JSON serialization across all plugin services.
/// </summary>
internal static class JsonDefaults
{
    /// <summary>
    ///     Gets the shared JSON serializer options used by all plugin services. Configured with camelCase property naming, indented output, and case-insensitive deserialization.
    /// </summary>
    private static readonly JsonSerializerOptions _options = CreateOptions();

    internal static JsonSerializerOptions Options => _options;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
    }
}