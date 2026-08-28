using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Extension methods for <see cref="Exception"/> used throughout the plugin.
/// </summary>
internal static class ExceptionExtensions
{
    /// <summary>
    ///     Returns true for exceptions that must never be swallowed: OutOfMemoryException and StackOverflowException.
    /// </summary>
    /// <param name="ex">The exception to test.</param>
    /// <returns>
    ///     <see langword="true"/> if the exception is fatal and must not be caught;
    ///     <see langword="false"/> otherwise.
    /// </returns>
    internal static bool IsFatal(this Exception ex)
        => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
