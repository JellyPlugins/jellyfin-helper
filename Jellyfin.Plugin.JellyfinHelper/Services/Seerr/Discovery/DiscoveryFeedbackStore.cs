using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     File-based persistence for discovery feedback data. Stores per-user interaction history (shown, dismissed, requested, watched) in a JSON file for consumption by the training data builder.
/// </summary>
public sealed class DiscoveryFeedbackStore : IDiscoveryFeedbackStore
{
    private const string LogSource = "DiscoveryFeedback";
    private const string FileName = "jellyfin-helper-discovery-feedback.json";

    /// <summary>
    ///     Maximum allowed file size for the feedback store (30 MB).
    ///     Oversized valid stores are compacted without dropping all users' feedback.
    /// </summary>
    private const long MaxFileSizeBytes = 30 * 1024 * 1024;

    private const long MaxRecoveryFileSizeBytes = 4 * MaxFileSizeBytes;

    /// <summary>
    ///     Maximum number of feedback entries retained per user.
    ///     Prevents unbounded growth over time. Oldest entries (by ShownAtUtc) are evicted first.
    /// </summary>
    private const int MaxEntriesPerUser = 200;

    /// <summary>
    ///     Maximum age (in days) of feedback entries before they are evicted. Entries older than this are removed during save to prevent stale data accumulation.
    /// </summary>
    private const int MaxEntryAgeDays = 365;
    private const int MaximumPendingWrites = 64;
    private const int MaximumRejectedReceipts = 4096;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;
    private readonly Lock _fileLock = new();
    private readonly Lock _writerGate = new();
    private readonly Queue<PendingWrite> _writes = new();
    private readonly Queue<PendingWrite> _retryWrites = new();
    private readonly Dictionary<ReceiptKey, DateTime> _rejectedReceipts = new();
    private readonly Dictionary<TitleKey, int> _pendingRequests = new();
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryFeedbackStore> _logger;
    private readonly long _sizeLimit = MaxFileSizeBytes;
    private bool _recoveryBlocked;
    private Task<bool>? _pendingGeneration;
    private bool _writerRunning;
    private int _pendingObservedWrites;
    private bool _untrackedRejection;

    /// <summary>
    ///     In-memory cache of feedback data. Avoids repeated disk reads.
    ///     Invalidated on mutation operations.
    /// </summary>
    private List<DiscoveryFeedbackResult>? _memoryCache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryFeedbackStore"/> class.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public DiscoveryFeedbackStore(
        IPluginLogService pluginLog,
        ILogger<DiscoveryFeedbackStore> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;

        var dataPath = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("Plugin.Instance is null; DiscoveryFeedbackStore cannot determine its data folder path.");
        _filePath = Path.Join(dataPath, FileName);
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryFeedbackStore"/> class
    ///     with an explicit data directory path. Used for test isolation.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="dataFolderPath">The directory path where the feedback file will be stored.</param>
    /// <param name="sizeLimit">Maximum serialized UTF-8 size; the smaller override supports isolated boundary tests.</param>
    internal DiscoveryFeedbackStore(
        IPluginLogService pluginLog,
        ILogger<DiscoveryFeedbackStore> logger,
        string dataFolderPath,
        long sizeLimit = MaxFileSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeLimit);
        _pluginLog = pluginLog;
        _logger = logger;
        _filePath = Path.Join(dataFolderPath, FileName);
        _sizeLimit = sizeLimit;
    }

    private enum ReceiptKind
    {
        Request,
        Dismissal,
        Watched,
    }

    /// <summary>Gets the atomic filesystem writer; tests can stall precisely the actual I/O boundary.</summary>
    internal Action<string, string> WriteFeedback { get; init; } = (path, contents) => AtomicFile.WriteAllText(path, contents);

    /// <summary>Gets the wait clock; the production deadline stays two seconds while tests control timer ordering.</summary>
    internal TimeProvider WaitTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the receipt acquisition clock; retries retain the original acquisition rather than execution time.</summary>
    internal TimeProvider ReceiptTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the test synchronization hook immediately before atomic training-view selection.</summary>
    internal Action? BeforeTrainingSnapshotSelection { get; init; }

    /// <inheritdoc />
    public void RecordShown(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items)
        => RecordGenerated(userId, userName, items);

    /// <inheritdoc />
    public void RecordGenerated(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items, DateTime? generatedAtUtc = null)
        => RecordGeneratedCore(userId, userName, items, generatedAtUtc);

    /// <inheritdoc />
    public async Task<bool> RecordGeneratedAsync(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items, DateTime? generatedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0)
        {
            return true;
        }

        var snapshot = items.Select(item => item.Clone()).ToArray();
        var acquired = DateTimeNormalization.ToUtc(generatedAtUtc ?? DateTime.UtcNow);
        return await RunWriteAsync(() => RecordGeneratedCore(userId, userName, snapshot, acquired), [], observed: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RunWriteAsync(Func<bool> operation, Receipt[] receipts, bool observed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writerGate)
        {
            var activeWriter = _writerRunning && _pendingGeneration is { IsCompleted: false };
            if (!observed && activeWriter)
            {
                return false;
            }

            if (_writes.Count + _retryWrites.Count >= MaximumPendingWrites)
            {
                if (observed)
                {
                    TrackRejectedReceipts(receipts);
                }

                RestartRetainedWrites(activeWriter);
                return false;
            }

            while (_retryWrites.Count > 0)
            {
                _writes.Enqueue(_retryWrites.Dequeue());
            }

            var dependencies = receipts.Where(receipt => receipt.Key.Kind == ReceiptKind.Watched)
                .Select(receipt => receipt.Key.Title).Where(HasPendingRequest).Distinct().ToArray();
            _writes.Enqueue(new PendingWrite(operation, observed, completion, receipts, dependencies));
            if (observed)
            {
                Interlocked.Increment(ref _pendingObservedWrites);
                AddPendingRequests(receipts);
            }

            if (!activeWriter)
            {
                _writerRunning = true;
                _pendingGeneration = Task.Run(ProcessWrites, CancellationToken.None);
            }
        }

        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), WaitTimeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void RestartRetainedWrites(bool activeWriter)
    {
        if (activeWriter || _retryWrites.Count == 0)
        {
            return;
        }

        while (_retryWrites.Count > 0)
        {
            _writes.Enqueue(_retryWrites.Dequeue());
        }

        _writerRunning = true;
        _pendingGeneration = Task.Run(ProcessWrites, CancellationToken.None);
    }

    private void TrackRejectedReceipts(IEnumerable<Receipt> receipts)
    {
        foreach (var receipt in receipts)
        {
            if (!_rejectedReceipts.ContainsKey(receipt.Key) && _rejectedReceipts.Count >= MaximumRejectedReceipts)
            {
                // A bounded identity ledger cannot prove recovery after losing identities.
                // Keep training fail-closed rather than clearing on mere queue drainage.
                _untrackedRejection = true;
                continue;
            }

            _rejectedReceipts[receipt.Key] = _rejectedReceipts.TryGetValue(receipt.Key, out var previous) && previous > receipt.Acquired
                ? previous
                : receipt.Acquired;
        }
    }

    private void AddPendingRequests(IEnumerable<Receipt> receipts)
    {
        foreach (var title in receipts.Where(receipt => receipt.Key.Kind == ReceiptKind.Request).Select(receipt => receipt.Key.Title))
        {
            _pendingRequests[title] = _pendingRequests.GetValueOrDefault(title) + 1;
        }
    }

    private bool HasPendingRequest(TitleKey title)
        => _pendingRequests.ContainsKey(title) || _rejectedReceipts.ContainsKey(new ReceiptKey(title, ReceiptKind.Request));

    private bool HasUndurableDependency(PendingWrite write)
    {
        var snapshot = Volatile.Read(ref _memoryCache);
        return write.Dependencies.Any(title => HasPendingRequest(title)
            && !(snapshot?.FirstOrDefault(user => user.UserId == title.User)?.Entries
                .Any(entry => entry.TmdbId == title.TmdbId && entry.MediaType == title.MediaType && entry.RequestedAtUtc.HasValue) ?? false));
    }

    private bool ProcessWrites()
    {
        var successful = true;
        while (true)
        {
            PendingWrite write;
            lock (_writerGate)
            {
                if (_writes.Count == 0)
                {
                    _writerRunning = false;
                    return successful;
                }

                write = _writes.Dequeue();
            }

            var saved = false;
            Exception? failure = null;
            try
            {
                bool blocked;
                lock (_writerGate)
                {
                    blocked = HasUndurableDependency(write);
                }

                saved = !blocked && write.Operation();
            }
            catch (Exception exception) when (!exception.IsFatal())
            {
                failure = exception;
            }

            successful &= saved;
            CompleteObservedWrite(write, saved);
            var finished = ReleaseWriterIfIdle();
            // Publication includes completion bookkeeping. A successful receipt
            // must not race a stale busy flag on the caller's next generation.
            if (failure is null)
            {
                write.Completion.TrySetResult(saved);
            }
            else
            {
                write.Completion.TrySetException(failure);
            }

            if (finished)
            {
                return successful;
            }
        }
    }

    private bool ReleaseWriterIfIdle()
    {
        lock (_writerGate)
        {
            if (_writes.Count > 0)
            {
                return false;
            }

            _writerRunning = false;
            return true;
        }
    }

    private void CompleteObservedWrite(PendingWrite write, bool saved)
    {
        if (!write.Observed)
        {
            return;
        }

        lock (_writerGate)
        {
            if (saved)
            {
                RetireObservedWrite(write);
                PromoteDependentWatches(write);
            }
            else
            {
                // Failed requests and dependent watches retain their original receipts.
                // Retry only when explicitly submitted again or their request becomes durable.
                if (_writes.Count + _retryWrites.Count < MaximumPendingWrites)
                {
                    _retryWrites.Enqueue(write);
                }
                else
                {
                    TrackRejectedReceipts(write.Receipts);
                    RetireObservedWrite(write, recovered: false);
                }
            }
        }
    }

    private void RetireObservedWrite(PendingWrite write, bool recovered = true)
    {
        Interlocked.Decrement(ref _pendingObservedWrites);
        foreach (var receipt in write.Receipts)
        {
            if (receipt.Key.Kind == ReceiptKind.Request)
            {
                var count = _pendingRequests.GetValueOrDefault(receipt.Key.Title);
                if (count <= 1)
                {
                    _pendingRequests.Remove(receipt.Key.Title);
                }
                else
                {
                    _pendingRequests[receipt.Key.Title] = count - 1;
                }
            }

            if (recovered && _rejectedReceipts.TryGetValue(receipt.Key, out var rejectedAt) && CoversRejectedReceipt(receipt, rejectedAt))
            {
                _rejectedReceipts.Remove(receipt.Key);
            }
        }
    }

    private bool CoversRejectedReceipt(Receipt receipt, DateTime rejectedAt)
    {
        if (receipt.Acquired >= rejectedAt)
        {
            return true;
        }

        // Reconciled requests and watches are idempotent states. An older exact
        // receipt becoming durable also covers a rejected duplicate confirmation;
        // Seerr will otherwise skip it as already recorded and never resubmit it.
        // Dismissals still require the rejected action's timestamp to be covered.
        var title = receipt.Key.Title;
        var entry = Volatile.Read(ref _memoryCache)?.FirstOrDefault(user => user.UserId == title.User)?.Entries
            .FirstOrDefault(entry => entry.TmdbId == title.TmdbId && entry.MediaType == title.MediaType);
        return receipt.Key.Kind switch
        {
            ReceiptKind.Request => entry?.RequestedAtUtc.HasValue == true,
            ReceiptKind.Watched => entry?.WasWatched == true,
            ReceiptKind.Dismissal => entry?.DismissedAtUtc >= rejectedAt,
            _ => false,
        };
    }

    private void PromoteDependentWatches(PendingWrite completed)
    {
        if (!completed.Receipts.Any(receipt => receipt.Key.Kind == ReceiptKind.Request))
        {
            return;
        }

        var count = _retryWrites.Count;
        for (var index = 0; index < count; index++)
        {
            var waiting = _retryWrites.Dequeue();
            if (waiting.Dependencies.Length > 0 && !HasUndurableDependency(waiting))
            {
                _writes.Enqueue(waiting);
            }
            else
            {
                _retryWrites.Enqueue(waiting);
            }
        }
    }

    private bool RecordGeneratedCore(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items, DateTime? generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return true;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, userName);

            // Build a lookup of existing entries by composite key (TmdbId, MediaType) for O(1) dedup + backfill.
            // This prevents TMDb ID collisions between movies and TV shows (e.g., Movie #550 vs TV #550).
            var entryLookup = new Dictionary<(int TmdbId, string MediaType), DiscoveryFeedbackEntry>(
                userResult.Entries.Count);
            foreach (var entry in userResult.Entries)
            {
                entryLookup.TryAdd((entry.TmdbId, NormalizeMediaType(entry.MediaType)), entry);
            }

            var now = DateTimeNormalization.ToUtc(generatedAtUtc ?? DateTime.UtcNow);
            var modified = false;

            foreach (var item in items)
            {
                var normalizedType = NormalizeMediaType(item.MediaType);
                var key = (item.TmdbId, normalizedType);
                if (entryLookup.TryGetValue(key, out var existing))
                {
                    if (existing.GetStatus() == DiscoveryInteractionStatus.Generated)
                    {
                        existing.ScoringFeatures = null;
                        existing.TitleMetadata = null;
                        existing.ShownAtUtc = now;
                        modified = true;
                    }

                    modified |= BackfillExistingEntry(existing, item);
                    continue;
                }

                var newEntry = new DiscoveryFeedbackEntry
                {
                    TmdbId = item.TmdbId,
                    MediaType = normalizedType,
                    Title = item.Title,
                    Year = item.Year,
                    Genres = item.Genres?.ToArray() ?? [],
                    TmdbRating = item.TmdbRating,
                    Popularity = item.Popularity,
                    Score = item.Score,
                    // Exact card features already live in the recommendation cache and delivery evidence.
                    // An unseen generation is not training evidence and need not duplicate those features here.
                    ScoringFeatures = null,
                    ShownAtUtc = now,
                    KnownPeople = item.KnownPeople?.ToList()
                };
                userResult.Entries.Add(newEntry);
                entryLookup.TryAdd(key, newEntry);
                modified = true;
            }

            return !modified || SaveInternal(data);
        }
    }

    /// <inheritdoc />
    public void RecordImpressions(Guid userId, IReadOnlyList<DiscoveryRecommendation> items, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, string.Empty);
            var modified = false;
            foreach (var item in items)
            {
                var mediaType = NormalizeMediaType(item.MediaType);
                var entry = userResult.Entries.FirstOrDefault(e => e.TmdbId == item.TmdbId && e.MediaType == mediaType);
                if (entry is null)
                {
                    entry = new DiscoveryFeedbackEntry { TmdbId = item.TmdbId, MediaType = mediaType };
                    userResult.Entries.Add(entry);
                }

                if (entry.ImpressionAtUtc.HasValue)
                {
                    continue;
                }

                // A verified action can establish the prediction before any viewport acknowledgement.
                // Later impressions may record visibility but must not rewrite that action's evidence.
                ConfirmPrediction(entry, item, generatedAtUtc);
                entry.ImpressionAtUtc = DateTime.UtcNow;
                modified = true;
            }

            if (modified && !SaveInternal(data))
            {
                throw new IOException("Could not persist confirmed discovery impressions.");
            }
        }
    }

    /// <summary>
    ///     Backfills metadata on an existing placeholder entry (created by RecordDismissed/RecordRequested before RecordShown ran) or an entry missing enriched data (e.g., KnownPeople not available on first generation but enriched on a subsequent run).
    /// </summary>
    /// <param name="existing">The existing feedback entry to backfill in place.</param>
    /// <param name="item">The freshly shown recommendation providing candidate values.</param>
    /// <returns><see langword="true"/> if any field was modified; otherwise <see langword="false"/>.</returns>
    private static bool BackfillExistingEntry(DiscoveryFeedbackEntry existing, DiscoveryRecommendation item)
    {
        var modified = false;

        if (string.IsNullOrEmpty(existing.Title) && !string.IsNullOrEmpty(item.Title))
        {
            existing.Title = item.Title;
            modified = true;
        }

        if (existing.Year is null or 0 && item.Year is > 0)
        {
            existing.Year = item.Year;
            modified = true;
        }

        if ((existing.Genres == null || existing.Genres.Count == 0) && item.Genres is { Count: > 0 })
        {
            existing.Genres = item.Genres.ToArray();
            modified = true;
        }

        // Zero is an exact missing-metadata sentinel; a small positive rating is still populated.
        if (existing.TmdbRating.Equals(0.0) && item.TmdbRating > 0)
        {
            existing.TmdbRating = item.TmdbRating;
            modified = true;
        }

        modified |= BackfillNumericAndPeople(existing, item);

        return modified;
    }

    /// <summary>
    ///     Backfills missing score, popularity, and credits without replacing an observed empty cast.
    /// </summary>
    /// <param name="existing">The existing feedback entry to backfill in place.</param>
    /// <param name="item">The freshly shown recommendation providing candidate values.</param>
    /// <returns><see langword="true"/> if any field was modified; otherwise <see langword="false"/>.</returns>
    private static bool BackfillNumericAndPeople(DiscoveryFeedbackEntry existing, DiscoveryRecommendation item)
    {
        var modified = false;

        // Only the exact zero sentinel may be backfilled, not valid near-zero scores or popularity.
        if (existing.Score.Equals(0.0) && item.Score > 0)
        {
            existing.Score = item.Score;
            modified = true;
        }

        if (existing.Popularity.Equals(0.0) && item.Popularity > 0)
        {
            existing.Popularity = item.Popularity;
            modified = true;
        }

        if (existing.KnownPeople is null && item.KnownPeople is not null)
        {
            existing.KnownPeople = item.KnownPeople.ToList();
            modified = true;
        }

        return modified;
    }

    /// <inheritdoc />
    public void RecordDismissed(Guid userId, int tmdbId, string mediaType)
        => RecordAction(userId, tmdbId, mediaType, requested: false, item: null, generatedAtUtc: null);

    /// <inheritdoc />
    public void RecordDismissed(Guid userId, DiscoveryRecommendation item, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        RecordAction(userId, item.TmdbId, item.MediaType, requested: false, item, generatedAtUtc);
    }

    /// <inheritdoc />
    public void RecordRequested(Guid userId, int tmdbId, string mediaType)
        => RecordAction(userId, tmdbId, mediaType, requested: true, item: null, generatedAtUtc: null);

    /// <inheritdoc />
    public void RecordRequested(Guid userId, DiscoveryRecommendation item, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        RecordAction(userId, item.TmdbId, item.MediaType, requested: true, item, generatedAtUtc);
    }

    /// <inheritdoc />
    public Task<bool> RecordRequestedAsync(Guid userId, int tmdbId, string mediaType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        mediaType = NormalizeMediaType(mediaType);
        var receipt = new Receipt(new ReceiptKey(new TitleKey(userId, tmdbId, mediaType), ReceiptKind.Request), acquired);
        return RunWriteAsync(() => RecordActionCore(userId, tmdbId, mediaType, requested: true, item: null, generatedAtUtc: null, acquired), [receipt], observed: true, cancellationToken);
    }

    private void RecordAction(Guid userId, int tmdbId, string mediaType, bool requested, DiscoveryRecommendation? item, DateTime? generatedAtUtc)
    {
        var snapshot = item?.Clone();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        mediaType = NormalizeMediaType(mediaType);
        var receipt = new Receipt(new ReceiptKey(new TitleKey(userId, tmdbId, mediaType), requested ? ReceiptKind.Request : ReceiptKind.Dismissal), acquired);
        var write = new PendingWrite(
            () => RecordActionCore(userId, tmdbId, mediaType, requested, snapshot, generatedAtUtc, acquired),
            true,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            [receipt],
            []);
        lock (_writerGate)
        {
            Interlocked.Increment(ref _pendingObservedWrites);
            AddPendingRequests(write.Receipts);
        }

        var saved = false;
        try
        {
            saved = write.Operation();
        }
        finally
        {
            CompleteObservedWrite(write, saved);
        }
    }

    private bool RecordActionCore(Guid userId, int tmdbId, string mediaType, bool requested, DiscoveryRecommendation? item, DateTime? generatedAtUtc, DateTime acquired)
    {
        if (tmdbId <= 0)
        {
            return true;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, userName: null);
            var normalizedType = NormalizeMediaType(mediaType);
            var entry = GetOrCreateEntry(userResult, tmdbId, normalizedType);
            if (item is not null && generatedAtUtc.HasValue)
            {
                ConfirmPrediction(entry, item, generatedAtUtc.Value, acquired);
            }
            else if (!entry.HasConfirmedPrediction)
            {
                // Legacy/non-card actions have no delivery proof. Do not attribute them to an unseen generation.
                entry.ScoringFeatures = null;
                entry.TitleMetadata = null;
                if (!entry.RequestedAtUtc.HasValue && !entry.DismissedAtUtc.HasValue)
                {
                    entry.ShownAtUtc = acquired;
                }
            }

            if (requested)
            {
                entry.RequestedAtUtc = item is not null ? acquired : entry.RequestedAtUtc ?? acquired;
            }
            else
            {
                entry.DismissedAtUtc = entry.DismissedAtUtc > acquired ? entry.DismissedAtUtc : acquired;
            }

            return SaveInternal(data);
        }
    }

    private static void ConfirmPrediction(DiscoveryFeedbackEntry entry, DiscoveryRecommendation item, DateTime generatedAtUtc, DateTime? acquiredAtUtc = null)
    {
        if (entry.HasConfirmedPrediction)
        {
            return;
        }

        entry.Title = item.Title;
        entry.Year = item.Year;
        entry.Genres = item.Genres.ToArray();
        entry.KnownPeople = item.KnownPeople?.ToList();
        entry.TmdbRating = item.TmdbRating;
        entry.Popularity = item.Popularity;
        entry.Score = item.Score;
        entry.ScoringFeatures = item.ScoringFeatures?.Copy();
        entry.TitleMetadata = item.TitleMetadata;
        entry.ShownAtUtc = DateTimeNormalization.ToUtc(generatedAtUtc);
        entry.PredictionEvidenceAtUtc = acquiredAtUtc ?? DateTime.UtcNow;
    }

    /// <inheritdoc />
    public void MarkWatched(Guid userId, IReadOnlySet<(int TmdbId, string MediaType)> watchedItems)
        => MarkWatchedCore(userId, watchedItems, DateTime.UtcNow);

    /// <inheritdoc />
    public Task<bool> MarkWatchedAsync(Guid userId, IReadOnlySet<(int TmdbId, string MediaType)> watchedItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchedItems);
        cancellationToken.ThrowIfCancellationRequested();
        if (watchedItems.Count == 0)
        {
            return Task.FromResult(true);
        }

        var snapshot = watchedItems.Select(title => (title.TmdbId, MediaType: NormalizeMediaType(title.MediaType))).ToHashSet();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        var receipts = snapshot.Select(title => new Receipt(new ReceiptKey(new TitleKey(userId, title.TmdbId, title.MediaType), ReceiptKind.Watched), acquired)).ToArray();
        return RunWriteAsync(() => MarkWatchedCore(userId, snapshot, acquired), receipts, observed: true, cancellationToken);
    }

    private bool MarkWatchedCore(Guid userId, IReadOnlySet<(int TmdbId, string MediaType)> watchedItems, DateTime acquired)
    {
        ArgumentNullException.ThrowIfNull(watchedItems);

        if (watchedItems.Count == 0)
        {
            return true;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = data.FirstOrDefault(r => r.UserId == userId);
            if (userResult == null)
            {
                return true;
            }

            // Normalize incoming media types for case-insensitive matching.
            // Stored entries use NormalizeMediaType (lowercase), but callers may pass mixed case.
            var normalizedWatched = new HashSet<(int TmdbId, string MediaType)>(
                watchedItems.Select(w => (w.TmdbId, NormalizeMediaType(w.MediaType))));

            var modified = false;
            foreach (var entry in userResult.Entries.Where(
                entry => entry.RequestedAtUtc.HasValue
                         && !entry.WasWatched
                         && normalizedWatched.Contains((entry.TmdbId, entry.MediaType))))
            {
                entry.WasWatched = true;
                entry.WatchedAtUtc = acquired;
                modified = true;
            }

            return !modified || SaveInternal(data);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveryFeedbackResult> LoadAll()
    {
        List<DiscoveryFeedbackResult>? snapshot;
        do
        {
            // Cold loading happens outside the readiness gate. Warm snapshot selection
            // and readiness form one linearization point, independent of stalled I/O.
            BeforeTrainingSnapshotSelection?.Invoke();
            lock (_writerGate)
            {
                if (_pendingObservedWrites > 0 || _rejectedReceipts.Count > 0 || _untrackedRejection)
                {
                    throw new IOException("New confirmed feedback is awaiting persistence; obsolete outcomes are unavailable for fitting or admission.");
                }

                snapshot = Volatile.Read(ref _memoryCache);
            }

            if (snapshot is null)
            {
                ReadSnapshot();
            }
        }
        while (snapshot is null);

        return snapshot.Select(CloneResult).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public DiscoveryFeedbackResult? LoadForUser(Guid userId)
    {
        var result = ReadSnapshot().FirstOrDefault(r => r.UserId == userId);
        return result == null ? null : CloneResult(result);
    }

    /// <inheritdoc />
    public IReadOnlySet<(int TmdbId, string MediaType)> GetDismissedItems(Guid userId)
    {
        var user = ReadSnapshot().FirstOrDefault(result => result.UserId == userId);
        return (user?.Entries ?? []).Where(entry => entry.DismissedAtUtc.HasValue).Select(entry => (entry.TmdbId, entry.MediaType)).ToHashSet();
    }

    /// <inheritdoc />
    public IReadOnlySet<(int TmdbId, string MediaType)> GetRequestedItems(Guid userId)
    {
        var user = ReadSnapshot().FirstOrDefault(result => result.UserId == userId);
        return (user?.Entries ?? []).Where(entry => entry.RequestedAtUtc.HasValue).Select(entry => (entry.TmdbId, entry.MediaType)).ToHashSet();
    }

    private List<DiscoveryFeedbackResult> ReadSnapshot()
    {
        // Published snapshots are never mutated. A stalled generation write must
        // not block the next task's reads of already confirmed requests/dismissals.
        var cached = Volatile.Read(ref _memoryCache);
        if (cached is not null)
        {
            return cached;
        }

        lock (_fileLock)
        {
            return LoadInternal();
        }
    }

    /// <summary>
    ///     Creates a defensive copy of a feedback result to avoid exposing internal mutable state.
    /// </summary>
    private static DiscoveryFeedbackResult CloneResult(DiscoveryFeedbackResult source)
    {
        return new DiscoveryFeedbackResult
        {
            UserId = source.UserId,
            UserName = source.UserName,
            Entries = source.Entries.Select(e => new DiscoveryFeedbackEntry
            {
                TmdbId = e.TmdbId,
                MediaType = e.MediaType,
                Title = e.Title,
                Year = e.Year,
                Genres = e.Genres?.ToArray() ?? [],
                TmdbRating = e.TmdbRating,
                Popularity = e.Popularity,
                Score = e.Score,
                ScoringFeatures = e.ScoringFeatures?.Copy(),
                TitleMetadata = e.TitleMetadata,
                ImpressionAtUtc = e.ImpressionAtUtc,
                PredictionEvidenceAtUtc = e.PredictionEvidenceAtUtc,
                PredictionInputsCompacted = e.PredictionInputsCompacted,
                KnownPeople = e.KnownPeople?.ToList(),
                ShownAtUtc = e.ShownAtUtc,
                DismissedAtUtc = e.DismissedAtUtc,
                RequestedAtUtc = e.RequestedAtUtc,
                WasWatched = e.WasWatched,
                WatchedAtUtc = e.WatchedAtUtc
            }).ToList()
        };
    }

    /// <summary>
    ///     Loads the feedback data from the in-memory cache or disk and returns a deep copy.
    /// </summary>
    private List<DiscoveryFeedbackResult> LoadInternal()
    {
        if (_memoryCache != null)
        {
            return _memoryCache.Select(CloneResult).ToList();
        }

        try
        {
            if (!File.Exists(_filePath))
            {
                Volatile.Write(ref _memoryCache, []);
                return [];
            }

            var fileInfo = new FileInfo(_filePath);
            if (fileInfo.Length > MaxRecoveryFileSizeBytes)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Feedback file exceeds the bounded recovery limit ({fileInfo.Length} bytes); preserving it and blocking writes.",
                    null,
                    _logger);
                _recoveryBlocked = true;
                Volatile.Write(ref _memoryCache, []);
                return [];
            }

            using (var stream = File.OpenRead(_filePath))
            {
                Volatile.Write(ref _memoryCache, JsonSerializer.Deserialize<List<DiscoveryFeedbackResult>>(stream, JsonOptions) ?? []);
            }

            if (fileInfo.Length > _sizeLimit)
            {
                _pluginLog.LogWarning(LogSource, $"Feedback file exceeds the size limit ({fileInfo.Length} bytes); recovering and compacting valid feedback.", null, _logger);
                var recovered = _memoryCache;
                SaveInternal(recovered);
                Volatile.Write(ref _memoryCache, _memoryCache ?? recovered);
            }

            return _memoryCache.Select(CloneResult).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Could not load discovery feedback from {_filePath}: {ex.Message}",
                ex,
                _logger);
            if (ex is JsonException)
            {
                TryDeleteFile();
            }

            Volatile.Write(ref _memoryCache, []);
            return [];
        }
    }

    /// <summary>
    ///     Saves the feedback data to disk using atomic write (temp file + move). Applies eviction rules before saving.
    /// </summary>
    private bool SaveInternal(List<DiscoveryFeedbackResult> data)
    {
        if (_recoveryBlocked)
        {
            return false;
        }

        // Work on a deep copy so eviction mutations never touch the live _memoryCache.
        // The copy is swapped into _memoryCache atomically after a successful disk write.
        var workingCopy = data.Select(CloneResult).ToList();

        // Eviction: remove entries older than MaxEntryAgeDays and cap per-user count. Use the latest interaction timestamp (not just ShownAtUtc) to prevent evicting entries that were recently dismissed or requested but originally shown long ago.
        var cutoff = DateTime.UtcNow.AddDays(-MaxEntryAgeDays);
        foreach (var userResult in workingCopy)
        {
            foreach (var entry in userResult.Entries.Where(entry => !entry.HasConfirmedPrediction))
            {
                entry.ScoringFeatures = null;
                entry.TitleMetadata = null;
            }

            // Remove expired entries based on their most recent activity
            userResult.Entries.RemoveAll(e => GetLatestActivityUtc(e) < cutoff);

            // Cap per-user count (keep most recently active entries)
            if (userResult.Entries.Count > MaxEntriesPerUser)
            {
                userResult.Entries = userResult.Entries
                    .OrderByDescending(GetLatestActivityUtc)
                    .Take(MaxEntriesPerUser)
                    .ToList();
            }
        }

        // Remove users with zero entries after eviction
        workingCopy.RemoveAll(r => r.Entries.Count == 0);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = SerializeWithinSizeLimit(workingCopy);
            if (bytes is null)
            {
                _pluginLog.LogWarning(LogSource, "Explicit feedback cannot fit the size limit; preserving the existing file instead of overwriting it.", null, _logger);
                return false;
            }

            // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
            WriteFeedback(_filePath, Encoding.UTF8.GetString(bytes));

            // Atomically replace the live cache with the post-eviction copy.
            Volatile.Write(ref _memoryCache, workingCopy);
            return true;
        }

        // Broader filter than plain IOException / UnauthorizedAccessException / JsonException because AtomicFile.WriteAllText can also surface SecurityException, NotSupportedException and ArgumentException (malformed path characters from OS layer).
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or JsonException
                                    or System.Security.SecurityException
                                    or NotSupportedException
                                    or ArgumentException)
        {
            // Invalidate the in-memory cache so the next LoadInternal() re-reads from disk.
            Volatile.Write(ref _memoryCache, null);
            _pluginLog.LogWarning(
                LogSource,
                $"Could not save discovery feedback to {_filePath}: {ex.Message}",
                ex,
                _logger);
        }

        return false;
    }

    /// <summary>Evicts generated-only rows first, then compacts older metadata while preserving explicit outcomes and identities.</summary>
    private byte[]? SerializeWithinSizeLimit(List<DiscoveryFeedbackResult> data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        if (bytes.LongLength <= _sizeLimit)
        {
            return bytes;
        }

        foreach (var result in data)
        {
            result.Entries.RemoveAll(entry => entry.GetStatus() == DiscoveryInteractionStatus.Generated);
        }

        data.RemoveAll(result => result.Entries.Count == 0);
        bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        var estimatedSize = bytes.LongLength;
        foreach (var entry in data.SelectMany(result => result.Entries)
                     .OrderBy(entry => entry.RequestedAtUtc.HasValue || entry.DismissedAtUtc.HasValue)
                     .ThenBy(GetLatestActivityUtc))
        {
            if (estimatedSize <= _sizeLimit)
            {
                break;
            }

            var previousSize = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions).LongLength;
            entry.PredictionInputsCompacted = true;
            entry.ScoringFeatures = null;
            entry.TitleMetadata = null;
            entry.Title = string.Empty;
            entry.Year = null;
            entry.Genres = [];
            entry.KnownPeople = null;
            estimatedSize -= previousSize - JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions).LongLength;
        }

        bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        return bytes.LongLength <= _sizeLimit ? bytes : null;
    }

    /// <summary>
    ///     Gets the existing user result or creates a new one.
    ///     Must be called under <see cref="_fileLock"/>.
    /// </summary>
    private static DiscoveryFeedbackResult GetOrCreateUserResult(
        List<DiscoveryFeedbackResult> data,
        Guid userId,
        string? userName)
    {
        var existing = data.FirstOrDefault(r => r.UserId == userId);
        if (existing != null)
        {
            // Update username in case it changed (only when caller supplies one)
            if (userName != null)
            {
                existing.UserName = userName;
            }

            return existing;
        }

        var newResult = new DiscoveryFeedbackResult
        {
            UserId = userId,
            UserName = userName ?? string.Empty
        };
        data.Add(newResult);
        return newResult;
    }

    /// <summary>
    ///     Returns the existing feedback entry for (tmdbId, mediaType), or creates and registers a new one.
    ///     Must be called under <see cref="_fileLock"/>.
    /// </summary>
    private static DiscoveryFeedbackEntry GetOrCreateEntry(
        DiscoveryFeedbackResult userResult,
        int tmdbId,
        string normalizedMediaType)
    {
        foreach (var e in userResult.Entries)
        {
            if (e.TmdbId == tmdbId && e.MediaType == normalizedMediaType)
            {
                return e;
            }
        }

        var entry = new DiscoveryFeedbackEntry
        {
            TmdbId = tmdbId,
            MediaType = normalizedMediaType,
            ShownAtUtc = DateTime.UtcNow
        };
        userResult.Entries.Add(entry);
        return entry;
    }

    /// <summary>
    ///     Returns the most recent activity timestamp for a feedback entry. Used by eviction logic to retain entries with recent interactions even if their original ShownAtUtc is old.
    /// </summary>
    private static DateTime GetLatestActivityUtc(DiscoveryFeedbackEntry entry)
    {
        var latest = entry.ShownAtUtc;
        if (entry.ImpressionAtUtc.HasValue && entry.ImpressionAtUtc.Value > latest)
        {
            latest = entry.ImpressionAtUtc.Value;
        }

        if (entry.DismissedAtUtc.HasValue && entry.DismissedAtUtc.Value > latest)
        {
            latest = entry.DismissedAtUtc.Value;
        }

        if (entry.RequestedAtUtc.HasValue && entry.RequestedAtUtc.Value > latest)
        {
            latest = entry.RequestedAtUtc.Value;
        }

        if (entry.WatchedAtUtc.HasValue && entry.WatchedAtUtc.Value > latest)
        {
            latest = entry.WatchedAtUtc.Value;
        }

        return latest;
    }

    /// <summary>
    ///     Normalizes a media type string to lowercase for consistent lookup/dedup.
    ///     Defensive: handles null/whitespace gracefully.
    /// </summary>
    private string NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return "movie";
        }

        var normalized = mediaType.Trim().ToLowerInvariant();

        if (normalized != "movie" && normalized != "tv")
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Unexpected mediaType value '{Value}' normalized to 'movie'.", mediaType.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' '));
            }

            return "movie";
        }

        return normalized;
    }

    private void TryDeleteFile()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort
        }
    }

    private readonly record struct TitleKey(Guid User, int TmdbId, string MediaType);

    private readonly record struct ReceiptKey(TitleKey Title, ReceiptKind Kind);

    private readonly record struct Receipt(ReceiptKey Key, DateTime Acquired);

    private sealed record PendingWrite(Func<bool> Operation, bool Observed, TaskCompletionSource<bool> Completion, Receipt[] Receipts, TitleKey[] Dependencies);
}
