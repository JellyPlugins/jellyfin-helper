using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Extension methods for <see cref="Exception"/> used throughout the plugin.
/// </summary>
internal static class ExceptionExtensions
{
    /// <summary>
    ///     Returns <see langword="true"/> for exceptions that must never be swallowed:
    ///     <see cref="OutOfMemoryException"/> and <see cref="StackOverflowException"/>.
    ///     Use as a catch filter: <c>catch (Exception ex) when (!ex.IsFatal())</c>.
    /// </summary>
    /// <param name="ex">The exception to test.</param>
    /// <returns>
    ///     <see langword="true"/> if the exception is fatal and must not be caught;
    ///     <see langword="false"/> otherwise.
    /// </returns>
    internal static bool IsFatal(this Exception ex)
        => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
