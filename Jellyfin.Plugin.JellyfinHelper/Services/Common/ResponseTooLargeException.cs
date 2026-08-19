using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Thrown by <see cref="HttpResponseReader" /> when a response body exceeds the configured size
///     limit. A dedicated type (instead of a bare <see cref="InvalidOperationException" />) lets call
///     sites catch the oversize condition precisely without masking unrelated invalid-operation bugs.
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
