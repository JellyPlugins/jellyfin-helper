using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Identifiers of visible cards; all features and ownership are recovered from server-issued deliveries.</summary>
public sealed class DiscoveryImpressionsDto
{
    /// <summary>Gets or sets the server-issued delivery whose cards were displayed; never a client-supplied feature snapshot.</summary>
    [Required]
    public Guid? DeliveryId { get; set; }

    /// <summary>Gets or sets the bounded set of actually visible card identifiers.</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(20)]
    public IReadOnlyList<DiscoveryDismissDto> Items { get; set; } = [];
}
