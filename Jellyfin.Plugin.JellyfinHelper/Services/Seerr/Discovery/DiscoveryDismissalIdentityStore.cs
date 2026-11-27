using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Permanent typed dismissal identities, with a storage budget independent of fitting rows.</summary>
internal sealed class DiscoveryDismissalIdentityStore
{
    internal const long MaximumStoreBytes = 256 * 1024 * 1024;
    private const long MaximumUserBytes = 128 * 1024 * 1024;
    private const int MaximumUsers = 4096;
    private const int MaximumIdentitiesPerUser = 4 * 1024 * 1024;
    private readonly string _directory;
    private readonly Action<string, string> _write;
    private readonly Func<string, Stream> _read;
    private readonly long _budget;
    private readonly int _identityLimit;
    private readonly int _userLimit;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, long> _files = [];
    private readonly ConcurrentDictionary<Guid, Lazy<UserIndex>> _users = new();
    private long _bytes;
    private int _reservedUsers;

    internal DiscoveryDismissalIdentityStore(string directory, Action<string, string>? write = null, Func<string, Stream>? read = null, Func<string, IEnumerable<string>>? enumerate = null, long byteBudget = MaximumStoreBytes, int identityLimit = MaximumIdentitiesPerUser, int userLimit = MaximumUsers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteBudget, MaximumStoreBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identityLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(identityLimit, MaximumIdentitiesPerUser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(userLimit, MaximumUsers);
        _directory = directory;
        _write = write ?? ((path, contents) => AtomicFile.WriteAllText(path, contents));
        _read = read ?? File.OpenRead;
        _budget = byteBudget;
        _identityLimit = identityLimit;
        _userLimit = userLimit;
        LoadInventory(enumerate ?? (path => Directory.EnumerateFiles(path, "*.json")));
        // Reserve every inventoried owner's reader slot without loading any user file.
        foreach (var user in _files.Keys)
        {
            _users[user] = CreateIndex(user);
        }
    }

    internal ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> Snapshot(Guid user)
        => _users.TryGetValue(user, out var index)
            ? LoadIndex(user, index).Snapshot
            : ImmutableDictionary<(int, string), DateTime?>.Empty;

    // Never performs I/O; serving callers compare this immutable publication
    // with their preloaded snapshot while holding the feedback handoff gate.
    internal ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> CachedSnapshot(Guid user)
        => _users.TryGetValue(user, out var index) && index.IsValueCreated
            ? index.Value.Snapshot
            : ImmutableDictionary<(int, string), DateTime?>.Empty;

    internal void Remember(Guid user, IReadOnlyCollection<Identity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count == 0)
        {
            return;
        }

        if (user == Guid.Empty || identities.Count > _identityLimit)
        {
            throw new IOException("Permanent dismissal identity admission is invalid or exceeds its bounded limit.");
        }

        lock (_gate)
        {
            if (!_files.ContainsKey(user) && _files.Count >= _userLimit)
            {
                throw new IOException("The permanent dismissal user inventory is full; existing reader slots remain reserved for their owners.");
            }
        }

        var (registration, index) = AcquireWriteLease(user);
        try
        {
            index.WithWriteLock(() =>
            {
                var original = index.Snapshot;
                var updated = Merge(original, identities, _identityLimit);
                if (ReferenceEquals(original, updated))
                {
                    return;
                }

                var document = new UserDocument(user, updated.OrderBy(pair => pair.Key.MediaType, StringComparer.Ordinal).ThenBy(pair => pair.Key.TmdbId)
                    .Select(pair => new Identity(pair.Key.TmdbId, pair.Key.MediaType, pair.Value)).ToArray());
                var contents = JsonSerializer.Serialize(document, JsonDefaults.Options);
                Publish(user, index, updated, contents);
            });
        }
        finally
        {
            lock (_gate)
            {
                index.ActiveWriters--;
                if (index.ActiveWriters == 0 && !_files.ContainsKey(user) && index.Snapshot.IsEmpty
                    && _users.TryGetValue(user, out var current) && ReferenceEquals(current, registration))
                {
                    _users.TryRemove(user, out _);
                }
            }
        }
    }

    private static ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> Merge(ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> original, IEnumerable<Identity> identities, int limit)
    {
        var updated = original.ToBuilder();
        var modified = false;
        foreach (var identity in identities)
        {
            Validate(identity);
            var key = (identity.TmdbId, identity.MediaType);
            var incoming = identity.DismissedAtUtc is { } time ? DateTimeNormalization.ToUtc(time) : (DateTime?)null;
            if (!updated.TryGetValue(key, out var previous) || (incoming.HasValue && (!previous.HasValue || incoming > previous)))
            {
                updated[key] = incoming;
                modified = true;
            }
        }

        if (updated.Count > limit)
        {
            throw new IOException("The permanent dismissal user index is full; existing identities remain preserved.");
        }

        return modified ? updated.ToImmutable() : original;
    }

    private void Publish(Guid user, UserIndex index, ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> snapshot, string contents)
    {
        var length = Encoding.UTF8.GetByteCount(contents);
        long previous;
        long growth;
        lock (_gate)
        {
            previous = _files.GetValueOrDefault(user);
            growth = Math.Max(0, length - previous);
            if (length > MaximumUserBytes || _bytes + growth > _budget || (previous == 0 && _files.Count + _reservedUsers >= _userLimit))
            {
                throw new IOException("The independent permanent dismissal index is full; fitting storage was not charged.");
            }

            _bytes += growth;
            if (previous == 0)
            {
                _reservedUsers++;
            }
        }

        var saved = false;
        try
        {
            _write(PathFor(user), contents);
            lock (_gate)
            {
                _files[user] = length;
                index.Publish(snapshot);
            }

            saved = true;
        }
        finally
        {
            lock (_gate)
            {
                _bytes -= saved ? previous + growth - length : growth;
                if (previous == 0)
                {
                    _reservedUsers--;
                }
            }
        }
    }

    private (Lazy<UserIndex> Registration, UserIndex Index) AcquireWriteLease(Guid user)
    {
        while (true)
        {
            Lazy<UserIndex> registration;
            lock (_gate)
            {
                if (!_users.TryGetValue(user, out registration!))
                {
                    if (_users.Count >= _userLimit)
                    {
                        throw new IOException("The bounded permanent dismissal reader registry is full.");
                    }

                    registration = CreateIndex(user);
                    _users[user] = registration;
                }
            }

            var index = LoadIndex(user, registration);
            lock (_gate)
            {
                // Loading is outside the gate: an empty registration may have been
                // released in the meantime. Lease only the current registration,
                // including writers still waiting for its per-user write lock.
                if (_users.TryGetValue(user, out var current) && ReferenceEquals(current, registration))
                {
                    index.ActiveWriters++;
                    return (registration, index);
                }
            }
        }
    }

    private UserIndex LoadIndex(Guid user, Lazy<UserIndex> registration)
    {
        try
        {
            return registration.Value;
        }
        catch
        {
            lock (_gate)
            {
                if (_users.TryGetValue(user, out var current) && ReferenceEquals(current, registration))
                {
                    if (_files.ContainsKey(user))
                    {
                        // A failed cold read must remain retryable without giving
                        // an inventoried owner's slot to a new owner.
                        _users[user] = CreateIndex(user);
                    }
                    else
                    {
                        _users.TryRemove(user, out _);
                    }
                }
            }

            throw;
        }
    }

    private Lazy<UserIndex> CreateIndex(Guid user)
        => new(() => LoadUser(user), LazyThreadSafetyMode.ExecutionAndPublication);

    private UserIndex LoadUser(Guid user)
    {
        bool existed;
        lock (_gate)
        {
            existed = _files.ContainsKey(user);
        }

        try
        {
            using var stream = _read(PathFor(user));
            if (stream.CanSeek && stream.Length > MaximumUserBytes)
            {
                throw new IOException("Permanent dismissal evidence exceeds its bounded read limit.");
            }

            var document = JsonSerializer.Deserialize<UserDocument>(stream, JsonDefaults.Options)
                ?? throw new IOException("A permanent dismissal user index is unavailable.");
            if (document.UserId != user || document.Identities is null || document.Identities.Length > _identityLimit)
            {
                throw new IOException("Permanent dismissal index ownership or size is invalid.");
            }

            return new UserIndex(Merge(ImmutableDictionary<(int, string), DateTime?>.Empty, document.Identities, _identityLimit));
        }
        catch (Exception exception) when (!existed && exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new UserIndex(ImmutableDictionary<(int, string), DateTime?>.Empty);
        }
        catch (Exception exception) when (exception is JsonException or UnauthorizedAccessException)
        {
            throw new IOException("Permanent dismissal evidence could not be read; identities were not discarded.", exception);
        }
    }

    private void LoadInventory(Func<string, IEnumerable<string>> enumerate)
    {
        var directoryObserved = false;
        try
        {
            var attributes = File.GetAttributes(_directory);
            directoryObserved = true;
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("The permanent dismissal index path is not a directory.");
            }

            foreach (var path in enumerate(_directory).Take(_userLimit + 1))
            {
                var length = new FileInfo(path).Length;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var owner) || owner == Guid.Empty
                    || length > MaximumUserBytes || _files.Count >= _userLimit || _bytes + length > _budget || !_files.TryAdd(owner, length))
                {
                    throw new IOException("Existing permanent dismissal identities exceed their independent bounds or have invalid ownership.");
                }

                _bytes += length;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only the absence of the whole index directory is an empty store.
            if (directoryObserved)
            {
                throw;
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Permanent dismissal identity storage is inaccessible.", exception);
        }
    }

    private static void Validate(Identity identity)
    {
        if (identity is null || identity.TmdbId <= 0 || identity.MediaType is not ("movie" or "tv"))
        {
            throw new IOException("A permanent dismissal identity has invalid typed provenance.");
        }
    }

    private string PathFor(Guid owner) => Path.Join(_directory, owner.ToString("N") + ".json");

    internal sealed record Identity(int TmdbId, string MediaType, DateTime? DismissedAtUtc = null);

    private sealed record UserDocument(Guid UserId, Identity[] Identities);

    private sealed class UserIndex(ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> snapshot)
    {
        private readonly Lock _writeGate = new();
        private ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> _snapshot = snapshot;

        // Includes queued writers; read and changed only under the store's registry gate.
        internal int ActiveWriters { get; set; }

        internal ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> Snapshot => Volatile.Read(ref _snapshot);

        internal void Publish(ImmutableDictionary<(int TmdbId, string MediaType), DateTime?> value)
            => Volatile.Write(ref _snapshot, value);

        internal void WithWriteLock(Action operation)
        {
            lock (_writeGate)
            {
                operation();
            }
        }
    }
}
