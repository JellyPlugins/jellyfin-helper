using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
public sealed class DiscoveryFeedbackStore : IDiscoveryFeedbackStore, IDisposable
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
    private const long MaximumReplayPayloadBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;
    private readonly Lock _fileLock = new();
    private readonly Lock _writerGate = new();
    private readonly Queue<PendingWrite> _writes = new();
    private readonly Queue<PendingWrite> _retryWrites = new();
    private readonly Dictionary<ReceiptKey, DateTime> _rejectedReceipts = new();
    private readonly Dictionary<ReceiptKey, PredictionReplay> _predictionReplays = new();
    private readonly Dictionary<ReceiptKey, string> _enqueuedPredictionReplays = new();
    private readonly Dictionary<TitleKey, int> _pendingRequests = new();
    private readonly Dictionary<TitleKey, int> _pendingDismissals = new();
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryFeedbackStore> _logger;
    private readonly long _sizeLimit = MaxFileSizeBytes;
    private long _replayPayloadBytes;
    private bool _recoveryBlocked;
    private Task<bool>? _pendingGeneration;
    private bool _writerRunning;
    private int _pendingObservedWrites;
    private bool _untrackedRejection;
    private ITimer? _retryTimer;
    private long _retryTimerGeneration;
    private int _retryBackoff;
    private bool _disposed;

    /// <summary>
    ///     Last complete recovered or committed snapshot. Failed reads remain unpublished;
    ///     failed writes cannot discard known durable exclusions.
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
        Impression,
    }

    /// <summary>Gets the atomic filesystem writer; tests can stall precisely the actual I/O boundary.</summary>
    internal Action<string, string> WriteFeedback { get; init; } = (path, contents) => AtomicFile.WriteAllText(path, contents);

    /// <summary>Gets the bounded recovery stream opener; unavailable initial reads cannot become an empty durable store.</summary>
    internal Func<string, Stream> OpenFeedbackRead { get; init; } = path => File.OpenRead(path);

    /// <summary>Gets the wait clock; the production deadline stays two seconds while tests control timer ordering.</summary>
    internal TimeProvider WaitTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the receipt acquisition clock; retries retain the original acquisition rather than execution time.</summary>
    internal TimeProvider ReceiptTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the background retry clock; one timer uses capped exponential backoff independently of caller waits.</summary>
    internal TimeProvider RetryTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the bounded serialized overflow-payload budget; identity/hash proofs remain replay requirements if bytes cannot fit.</summary>
    internal long ReplayPayloadBudget { get; init; } = MaximumReplayPayloadBytes;

    /// <summary>Gets the test synchronization hook immediately before atomic training-view selection.</summary>
    internal Action? BeforeTrainingSnapshotSelection { get; init; }

    /// <summary>Gets the test handoff hook before coherent serving snapshot selection.</summary>
    internal Action? BeforeDismissalSnapshotSelection { get; init; }

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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
                AddPendingActions(receipts);
            }

            StartWriterIfNeeded();
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
        if (activeWriter)
        {
            return;
        }

        while (_retryWrites.Count > 0)
        {
            _writes.Enqueue(_retryWrites.Dequeue());
        }

        StartWriterIfNeeded();
    }

    private void StartWriterIfNeeded()
    {
        if (_writes.Count > 0 && !(_writerRunning && _pendingGeneration is { IsCompleted: false }))
        {
            _writerRunning = true;
            _pendingGeneration = Task.Run(ProcessWrites, CancellationToken.None);
        }
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

            _rejectedReceipts[receipt.Key] = RetainedReceiptTime(receipt);
            RetainRejectedPrediction(receipt);
        }
    }

    private void RetainRejectedPrediction(Receipt receipt)
    {
        if (receipt.Prediction is not { } bytes)
        {
            return;
        }

        if (_predictionReplays.TryGetValue(receipt.Key, out var prior))
        {
            var keepPrior = receipt.Key.Kind == ReceiptKind.Request ? prior.Acquired >= receipt.Acquired : prior.Acquired <= receipt.Acquired;
            if (keepPrior)
            {
                return;
            }

            _replayPayloadBytes -= prior.Payload?.LongLength ?? 0;
        }

        var payload = _replayPayloadBytes + bytes.LongLength <= ReplayPayloadBudget ? bytes : null;
        _predictionReplays[receipt.Key] = new(receipt.PredictionHash!, receipt.Acquired, payload);
        _replayPayloadBytes += payload?.LongLength ?? 0;
        // If the bounded byte budget is exhausted, the proof remains an explicit
        // replay requirement. Only that original validated delivery can resolve it;
        // a later impression never substitutes a discarded prediction payload.
    }

    private Receipt CapturePredictionReceipt(ReceiptKey key, DateTime acquired, DiscoveryRecommendation item, DateTime generatedAt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PredictionPayload(item, generatedAt), JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        lock (_writerGate)
        {
            if (_predictionReplays.TryGetValue(key, out var replay) && replay.Hash == hash)
            {
                acquired = replay.Acquired;
            }
        }

        return new(key, acquired, bytes, hash);
    }

    private void PromoteRetainedPredictions()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var pair in _predictionReplays.Where(pair => pair.Value.Payload is not null && !_enqueuedPredictionReplays.ContainsKey(pair.Key)).ToArray())
        {
            if (_writes.Count + _retryWrites.Count >= MaximumPendingWrites)
            {
                break;
            }

            var receipt = new Receipt(pair.Key, pair.Value.Acquired, pair.Value.Payload, pair.Value.Hash);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _writes.Enqueue(new PendingWrite(() => ReplayPrediction(receipt), true, completion, [receipt], []));
            _enqueuedPredictionReplays.Add(pair.Key, pair.Value.Hash);
            Interlocked.Increment(ref _pendingObservedWrites);
            AddPendingActions([receipt]);
        }

        StartWriterIfNeeded();
    }

    private bool ReplayPrediction(Receipt receipt)
    {
        // Deserialization and file I/O run on the managed worker, never while the
        // serving/readiness gate is held.
        var payload = JsonSerializer.Deserialize<PredictionPayload>(receipt.Prediction!, JsonOptions)!;
        return receipt.Key.Kind == ReceiptKind.Impression
            ? RecordImpressionsCore(receipt.Key.Title.User, [payload.Item], payload.GeneratedAt, receipt.Acquired)
            : RecordActionCore(receipt.Key.Title.User, receipt.Key.Title.TmdbId, receipt.Key.Title.MediaType, receipt.Key.Kind == ReceiptKind.Request, payload.Item, payload.GeneratedAt, receipt.Acquired);
    }

    private DateTime RetainedReceiptTime(Receipt receipt)
    {
        if (!_rejectedReceipts.TryGetValue(receipt.Key, out var previous))
        {
            return receipt.Acquired;
        }

        // Request intent is idempotent; repeated observed actions retain their later age.
        if (receipt.Key.Kind is ReceiptKind.Request or ReceiptKind.Impression)
        {
            return previous < receipt.Acquired ? previous : receipt.Acquired;
        }

        return previous > receipt.Acquired ? previous : receipt.Acquired;
    }

    private void AddPendingActions(IEnumerable<Receipt> receipts)
    {
        foreach (var key in receipts.Where(receipt => receipt.Key.Kind is ReceiptKind.Request or ReceiptKind.Dismissal).Select(receipt => receipt.Key))
        {
            var pending = key.Kind == ReceiptKind.Request ? _pendingRequests : _pendingDismissals;
            pending[key.Title] = pending.GetValueOrDefault(key.Title) + 1;
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
                    ScheduleRetainedRetry();
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
            ScheduleRetainedRetry();
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
                PromoteRetainedPredictions();
                PromoteDependentWatches(write);
            }
            else
            {
                // Failed requests and dependent watches retain their original receipts.
                // One backoff timer restarts runnable retained work without requiring a new action.
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

            if (!_writerRunning && _writes.Count == 0)
            {
                ScheduleRetainedRetry();
            }
        }
    }

    private void ScheduleRetainedRetry()
    {
        if (_disposed || _retryTimer is not null || !HasRunnableRetainedWrites())
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(4, _retryBackoff++))));
        var generation = ++_retryTimerGeneration;
        _retryTimer = RetryTimeProvider.CreateTimer(_ => RetryRetainedWrites(generation), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _retryTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private bool HasRunnableRetainedWrites()
        => _retryWrites.Any(write => !HasUndurableDependency(write))
            || _predictionReplays.Any(pair => pair.Value.Payload is not null && !_enqueuedPredictionReplays.ContainsKey(pair.Key));

    private void RetryRetainedWrites(long generation)
    {
        lock (_writerGate)
        {
            if (_retryTimer is null || generation != _retryTimerGeneration)
            {
                return;
            }

            _retryTimer.Dispose();
            _retryTimer = null;
            if (_disposed)
            {
                return;
            }

            var count = _retryWrites.Count;
            for (var index = 0; index < count; index++)
            {
                var write = _retryWrites.Dequeue();
                if (HasUndurableDependency(write))
                {
                    _retryWrites.Enqueue(write);
                }
                else
                {
                    _writes.Enqueue(write);
                }
            }

            PromoteRetainedPredictions();
            StartWriterIfNeeded();
        }
    }

    private void RetireObservedWrite(PendingWrite write, bool recovered = true)
    {
        Interlocked.Decrement(ref _pendingObservedWrites);
        if (_pendingObservedWrites == 0 && _retryWrites.Count == 0)
        {
            _retryBackoff = 0;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }

        foreach (var receipt in write.Receipts)
        {
            RetirePendingAction(receipt.Key);

            if (recovered && _rejectedReceipts.TryGetValue(receipt.Key, out var rejectedAt) && CoversRejectedReceipt(receipt, rejectedAt))
            {
                _rejectedReceipts.Remove(receipt.Key);
                if (_predictionReplays.Remove(receipt.Key, out var replay))
                {
                    _replayPayloadBytes -= replay.Payload?.LongLength ?? 0;
                }
            }

            if (_enqueuedPredictionReplays.TryGetValue(receipt.Key, out var enqueuedHash) && enqueuedHash == receipt.PredictionHash)
            {
                _enqueuedPredictionReplays.Remove(receipt.Key);
            }
        }
    }

    private void RetirePendingAction(ReceiptKey key)
    {
        if (key.Kind is not (ReceiptKind.Request or ReceiptKind.Dismissal))
        {
            return;
        }

        var pending = key.Kind == ReceiptKind.Request ? _pendingRequests : _pendingDismissals;
        var count = pending.GetValueOrDefault(key.Title);
        if (count <= 1)
        {
            pending.Remove(key.Title);
        }
        else
        {
            pending[key.Title] = count - 1;
        }
    }

    private bool CoversRejectedReceipt(Receipt receipt, DateTime rejectedAt)
    {
        if (_predictionReplays.TryGetValue(receipt.Key, out var prediction))
        {
            return receipt.PredictionHash == prediction.Hash;
        }

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
            // The store preserves first visibility. Its durable earlier receipt also
            // covers a later duplicate, but an absent impression never proves recovery.
            ReceiptKind.Impression => entry?.ImpressionAtUtc.HasValue == true,
            _ => false,
        };
    }

    private void PromoteDependentWatches(PendingWrite completed)
    {
        if (_disposed || !completed.Receipts.Any(receipt => receipt.Key.Kind == ReceiptKind.Request))
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

        // A synchronous request can make retained watches runnable after the async
        // worker went idle. Promotion itself must hand them to a managed writer.
        StartWriterIfNeeded();
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

        var snapshot = items.Select(item => item.Clone()).ToArray();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        var receipts = snapshot.Select(item => CapturePredictionReceipt(new ReceiptKey(new TitleKey(userId, item.TmdbId, NormalizeMediaType(item.MediaType)), ReceiptKind.Impression), acquired, item, generatedAtUtc)).ToArray();
        var write = new PendingWrite(() => RecordImpressionsCore(userId, snapshot, generatedAtUtc, acquired, receipts), true, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), receipts, []);
        if (!ExecuteSynchronousWrite(write))
        {
            throw new IOException("Could not persist confirmed discovery impressions.");
        }
    }

    private bool RecordImpressionsCore(Guid userId, DiscoveryRecommendation[] items, DateTime generatedAtUtc, DateTime acquired, IReadOnlyList<Receipt>? receipts = null)
    {
        lock (_fileLock)
        {
            var data = LoadInternal();
            var userResult = GetOrCreateUserResult(data, userId, string.Empty);
            var modified = false;
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                var itemAcquired = receipts?[index].Acquired ?? acquired;
                var mediaType = NormalizeMediaType(item.MediaType);
                var entry = userResult.Entries.FirstOrDefault(e => e.TmdbId == item.TmdbId && e.MediaType == mediaType);
                if (entry is null)
                {
                    entry = new DiscoveryFeedbackEntry { TmdbId = item.TmdbId, MediaType = mediaType };
                    userResult.Entries.Add(entry);
                }

                if (entry.ImpressionAtUtc.HasValue && entry.ImpressionAtUtc.Value <= itemAcquired)
                {
                    continue;
                }

                // A verified action can establish the prediction before any viewport acknowledgement.
                // Later impressions may record visibility but must not rewrite that action's evidence.
                ConfirmPrediction(entry, item, generatedAtUtc, itemAcquired);
                entry.ImpressionAtUtc = itemAcquired;
                modified = true;
            }

            return !modified || SaveInternal(data);
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
        var key = new ReceiptKey(new TitleKey(userId, tmdbId, mediaType), ReceiptKind.Request);
        lock (_writerGate)
        {
            if (_rejectedReceipts.TryGetValue(key, out var rejectedAt) && rejectedAt < acquired)
            {
                acquired = rejectedAt;
            }
        }

        var receipt = new Receipt(key, acquired);
        return RunWriteAsync(() => RecordActionCore(userId, tmdbId, mediaType, requested: true, item: null, generatedAtUtc: null, acquired), [receipt], observed: true, cancellationToken);
    }

    private void RecordAction(Guid userId, int tmdbId, string mediaType, bool requested, DiscoveryRecommendation? item, DateTime? generatedAtUtc)
    {
        var snapshot = item?.Clone();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        mediaType = NormalizeMediaType(mediaType);
        var key = new ReceiptKey(new TitleKey(userId, tmdbId, mediaType), requested ? ReceiptKind.Request : ReceiptKind.Dismissal);
        var receipt = snapshot is not null && generatedAtUtc.HasValue ? CapturePredictionReceipt(key, acquired, snapshot, generatedAtUtc.Value) : new Receipt(key, acquired);
        var write = new PendingWrite(
            () => RecordActionCore(userId, tmdbId, mediaType, requested, snapshot, generatedAtUtc, receipt.Acquired),
            true,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            [receipt],
            []);
        if (!ExecuteSynchronousWrite(write) && !requested)
        {
            // A permanent dismissal must not be acknowledged as durable while its
            // receipt exists only in memory. Retained retry and serving exclusions
            // still own the confirmed action, but callers must report persistence failure.
            throw new IOException("Could not persist the discovery dismissal; its receipt remains pending.");
        }
    }

    private bool ExecuteSynchronousWrite(PendingWrite write)
    {
        lock (_writerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _pendingObservedWrites);
            AddPendingActions(write.Receipts);
        }

        var saved = false;
        try
        {
            saved = write.Operation();
            return saved;
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
            UpdateActionPrediction(entry, item, generatedAtUtc, acquired, requested);
            RecordActionTimestamp(entry, requested, item is not null, acquired);

            return SaveInternal(data);
        }
    }

    private static void UpdateActionPrediction(DiscoveryFeedbackEntry entry, DiscoveryRecommendation? item, DateTime? generatedAtUtc, DateTime acquired, bool requested)
    {
        if (item is not null && generatedAtUtc.HasValue)
        {
            ConfirmPrediction(entry, item, generatedAtUtc.Value, acquired, action: true, requested: requested);
        }
        else if (!entry.HasConfirmedPrediction)
        {
            entry.ScoringFeatures = null;
            entry.TitleMetadata = null;
            if (!entry.RequestedAtUtc.HasValue && !entry.DismissedAtUtc.HasValue)
            {
                entry.ShownAtUtc = acquired;
            }
        }
    }

    private static void RecordActionTimestamp(DiscoveryFeedbackEntry entry, bool requested, bool card, DateTime acquired)
    {
        if (!requested)
        {
            entry.DismissedAtUtc = entry.DismissedAtUtc > acquired ? entry.DismissedAtUtc : acquired;
        }
        else if (card)
        {
            entry.RequestedAtUtc = entry.RequestedAtUtc > acquired ? entry.RequestedAtUtc : acquired;
        }
        else if (!entry.CardRequestedAtUtc.HasValue)
        {
            entry.RequestedAtUtc = entry.RequestedAtUtc.HasValue && entry.RequestedAtUtc.Value < acquired ? entry.RequestedAtUtc : acquired;
        }
    }

    private static void ConfirmPrediction(DiscoveryFeedbackEntry entry, DiscoveryRecommendation item, DateTime generatedAtUtc, DateTime? acquiredAtUtc = null, bool action = false, bool requested = false)
    {
        var acquired = acquiredAtUtc ?? DateTime.UtcNow;
        var confirmedAt = entry.PredictionEvidenceAtUtc ?? entry.ImpressionAtUtc;
        if (entry.CardRequestedAtUtc > acquired
            || (!action && (entry.PredictionActionAtUtc.HasValue || (confirmedAt.HasValue && confirmedAt.Value <= acquired)))
            || (action && !requested && entry.PredictionActionAtUtc.HasValue && entry.PredictionActionAtUtc.Value <= acquired))
        {
            return;
        }

        // An older exact receipt can reach disk after a later impression. Restore
        // the first actually confirmed inputs, never whichever write finished first.
        // A genuinely newer observed action remains authoritative over an older retry.
        entry.Title = item.Title;
        if (action)
        {
            entry.PredictionActionAtUtc = acquired;
            if (requested)
            {
                entry.CardRequestedAtUtc = acquired;
            }
        }

        entry.Year = item.Year;
        entry.Genres = item.Genres.ToArray();
        entry.KnownPeople = item.KnownPeople?.ToList();
        entry.TmdbRating = item.TmdbRating;
        entry.Popularity = item.Popularity;
        entry.Score = item.Score;
        entry.ScoringFeatures = item.ScoringFeatures?.Copy();
        entry.TitleMetadata = item.TitleMetadata;
        entry.ShownAtUtc = DateTimeNormalization.ToUtc(generatedAtUtc);
        entry.PredictionEvidenceAtUtc = acquired;
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
        while (true)
        {
            // Cold disk I/O must not run under the receipt gate. The subsequent
            // published/pending selection is one atomic handoff with retirement.
            ReadSnapshot();
            BeforeDismissalSnapshotSelection?.Invoke();
            lock (_writerGate)
            {
                var snapshot = Volatile.Read(ref _memoryCache);
                if (snapshot is null)
                {
                    continue;
                }

                var user = snapshot.FirstOrDefault(result => result.UserId == userId);
                var dismissed = (user?.Entries ?? []).Where(entry => entry.DismissedAtUtc.HasValue).Select(entry => (entry.TmdbId, NormalizeMediaType(entry.MediaType))).ToHashSet();
                dismissed.UnionWith((user?.DismissedItems ?? []).OfType<DiscoveryDismissedItem>().Where(item => item.TmdbId > 0)
                    .Select(item => (item.TmdbId, NormalizeMediaType(item.MediaType))));
                dismissed.UnionWith(_pendingDismissals.Keys.Concat(_rejectedReceipts.Keys.Where(key => key.Kind == ReceiptKind.Dismissal).Select(key => key.Title))
                    .Where(title => title.User == userId).Select(title => (title.TmdbId, title.MediaType)));
                return dismissed;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<(int TmdbId, string MediaType)> GetRequestedItems(Guid userId)
    {
        var user = ReadSnapshot().FirstOrDefault(result => result.UserId == userId);
        return (user?.Entries ?? []).Where(entry => entry.RequestedAtUtc.HasValue).Select(entry => (entry.TmdbId, entry.MediaType)).ToHashSet();
    }

    /// <inheritdoc />
    public IReadOnlySet<(int TmdbId, string MediaType)> GetPendingRequestedItems(Guid userId)
    {
        lock (_writerGate)
        {
            return _pendingRequests.Keys.Concat(_rejectedReceipts.Keys.Where(key => key.Kind == ReceiptKind.Request).Select(key => key.Title))
                .Where(title => title.User == userId).Select(title => (title.TmdbId, title.MediaType)).ToHashSet();
        }
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
            DismissedItems = source.DismissedItems?.OfType<DiscoveryDismissedItem>().Select(item => item with { }).ToArray(),
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
                PredictionActionAtUtc = e.PredictionActionAtUtc,
                CardRequestedAtUtc = e.CardRequestedAtUtc,
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

            List<DiscoveryFeedbackResult> recovered;
            using (var stream = OpenFeedbackRead(_filePath))
            {
                recovered = JsonSerializer.Deserialize<List<DiscoveryFeedbackResult>>(stream, JsonOptions) ?? [];
            }

            if (fileInfo.Length > _sizeLimit)
            {
                _pluginLog.LogWarning(LogSource, $"Feedback file exceeds the size limit ({fileInfo.Length} bytes); recovering and compacting valid feedback.", null, _logger);
                if (!SaveInternal(recovered))
                {
                    Volatile.Write(ref _memoryCache, recovered);
                }
            }
            else
            {
                Volatile.Write(ref _memoryCache, recovered);
            }

            return (_memoryCache ?? recovered).Select(CloneResult).ToList();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A confirmed absent file is an empty store, unlike an unavailable read.
            Volatile.Write(ref _memoryCache, []);
            return [];
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
            else
            {
                // Unknown is not empty. Leave the cache unpublished so retained
                // writes retry the existing file rather than overwrite other users.
                throw new IOException("Discovery feedback could not be loaded; its durable state remains unknown.", ex);
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
            // Migrate/retain explicit dismissal identity before trimming training rows.
            // Expiration and the 200-row training cap must never re-offer a rejected title.
            var dismissed = (userResult.DismissedItems ?? []).OfType<DiscoveryDismissedItem>()
                .Where(item => item.TmdbId > 0).Select(item => item with { MediaType = NormalizeMediaType(item.MediaType) })
                .Concat(userResult.Entries.Where(entry => entry.TmdbId > 0 && entry.DismissedAtUtc.HasValue)
                    .Select(entry => new DiscoveryDismissedItem(entry.TmdbId, NormalizeMediaType(entry.MediaType))))
                .Distinct().OrderBy(item => item.MediaType, StringComparer.Ordinal).ThenBy(item => item.TmdbId).ToArray();
            userResult.DismissedItems = dismissed.Length == 0 ? null : dismissed;

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
        workingCopy.RemoveAll(r => r.Entries.Count == 0 && (r.DismissedItems?.Count ?? 0) == 0);

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
            // The published view is the last durable, immutable snapshot. A failed
            // atomic write must not discard known exclusions during a storage outage.
            // Pending receipts separately withhold fitting and retain their retry inputs.
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

        data.RemoveAll(result => result.Entries.Count == 0 && (result.DismissedItems?.Count ?? 0) == 0);
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

    /// <summary>Stops future retry work without blocking already-admitted atomic persistence.</summary>
    public void Dispose()
    {
        lock (_writerGate)
        {
            _disposed = true;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }
    }

    private readonly record struct TitleKey(Guid User, int TmdbId, string MediaType);

    private readonly record struct ReceiptKey(TitleKey Title, ReceiptKind Kind);

    private readonly record struct Receipt(ReceiptKey Key, DateTime Acquired, byte[]? Prediction = null, string? PredictionHash = null);

    private sealed record PredictionPayload(DiscoveryRecommendation Item, DateTime GeneratedAt);

    private sealed record PredictionReplay(string Hash, DateTime Acquired, byte[]? Payload);

    private sealed record PendingWrite(Func<bool> Operation, bool Observed, TaskCompletionSource<bool> Completion, Receipt[] Receipts, TitleKey[] Dependencies);
}
