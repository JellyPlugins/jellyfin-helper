namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
///     A single record of a config setter clamping an out-of-range value. Surfaced by the
///     Plugin startup path so admins who hand-edit the XML see immediately when a value
///     was narrowed to the accepted range.
/// </summary>
/// <param name="PropertyName">The name of the affected configuration property.</param>
/// <param name="RawValue">The original value the setter received (invariant culture).</param>
/// <param name="ClampedValue">The clamped value that was actually stored (invariant culture).</param>
public sealed record ClampReportEntry(string PropertyName, string RawValue, string ClampedValue);