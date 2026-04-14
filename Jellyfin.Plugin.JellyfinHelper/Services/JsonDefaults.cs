using System.Text.Json;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Provides shared <see cref="JsonSerializerOptions"/> for consistent JSON serialization
/// across all plugin services. Using a single instance avoids duplicate allocations
/// and ensures consistent behavior (casing, indentation, case-insensitivity).
/// </summary>
internal static class JsonDefaults
{
    /// <summary>
    /// Gets the shared JSON serializer options used by all plugin services.
    /// Configured with camelCase property naming, indented output, and case-insensitive deserialization.
    /// </summary>
    internal static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}