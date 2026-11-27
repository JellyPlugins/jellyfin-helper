using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>Bounded per-owner lifecycle leases; recovery, creation and cleanup cannot overtake one another.</summary>
internal sealed class PlaylistOwnerOperations
{
    private const int MaximumOwners = 4096;
    private const int MaximumOwnerOperations = 64;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    internal async Task<IDisposable> EnterAsync(Guid owner, CancellationToken cancellationToken)
    {
        Lease? lease = new(this);
        try
        {
            var entry = Admit(owner);
            lease.Entry = entry;
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            lease.Acquired = true;
            var granted = lease;
            lease = null;
            return granted;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    internal IDisposable? TryEnter(Guid owner)
    {
        Lease? lease = new(this);
        try
        {
            var entry = Admit(owner);
            lease.Entry = entry;
            if (!entry.Semaphore.Wait(0))
            {
                return null;
            }

            lease.Acquired = true;
            var granted = lease;
            lease = null;
            return granted;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private Entry Admit(Guid owner)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(owner, out var entry))
            {
                if (_entries.Count >= MaximumOwners)
                {
                    throw new IOException("The bounded playlist owner-operation registry is full.");
                }

                entry = new Entry(owner);
                _entries.Add(owner, entry);
            }

            if (entry.References >= MaximumOwnerOperations)
            {
                throw new IOException("The bounded playlist owner-operation queue is full.");
            }

            entry.References++;
            return entry;
        }
    }

    private void ReleaseReference(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(entry.Owner);
                entry.Dispose();
            }
        }
    }

    private sealed class Entry(Guid owner) : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        internal Guid Owner { get; } = owner;

        internal SemaphoreSlim Semaphore => _semaphore;

        internal int References { get; set; }

        public void Dispose()
        {
            _semaphore.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class Lease(PlaylistOwnerOperations registry) : IDisposable
    {
        private PlaylistOwnerOperations? _registry = registry;

        internal Entry? Entry { get; set; }

        internal bool Acquired { get; set; }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _registry, null);
            if (current is not null && Entry is { } entry)
            {
                if (Acquired)
                {
                    entry.Semaphore.Release();
                }

                current.ReleaseReference(entry);
            }

            GC.SuppressFinalize(this);
        }
    }
}
