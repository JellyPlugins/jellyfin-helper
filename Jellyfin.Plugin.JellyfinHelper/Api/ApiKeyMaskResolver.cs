using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Shared resolution logic for the masked API-key sentinel (ApiKeyMask). The GET /Configuration response never returns a real API key: every stored key is replaced with a fixed-length mask so secrets never leave the server in plain text.
/// </summary>
internal static class ApiKeyMaskResolver
{
    /// <summary>
    ///     Determines whether the supplied value is the masked-key sentinel. The comparison is ordinal and trims surrounding whitespace first, so a padded copy (e.g.
    /// </summary>
    /// <param name="candidate">The incoming API key value (may be null).</param>
    /// <returns><see langword="true"/> if the value is the mask sentinel; otherwise <see langword="false"/>.</returns>
    public static bool IsMask(string? candidate)
    {
        return string.Equals(candidate?.Trim(), ConfigurationResponse.ApiKeyMask, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Resolves the effective API key for an incoming Arr instance value against a set of stored instances.
    /// </summary>
    /// <param name="incomingKey">The API key from the request (mask sentinel or a real key).</param>
    /// <param name="url">The URL of the incoming instance, used to match a stored instance.</param>
    /// <param name="name">The name of the incoming instance, used to disambiguate same-URL matches.</param>
    /// <param name="stored">The stored instances to recover a real key from.</param>
    /// <returns>
    ///     The real API key to use. Returns the incoming key unchanged when it is not the mask; returns
    ///     the matched stored key when the mask was sent; returns an empty string when the mask was sent
    ///     but no stored instance matches (callers must treat empty as "cannot test / do not send").
    /// </returns>
    public static string ResolveArrKey(
        string? incomingKey,
        string? url,
        string? name,
        IEnumerable<ArrInstanceConfig> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (!IsMask(incomingKey))
        {
            return incomingKey ?? string.Empty;
        }

        var list = stored as IReadOnlyList<ArrInstanceConfig> ?? stored.ToList();
        var trimmedUrl = url?.Trim();

        // Name+URL first (exact match, handles same-URL collision), then URL-only (handles rename).
        return (list.FirstOrDefault(p =>
                    string.Equals(p.Url?.Trim(), trimmedUrl, StringComparison.OrdinalIgnoreCase)
                    && p.Name == name)
                ?? list.FirstOrDefault(p =>
                    string.Equals(p.Url?.Trim(), trimmedUrl, StringComparison.OrdinalIgnoreCase)))?.ApiKey
               ?? string.Empty;
    }
}
