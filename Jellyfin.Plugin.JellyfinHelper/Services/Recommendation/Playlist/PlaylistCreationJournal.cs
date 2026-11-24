using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>Durable pre-create ownership evidence; pending attempts never expire into name-based cleanup.</summary>
internal sealed class PlaylistCreationJournal
{
    private const int MaximumAttempts = 4096;
    private const int MaximumOwnerIds = 4096;
    private const long MaximumEnvelopeBytes = 256 * 1024;
    internal const long MaximumJournalBytes = 64 * 1024 * 1024;
    private readonly string _directory;
    private readonly Action<string, string> _write;
    private readonly Action<string> _delete;
    private readonly Func<string, IEnumerable<string>> _enumerate;
    private readonly long _byteBudget;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Attempt> _entries = [];
    private readonly HashSet<Guid> _writingOwners = [];
    private long _bytes;
    private int _reservedNew;

    internal PlaylistCreationJournal(string directory, Action<string, string>? write = null, Action<string>? delete = null, Func<string, IEnumerable<string>>? enumerate = null, long byteBudget = MaximumJournalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteBudget, MaximumJournalBytes);
        _directory = directory;
        _write = write ?? ((path, contents) => AtomicFile.WriteAllText(path, contents));
        _delete = delete ?? File.Delete;
        _enumerate = enumerate ?? (path => Directory.EnumerateFiles(path, "*.json"));
        _byteBudget = byteBudget;
        Load();
    }

    internal Attempt? Get(Guid owner)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(owner);
        }
    }

    internal Attempt[] Pending()
    {
        lock (_gate)
        {
            return _entries.Values.ToArray();
        }
    }

    internal Attempt Begin(Guid owner, IReadOnlySet<Guid> before)
        => Save(new Attempt(Guid.NewGuid(), owner, before.Order().ToImmutableArray()), creating: true);

    internal Attempt RecordReturned(Guid owner, Guid attemptId, Guid returned)
        => Save(Require(owner, attemptId) with { ReturnedId = returned }, creating: false);

    internal Attempt RecordMarked(Guid owner, Guid attemptId)
        => Save(Require(owner, attemptId) with { Marked = true }, creating: false);

    internal bool Complete(Guid owner, Guid attemptId)
    {
        Attempt current;
        lock (_gate)
        {
            if (!_entries.TryGetValue(owner, out var value) || value.Id != attemptId)
            {
                return false;
            }

            if (!_writingOwners.Add(owner))
            {
                throw new IOException("The playlist creation attempt is already being persisted.");
            }

            current = value;
        }

        try
        {
            _delete(PathFor(owner));
            lock (_gate)
            {
                _entries.Remove(owner);
                _bytes -= current.ReservedBytes;
            }

            return true;
        }
        finally
        {
            lock (_gate)
            {
                _writingOwners.Remove(owner);
            }
        }
    }

    private Attempt Require(Guid owner, Guid attemptId)
    {
        var current = Get(owner);
        if (current is null || current.Id != attemptId)
        {
            throw new IOException("The playlist creation attempt no longer owns its lifecycle gate.");
        }

        return current;
    }

    private Attempt Save(Attempt attempt, bool creating)
    {
        Validate(attempt);
        var contents = JsonSerializer.Serialize(attempt, JsonDefaults.Options);
        var length = Encoding.UTF8.GetByteCount(contents);
        var reservation = MaximumStateBytes(attempt);
        // The returned Guid and false (longer than true) marker state are charged
        // before the host can register anything. Rewrites never release that lease.

        long previous;
        long growth;
        lock (_gate)
        {
            var current = _entries.GetValueOrDefault(attempt.Owner);
            if ((creating && current is not null) || (!creating && current?.Id != attempt.Id))
            {
                throw new IOException("The playlist owner already has a different creation attempt.");
            }

            previous = current?.ReservedBytes ?? 0;
            reservation = Math.Max(previous, reservation);
            growth = Math.Max(0, reservation - previous);
            if ((creating && _entries.Count + _reservedNew >= MaximumAttempts) || _bytes + growth > _byteBudget || !_writingOwners.Add(attempt.Owner))
            {
                throw new IOException("The bounded playlist creation journal is unavailable or full.");
            }

            if (creating)
            {
                _reservedNew++;
            }

            _bytes += growth;
        }

        var saved = false;
        try
        {
            _write(PathFor(attempt.Owner), contents);
            attempt = attempt with { FileBytes = length, ReservedBytes = reservation };
            lock (_gate)
            {
                _entries[attempt.Owner] = attempt;
            }

            saved = true;
            return attempt;
        }
        finally
        {
            lock (_gate)
            {
                _writingOwners.Remove(attempt.Owner);
                if (creating)
                {
                    _reservedNew--;
                }

                _bytes -= saved ? previous + growth - reservation : growth;
            }
        }
    }

    private string PathFor(Guid owner) => Path.Join(_directory, owner.ToString("N") + ".json");

    private static long MaximumStateBytes(Attempt attempt)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(attempt with { ReturnedId = attempt.Id, Marked = false }, JsonDefaults.Options));

    private void Load()
    {
        try
        {
            LoadCore();
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Playlist creation evidence is inaccessible.", exception);
        }
    }

    private void LoadCore()
    {
        string[] paths;
        var directoryObserved = false;
        try
        {
            var attributes = File.GetAttributes(_directory);
            directoryObserved = true;
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("Playlist recovery path is not a directory.");
            }

            paths = _enumerate(_directory).Take(MaximumAttempts + 1).ToArray();
        }
        catch (Exception exception) when (!directoryObserved && exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // An observed inventory failure cannot release a durable attempt
            // into an empty journal that may overwrite it after restoration.
            return;
        }

        foreach (var path in paths)
        {
            var length = new FileInfo(path).Length;
            if (_entries.Count >= MaximumAttempts || length > MaximumEnvelopeBytes)
            {
                throw new IOException("Existing playlist creation evidence exceeds its bounded recovery limit.");
            }

            using var stream = File.OpenRead(path);
            Attempt value;
            try
            {
                value = JsonSerializer.Deserialize<Attempt>(stream, JsonDefaults.Options) ?? throw new IOException("Playlist creation evidence is unavailable.");
            }
            catch (JsonException exception)
            {
                throw new IOException("Playlist creation evidence is malformed.", exception);
            }

            Validate(value);
            var reservation = Math.Max(length, MaximumStateBytes(value));
            if (_bytes + reservation > _byteBudget)
            {
                throw new IOException("Existing playlist creation attempts exceed their fully reserved lifecycle budget.");
            }

            if (Path.GetFileName(path) != value.Owner.ToString("N") + ".json" || !_entries.TryAdd(value.Owner, value with { FileBytes = length, ReservedBytes = reservation }))
            {
                throw new IOException("Playlist creation identities are inconsistent.");
            }

            _bytes += reservation;
        }
    }

    private static void Validate(Attempt value)
    {
        if (value.Id == Guid.Empty || value.Owner == Guid.Empty || value.BeforeIds.IsDefault || value.BeforeIds.Length > MaximumOwnerIds
            || value.BeforeIds.Contains(Guid.Empty) || value.BeforeIds.Distinct().Count() != value.BeforeIds.Length
            || value.ReturnedId == Guid.Empty || (value.ReturnedId.HasValue && value.BeforeIds.Contains(value.ReturnedId.Value)) || (value.Marked && !value.ReturnedId.HasValue))
        {
            throw new IOException("Playlist creation provenance is invalid.");
        }
    }

    internal sealed record Attempt(Guid Id, Guid Owner, ImmutableArray<Guid> BeforeIds, Guid? ReturnedId = null, bool Marked = false)
    {
        internal long FileBytes { get; init; }

        internal long ReservedBytes { get; init; }
    }
}
