using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Contains all discovery feedback entries for a single user.
///     Persisted as part of the feedback store for training consumption.
/// </summary>
public sealed class DiscoveryFeedbackResult
{
    /// <summary>
    ///     Gets or sets the Jellyfin user ID.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    ///     Gets or sets the user's display name (for logging/debugging).
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the feedback entries for this user.
    /// </summary>
    [SuppressMessage("Usage", "CA1002:DoNotExposeGenericLists", Justification = "System.Text.Json round-trip")]
    public List<DiscoveryFeedbackEntry> Entries { get; set; } = [];
}