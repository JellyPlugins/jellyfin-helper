using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>Receives a consistent history boundary before any later playback observation is accepted.</summary>
internal interface IUserActivityHistoryConsumer
{
    bool NeedsCurrentState(DateTimeOffset now, IReadOnlyCollection<User> users);

    void Tick(DateTimeOffset now, IReadOnlyCollection<User> users, Func<Guid, UserActivityHistory> history, bool hasNewBaseline);

    IEnumerable<Guid> KnownUsers();

    void DeleteUser(Guid userId);
}
