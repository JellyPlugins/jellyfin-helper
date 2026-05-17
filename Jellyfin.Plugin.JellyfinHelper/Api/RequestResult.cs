namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Result of a discovery request submission.
/// </summary>
public sealed class RequestResult
{
    /// <summary>
    ///     Gets or sets a value indicating whether the request was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Gets or sets the result message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
