namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>The outcome of a request intent, including a confirmed existing request.</summary>
/// <param name="Success">Whether the intent was accepted.</param>
/// <param name="Message">A client-safe description.</param>
public sealed record SeerrRequestResult(bool Success, string Message)
{
    /// <summary>Gets a value indicating whether this title was already requested.</summary>
    public bool AlreadyRequested { get; init; }

    /// <summary>Gets or initializes a value indicating whether an attempted submission may have been accepted despite a failed transport/status response.</summary>
    public bool SubmissionUncertain { get; init; }

    /// <summary>Creates a successful intent without claiming a second upstream request was created.</summary>
    /// <returns>A successful already-requested intent.</returns>
    internal static SeerrRequestResult ExistingRequest() => new(true, "Title has already been requested.") { AlreadyRequested = true };
}
