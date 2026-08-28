using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Thrown by HttpResponseReader when a response body exceeds the configured size limit.
/// </summary>
public sealed class ResponseTooLargeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ResponseTooLargeException" /> class.</summary>
    public ResponseTooLargeException()
        : base("Response too large")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ResponseTooLargeException" /> class.</summary>
    /// <param name="message">The message.</param>
    public ResponseTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ResponseTooLargeException" /> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ResponseTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
