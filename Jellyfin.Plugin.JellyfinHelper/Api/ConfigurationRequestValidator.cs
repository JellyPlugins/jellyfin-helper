using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Validates <see cref="ConfigurationUpdateRequest" /> fields before they are applied.
///     Extracted from <see cref="ConfigurationController" /> to keep the controller focused on HTTP concerns.
/// </summary>
public static class ConfigurationRequestValidator
{
    /// <summary>Maximum allowed value for day-range fields (OrphanMinAgeDays, TrashRetentionDays).</summary>
    private const int MaxDays = 3650;

    /// <summary>Maximum number of Arr instances per type (Radarr / Sonarr).</summary>
    private const int MaxArrInstances = 3;

    /// <summary>
    ///     Validates the given <paramref name="request" /> and returns the first error found, or <c>null</c> if valid.
    /// </summary>
    /// <param name="request">The configuration update request to validate.</param>
    /// <returns>An error message string, or <c>null</c> when the request is valid.</returns>
    public static string? Validate(ConfigurationUpdateRequest request)
    {
        // Numeric range checks
        if (request.OrphanMinAgeDays is < 0 or > MaxDays)
        {
            return "OrphanMinAgeDays must be 0–3650.";
        }

        if (request.TrashRetentionDays is < 0 or > MaxDays)
        {
            return "TrashRetentionDays must be 0–3650.";
        }

        // Arr instance count limits
        if (request.RadarrInstances is { Count: > MaxArrInstances })
        {
            return $"Maximum {MaxArrInstances} Radarr instances allowed.";
        }

        if (request.SonarrInstances is { Count: > MaxArrInstances })
        {
            return $"Maximum {MaxArrInstances} Sonarr instances allowed.";
        }

        // Seerr settings validation - only enforce range when Seerr is actually configured
        if (!string.IsNullOrWhiteSpace(request.SeerrUrl) &&
            request.SeerrCleanupAgeDays is (< 1 or > MaxDays))
        {
            return "SeerrCleanupAgeDays must be 1–3650.";
        }

        // Validate Seerr URL if provided
        if (!string.IsNullOrWhiteSpace(request.SeerrUrl) &&
            (!Uri.TryCreate(request.SeerrUrl, UriKind.Absolute, out var seerrUri) ||
             (seerrUri.Scheme != "http" && seerrUri.Scheme != "https")))
        {
            return "Seerr URL must be a valid http:// or https:// URL.";
        }

        // If Seerr URL is set, API key must also be set
        if (!string.IsNullOrWhiteSpace(request.SeerrUrl) && string.IsNullOrWhiteSpace(request.SeerrApiKey))
        {
            return "Seerr API key is required when a Seerr URL is configured.";
        }

        // Arr instance format validation (multi-instance lists)
        var error = ValidateArrInstances(request.RadarrInstances, "Radarr");

        error ??= ValidateArrInstances(request.SonarrInstances, "Sonarr");

        return error;
    }

    /// <summary>
    ///     Checks whether <paramref name="trashFolderPath" /> is a relative path that escapes upward via
    ///     <c>..</c> segments. Returns a warning message when suspicious, or <c>null</c> when the path is
    ///     fine. Absolute paths and empty/whitespace values are always considered valid here.
    /// </summary>
    /// <remarks>
    ///     The check is intentionally a warning, not a hard error: <c>ICleanupConfigHelper.GetTrashPath</c>
    ///     already falls back to <c>.jellyfin-trash</c> at runtime, so the system stays safe. The warning
    ///     surfaces the problem to the admin without blocking the save.
    /// </remarks>
    /// <param name="trashFolderPath">The path value from the configuration update request.</param>
    /// <returns>A warning message string, or <c>null</c> when no issue is detected.</returns>
    public static string? ValidateTrashPath(string? trashFolderPath)
    {
        if (string.IsNullOrWhiteSpace(trashFolderPath) || Path.IsPathRooted(trashFolderPath))
        {
            return null;
        }

        // Resolve against a dummy root to detect whether ".." sequences escape upward.
        // We cannot use a real library root here — it is runtime state unknown at config-save time.
        // Use Path.GetTempPath() as a guaranteed-absolute, platform-correct anchor.
        // Path.GetFullPath(path, basePath) is used instead of Path.GetFullPath(Path.Combine(...))
        // to avoid the silent dropped-prefix pitfall when path is rooted (CA2249 / S4347).
        var dummyRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        string resolved;
        try
        {
            resolved = Path.GetFullPath(trashFolderPath, dummyRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"TrashFolderPath '{trashFolderPath}' contains invalid characters or is too long. " +
                   "At runtime it will fall back to '.jellyfin-trash'.";
        }

        var rootPrefix = dummyRoot + Path.DirectorySeparatorChar;

        if (string.Equals(resolved, dummyRoot, StringComparison.OrdinalIgnoreCase))
        {
            return $"TrashFolderPath '{trashFolderPath}' resolves to the library root itself. " +
                   "At runtime it will fall back to '.jellyfin-trash'.";
        }

        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"TrashFolderPath '{trashFolderPath}' is a relative path that escapes the library root " +
                   "via '..' sequences. At runtime it will fall back to '.jellyfin-trash'. " +
                   "Use an absolute path if you intend a location outside the library folder.";
        }

        return null;
    }

    /// <summary>
    ///     Validates a list of Arr instances for URL format and non-empty API keys.
    /// </summary>
    /// <param name="instances">The instances to validate.</param>
    /// <param name="typeName">The type name (Radarr/Sonarr) for error messages.</param>
    /// <returns>An error message string, or <c>null</c> if all instances are valid.</returns>
    internal static string? ValidateArrInstances(IReadOnlyList<ArrInstanceConfig>? instances, string typeName)
    {
        if (instances is null)
        {
            return null;
        }

        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];

            // Skip completely empty instances (user may have added a blank row)
            if (string.IsNullOrWhiteSpace(instance.Url) && string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            // If URL is provided, validate format
            if (!string.IsNullOrWhiteSpace(instance.Url) &&
                (!Uri.TryCreate(instance.Url, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https")))
            {
                var instanceName = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
                return
                    $"{typeName} instance '{instanceName}' has an invalid URL. Only http:// and https:// URLs are allowed.";
            }

            // If URL is set, API key must also be set
            if (string.IsNullOrWhiteSpace(instance.Url) || !string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
            return $"{typeName} instance '{label}' has a URL but no API key.";
        }

        return null;
    }
}