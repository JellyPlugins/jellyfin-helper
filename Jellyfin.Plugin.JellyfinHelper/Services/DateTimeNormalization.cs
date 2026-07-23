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
    ///     Local values are converted; Unspecified values are reinterpreted as UTC without
    ///     any offset adjustment. Callers must not pass a value that is actually local time
    ///     with <see cref="DateTimeKind.Unspecified" /> — the resulting timestamp will be
    ///     wrong by the server's UTC offset.
    /// </summary>
    /// <param name="value">The DateTime value to normalize.</param>
    /// <returns>The UTC-normalized DateTime.</returns>
    internal static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // WARNING: Unspecified kind is assumed to be UTC (no offset conversion).
            // If the value is actually local time marked as Unspecified, the result will be
            // wrong by the server's UTC offset. Callers must guarantee this does not happen.
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