using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
///     Shared UTC normalization helpers. Eliminates duplicated private NormalizeToUtc
///     methods across Activity DTOs and Recommendation DTOs.
/// </summary>
internal static class DateTimeNormalization
{
    /// <summary>
    ///     Normalizes a <see cref="DateTime"/> to UTC.
    ///     Local values are converted; Unspecified values are tagged as UTC.
    /// </summary>
    /// <param name="value">The DateTime value to normalize.</param>
    /// <returns>The UTC-normalized DateTime.</returns>
    internal static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    /// <summary>
    ///     Normalizes a nullable <see cref="DateTime"/> to UTC.
    /// </summary>
    /// <param name="value">The nullable DateTime value to normalize.</param>
    /// <returns>The UTC-normalized DateTime, or null if input is null.</returns>
    internal static DateTime? ToUtc(DateTime? value) =>
        value.HasValue ? ToUtc(value.Value) : null;
}