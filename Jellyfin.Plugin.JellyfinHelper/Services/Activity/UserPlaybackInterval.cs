using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

internal sealed record UserPlaybackInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
