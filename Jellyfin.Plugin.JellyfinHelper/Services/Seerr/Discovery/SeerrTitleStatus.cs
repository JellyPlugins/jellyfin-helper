using System;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Library/request status only; never changes scoring metadata or age certifications.</summary>
internal readonly record struct SeerrTitleStatus(bool Verified, bool AlreadyRequested, bool AlreadyAvailable)
{
    internal string? ErrorMessage { get; init; }

    internal static SeerrTitleStatus Parse(string json, int tmdbId, string mediaType)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !MatchesId(root, "id", tmdbId, required: true)
            || !MatchesType(root, mediaType))
        {
            return default;
        }

        if (!root.TryGetProperty("mediaInfo", out var media) || media.ValueKind == JsonValueKind.Null)
        {
            return new(true, false, false);
        }

        if (media.ValueKind != JsonValueKind.Object || !MatchesId(media, "tmdbId", tmdbId, required: false)
            || !MatchesType(media, mediaType) || !media.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var value) || value is < 1 or > 6)
        {
            return default;
        }

        // Pending/processing proves an active standard request. Unknown/deleted media alone does not.
        var requested = value is 2 or 3;
        if (media.TryGetProperty("requests", out var requests))
        {
            if (requests.ValueKind != JsonValueKind.Array)
            {
                return default;
            }

            foreach (var request in requests.EnumerateArray())
            {
                if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("status", out var requestStatus)
                    || requestStatus.ValueKind != JsonValueKind.Number || !requestStatus.TryGetInt32(out var requestValue)
                    || requestValue is < 1 or > 5)
                {
                    return default;
                }

                // A declined, failed or 4K-only request is not a successful standard request intent.
                if (request.TryGetProperty("is4k", out var fourK) && fourK.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return default;
                }

                var is4k = fourK.ValueKind == JsonValueKind.True;
                requested |= !is4k && requestValue is 1 or 2;
            }
        }

        return new(true, requested, value is SeerrMediaStatus.PartiallyAvailable or SeerrMediaStatus.Available);
    }

    private static bool MatchesId(JsonElement element, string property, int id, bool required)
        => element.TryGetProperty(property, out var value) ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == id : !required;

    private static bool MatchesType(JsonElement element, string mediaType)
        => !element.TryGetProperty("mediaType", out var value)
            || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), mediaType, StringComparison.Ordinal));
}
