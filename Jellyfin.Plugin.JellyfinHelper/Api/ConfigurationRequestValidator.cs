using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// Validates <see cref="ConfigurationUpdateRequest"/> fields before they are applied.
/// Extracted from <see cref="ConfigurationController"/> to keep the controller focused on HTTP concerns.
/// </summary>
public static class ConfigurationRequestValidator
{
    /// <summary>Maximum allowed value for day-range fields (OrphanMinAgeDays, TrashRetentionDays).</summary>
    private const int MaxDays = 3650;

    /// <summary>Maximum number of Arr instances per type (Radarr / Sonarr).</summary>
    private const int MaxArrInstances = 3;

    /// <summary>
    /// Validates the given <paramref name="request"/> and returns the first error found, or <c>null</c> if valid.
    /// </summary>
    /// <param name="request">The configuration update request to validate.</param>
    /// <returns>An error message string, or <c>null</c> when the request is valid.</returns>
    public static string? Validate(ConfigurationUpdateRequest request)
    {
        // Numeric range checks
        if (request.OrphanMinAgeDays < 0 || request.OrphanMinAgeDays > MaxDays)
        {
            return "OrphanMinAgeDays must be 0–3650.";
        }

        if (request.TrashRetentionDays < 0 || request.TrashRetentionDays > MaxDays)
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

        // Arr instance format validation (multi-instance lists)
        var error = request.RadarrInstances is not null
            ? ValidateArrInstances(request.RadarrInstances, "Radarr")
            : null;

        error ??= request.SonarrInstances is not null
            ? ValidateArrInstances(request.SonarrInstances, "Sonarr")
            : null;

        // Legacy single-instance fields (promoted by GetEffective*Instances)
        if (error is null && request.RadarrInstances is null && !string.IsNullOrWhiteSpace(request.RadarrUrl))
        {
            error = ValidateArrInstances(
                new List<ArrInstanceConfig> { new() { Name = "Radarr", Url = request.RadarrUrl, ApiKey = request.RadarrApiKey ?? string.Empty } },
                "Radarr");
        }

        if (error is null && request.SonarrInstances is null && !string.IsNullOrWhiteSpace(request.SonarrUrl))
        {
            error = ValidateArrInstances(
                new List<ArrInstanceConfig> { new() { Name = "Sonarr", Url = request.SonarrUrl, ApiKey = request.SonarrApiKey ?? string.Empty } },
                "Sonarr");
        }

        return error;
    }

    /// <summary>
    /// Validates a list of Arr instances for URL format and non-empty API keys.
    /// </summary>
    /// <param name="instances">The instances to validate.</param>
    /// <param name="typeName">The type name (Radarr/Sonarr) for error messages.</param>
    /// <returns>An error message string, or <c>null</c> if all instances are valid.</returns>
    internal static string? ValidateArrInstances(IReadOnlyList<ArrInstanceConfig> instances, string typeName)
    {
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];

            if (instance == null)
            {
                return $"{typeName} instance '#{i + 1}' is null.";
            }

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
                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
                return $"{typeName} instance '{label}' has an invalid URL. Only http:// and https:// URLs are allowed.";
            }

            // If URL is set, API key must also be set
            if (!string.IsNullOrWhiteSpace(instance.Url) && string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
                return $"{typeName} instance '{label}' has a URL but no API key.";
            }
        }

        return null;
    }
}