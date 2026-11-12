using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Serializes same-title requests without blocking unrelated titles or retaining idle locks.</summary>
internal sealed class SeerrRequestCoordinator
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal async Task<SeerrRequestResult> ExecuteAsync(string key, Func<bool, Task<SeerrRequestResult>> submit, CancellationToken cancellationToken)
    {
        Entry entry;
        int observedSubmissions;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            entry.Users++;
            observedSubmissions = entry.Submissions;
        }

        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            bool submittedWhileWaiting;
            lock (_gate)
            {
                submittedWhileWaiting = entry.Submissions != observedSubmissions;
            }

            var result = await submit(submittedWhileWaiting).ConfigureAwait(false);
            if (result.Success && !result.AlreadyRequested)
            {
                lock (_gate)
                {
                    entry.Submissions++;
                }
            }

            return result;
        }
        finally
        {
            if (acquired)
            {
                entry.Semaphore.Release();
            }

            lock (_gate)
            {
                if (--entry.Users == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int Users { get; set; }

        internal int Submissions { get; set; }
    }
}
