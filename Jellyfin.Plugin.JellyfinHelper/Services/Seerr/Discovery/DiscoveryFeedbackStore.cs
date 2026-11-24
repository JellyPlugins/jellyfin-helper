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
    private const long MaximumPreparedRecoveryBytes = 64 * 1024 * 1024;
    private const long MaximumDeferredRecoveryBytes = 128 * 1024 * 1024;
    private const int MaximumIntentMaintenancePerRead = 16;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;
    private readonly Lock _fileLock = new();
    private readonly Lock _writerGate = new();
    private readonly Queue<PendingWrite> _writes = new();
    private readonly Queue<PendingWrite> _retryWrites = new();
    private readonly Dictionary<ReceiptKey, DateTime> _rejectedReceipts = new();
    private readonly Dictionary<ReceiptKey, PredictionReplay> _predictionReplays = new();
    private readonly Lock _recoveryInitializationGate = new();
    private readonly Lock _preparedRequestGate = new();
    private readonly HashSet<Guid> _activeRecoveryTickets = [];
    private readonly HashSet<Guid> _knownAcceptedIntents = [];
    private readonly HashSet<Guid> _completedPreparedRequests = [];
    private readonly Queue<Guid> _preparedCompletionOrder = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _preparedAttempts = [];
    private readonly HashSet<Guid> _uncertainPreparedIntents = [];
    private readonly Dictionary<TitleKey, int> _pendingRequests = new();
    private readonly Dictionary<TitleKey, int> _pendingDismissals = new();
    private readonly Dictionary<TitleKey, SortedDictionary<DateTime, int>> _pendingWatchTimes = [];
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryFeedbackStore> _logger;
    private readonly long _sizeLimit = MaxFileSizeBytes;
    // Normal/blocked work retains its 64-write bound. One separate recovery
    // slot can establish the request that makes all blocked dependents runnable.
    private PendingWrite? _priorityRecovery;
    private int _preparedAttemptCount;
    private PendingWrite? _retainedPriorityRecovery;
    private PendingWrite? _priorityRecoveryOwner;
    private long _replayPayloadBytes;
    private long _servingRevision;
    private bool _recoveryBlocked;
    private Task<bool>? _pendingGeneration;
    private bool _writerRunning;
    private int _pendingObservedWrites;
    private bool _untrackedRejection;
    private ITimer? _retryTimer;
    private long _retryTimerGeneration;
    private int _retryBackoff;
    private bool _disposed;
    private DiscoveryFeedbackRecoveryJournal? _recoveryJournal;
    private DiscoveryFeedbackRecoveryJournal? _preparedRecoveryJournal;
    private DiscoveryFeedbackRecoveryJournal? _deferredIntentJournal;
    private DiscoveryDismissalIdentityStore? _dismissalIdentityStore;

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
        RequestIntent,
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

    /// <summary>Gets the separate durable-original writer; recovery admission never depends on the display-delivery cache.</summary>
    internal Action<string, string> WriteRecovery { get; init; } = (path, contents) => AtomicFile.WriteAllText(path, contents);

    /// <summary>Gets the recovery completion boundary; failed deletion retains original evidence and retry backoff.</summary>
    internal Action<string> DeleteRecovery { get; init; } = File.Delete;

    /// <summary>Gets the journal enumeration boundary; unavailable originals must not become a cached empty journal.</summary>
    internal Func<string, IEnumerable<string>> EnumerateRecoveryFiles { get; init; } = directory => Directory.EnumerateFiles(directory, "*.json");

    /// <summary>Gets the independent permanent-dismissal write boundary.</summary>
    internal Action<string, string> WriteDismissalIdentities { get; init; } = (path, contents) => AtomicFile.WriteAllText(path, contents);

    /// <summary>Gets the independent permanent-dismissal read boundary.</summary>
    internal Func<string, Stream> OpenDismissalIdentityRead { get; init; } = path => File.OpenRead(path);

    /// <summary>Gets the independent permanent-dismissal inventory boundary.</summary>
    internal Func<string, IEnumerable<string>> EnumerateDismissalIdentityFiles { get; init; } = directory => Directory.EnumerateFiles(directory, "*.json");

    /// <summary>Gets the separate identity budget; test overrides cannot raise the production ceiling.</summary>
    internal long DismissalIdentityByteBudget { get; init; } = DiscoveryDismissalIdentityStore.MaximumStoreBytes;

    /// <summary>Gets the independent unresolved-submission admission budget.</summary>
    internal long PreparedRecoveryByteBudget { get; init; } = MaximumPreparedRecoveryBytes;

    /// <summary>Gets the independent dormant-original budget; absence never becomes a fitting label.</summary>
    internal long DeferredRecoveryByteBudget { get; init; } = MaximumDeferredRecoveryBytes;

    /// <summary>Gets the bounded in-memory overflow budget; durable originals remain recoverable independently of this budget.</summary>
    internal long ReplayPayloadBudget { get; init; } = MaximumReplayPayloadBytes;

    /// <summary>Gets the bounded completion-acknowledgment budget; overrides never raise the 4096-ID production ceiling.</summary>
    internal int PreparedAcknowledgmentLimit { get; init; } = MaximumRejectedReceipts;

    /// <summary>Gets the bounded upstream-attempt lease budget; overrides cannot exceed 4096.</summary>
    internal int PreparedAttemptLimit { get; init; } = MaximumRejectedReceipts;

    /// <summary>Gets the test synchronization hook immediately before atomic training-view selection.</summary>
    internal Action? BeforeTrainingSnapshotSelection { get; init; }

    /// <summary>Gets the test handoff hook before coherent serving snapshot selection.</summary>
    internal Action? BeforeDismissalSnapshotSelection { get; init; }

    /// <summary>Gets the cheap serving handoff version without taking a filesystem lock.</summary>
    internal long ServingRevision => Volatile.Read(ref _servingRevision);

    internal PreparedRequest PrepareRequestedCard(Guid user, DiscoveryRecommendation card, DateTime generatedAt, string sourceScope)
    {
        lock (_writerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var journals = RecoveryJournals();
        MoveLegacyPreparedOriginals(journals);
        lock (_preparedRequestGate)
        {
            EnsurePreparedAttemptCapacity();
            var existing = journals.SelectMany(journal => journal.Pending()).FirstOrDefault(ticket => ticket.Kind == nameof(ReceiptKind.RequestIntent) && ticket.User == user && ticket.Tmdb == card.TmdbId
                && ticket.Type == NormalizeMediaType(card.MediaType) && ticket.RequestSourceScope == sourceScope);
            if (existing is not null)
            {
                return RegisterPreparedAttempt(existing.Id, existing.SubmissionUncertain && !_preparedAttempts.ContainsKey(existing.Id));
            }
        }

        var receipt = CapturePredictionReceipt(new ReceiptKey(new TitleKey(user, card.TmdbId, NormalizeMediaType(card.MediaType)), ReceiptKind.RequestIntent), ReceiptTimeProvider.GetUtcNow().UtcDateTime, card.Clone(), generatedAt, sourceScope);
        lock (_writerGate)
        {
            _activeRecoveryTickets.Remove(receipt.RecoveryTicket!.Id);
        }

        lock (_preparedRequestGate)
        {
            return RegisterPreparedAttempt(receipt.RecoveryTicket.Id, false);
        }
    }

    private PreparedRequest RegisterPreparedAttempt(Guid id, bool previouslyUncertain)
    {
        EnsurePreparedAttemptCapacity();

        if (!_preparedAttempts.TryGetValue(id, out var attempts))
        {
            attempts = [];
            _preparedAttempts[id] = attempts;
        }

        var attempt = Guid.NewGuid();
        attempts.Add(attempt);
        _preparedAttemptCount++;
        if (previouslyUncertain)
        {
            _uncertainPreparedIntents.Add(id);
        }

        return new PreparedRequest(id, previouslyUncertain || _uncertainPreparedIntents.Contains(id), attempt);
    }

    private void EnsurePreparedAttemptCapacity()
    {
        if (_preparedAttemptCount >= Math.Clamp(PreparedAttemptLimit, 1, MaximumRejectedReceipts))
        {
            throw new IOException("The bounded prepared-request attempt registry is full; no additional upstream attempt was admitted.");
        }
    }

    internal void MarkPreparedSubmission(PreparedRequest prepared)
    {
        lock (_writerGate)
        {
            if (_completedPreparedRequests.Contains(prepared.Id))
            {
                return;
            }
        }

        PreparedJournalContaining(prepared.Id).UpdateIntent(prepared.Id, accepted: false, uncertain: true, ReceiptTimeProvider.GetUtcNow().UtcDateTime, prepared.Attempt == Guid.Empty ? null : prepared.Attempt);
    }

    internal void AbandonPreparedRequest(PreparedRequest prepared)
        => FinishPreparedAttempt(prepared, submissionUncertain: false);

    internal void FinishPreparedAttempt(PreparedRequest prepared, bool submissionUncertain, bool definitelyUnaccepted = false)
    {
        lock (_preparedRequestGate)
        {
            if (!ReleasePreparedAttemptLease(prepared))
            {
                return;
            }

            lock (_writerGate)
            {
                if (_completedPreparedRequests.Contains(prepared.Id))
                {
                    _uncertainPreparedIntents.Remove(prepared.Id);
                    return;
                }
            }

            var copies = PublishedRecoveryJournals().Select(journal => (Journal: journal, Ticket: journal.Find(prepared.Id))).Where(copy => copy.Ticket is not null).ToArray();
            if (copies.Length == 0)
            {
                // A late sibling may outlive the bounded completion acknowledgment.
                // Its released lease cannot recreate uncertainty for a retired original.
                _uncertainPreparedIntents.Remove(prepared.Id);
                return;
            }

            if (submissionUncertain || prepared.PreviouslyUncertain)
            {
                _uncertainPreparedIntents.Add(prepared.Id);
            }

            if (HasUncertainJournalSubmission(prepared, copies.Select(copy => copy.Ticket!).ToArray(), definitelyUnaccepted))
            {
                // An unchanged preparation handle cannot retire a later attempt's
                // possible POST, or uncertainty restored from the journal.
                _uncertainPreparedIntents.Add(prepared.Id);
            }

            if (_preparedAttempts.ContainsKey(prepared.Id) || _uncertainPreparedIntents.Contains(prepared.Id))
            {
                return;
            }

            lock (_writerGate)
            {
                if (_activeRecoveryTickets.Contains(prepared.Id) || _knownAcceptedIntents.Contains(prepared.Id) || _completedPreparedRequests.Contains(prepared.Id))
                {
                    return;
                }
            }

            if (copies.Any(copy => copy.Ticket!.Kind != nameof(ReceiptKind.RequestIntent)))
            {
                return;
            }

            if (copies.Any(copy => !DiscoveryFeedbackRecoveryJournal.SameOriginal(copies[0].Ticket!, copy.Ticket!)))
            {
                throw new IOException("Conflicting prepared originals cannot be retired together.");
            }

            foreach (var copy in copies)
            {
                copy.Journal.AbandonIntent(prepared.Id);
            }
        }
    }

    private bool ReleasePreparedAttemptLease(PreparedRequest prepared)
    {
        if (prepared.Attempt == Guid.Empty)
        {
            return true;
        }

        if (!_preparedAttempts.TryGetValue(prepared.Id, out var attempts) || !attempts.Remove(prepared.Attempt))
        {
            return false;
        }

        _preparedAttemptCount--;
        if (attempts.Count == 0)
        {
            _preparedAttempts.Remove(prepared.Id);
        }

        return true;
    }

    private bool HasUncertainJournalSubmission(PreparedRequest prepared, DiscoveryFeedbackRecoveryJournal.Ticket[] copies, bool definitelyUnaccepted)
        => !_preparedAttempts.ContainsKey(prepared.Id) && copies.Any(ticket => ticket.SubmissionUncertain
            && (!definitelyUnaccepted || prepared.Attempt == Guid.Empty || ticket.LastSubmissionAttempt != prepared.Attempt));

    internal IReadOnlySet<(int TmdbId, string MediaType)> GetPreparedRequestItems(Guid user, string? sourceScope)
        => RecoveryJournals().SelectMany(journal => journal.Pending()).Where(ticket => ticket.User == user && ticket.Kind == nameof(ReceiptKind.RequestIntent) && ticket.RequestSourceScope == sourceScope)
            .Select(ticket => (ticket.Tmdb, ticket.Type)).ToHashSet();

    internal async Task<bool> ConfirmPreparedRequestAsync(Guid user, PreparedRequest prepared, string? sourceScope, CancellationToken cancellationToken)
    {
        Receipt receipt;
        lock (_preparedRequestGate)
        {
            lock (_writerGate)
            {
                if (_completedPreparedRequests.Contains(prepared.Id))
                {
                    return true;
                }
            }

            var journal = PreparedJournalContaining(prepared.Id);
            var ticket = journal.Get(prepared.Id);
            if (ticket.User != user || ticket.RequestSourceScope != sourceScope)
            {
                throw new IOException("The original request does not match its validated user/source.");
            }

            receipt = new Receipt(new ReceiptKey(new TitleKey(user, ticket.Tmdb, ticket.Type), ReceiptKind.Request), ticket.Acquired, null, ticket.Hash, ticket) { Journal = journal };
            lock (_writerGate)
            {
                if (!_activeRecoveryTickets.Add(ticket.Id))
                {
                    return false;
                }

                _knownAcceptedIntents.Add(ticket.Id);
                Interlocked.Increment(ref _servingRevision);
            }
        }

        try
        {
            return await RunWriteAsync(() => ReplayPrediction(receipt), [receipt], observed: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FinishPreparedAttempt(prepared, submissionUncertain: false);
        }
    }

    internal async Task<bool> ConfirmPreparedForTitleAsync(Guid user, int tmdb, string type, string? sourceScope, CancellationToken cancellationToken)
    {
        var tickets = RecoveryJournals().SelectMany(journal => journal.Pending()).Where(ticket => ticket.User == user && ticket.Tmdb == tmdb && ticket.Type == type && ticket.Kind == nameof(ReceiptKind.RequestIntent) && ticket.RequestSourceScope == sourceScope).DistinctBy(ticket => ticket.Id).ToArray();
        var completed = true;
        foreach (var ticket in tickets)
        {
            completed &= await ConfirmPreparedRequestAsync(user, new PreparedRequest(ticket.Id, ticket.SubmissionUncertain), sourceScope, cancellationToken).ConfigureAwait(false);
        }

        return completed;
    }

    private DiscoveryFeedbackRecoveryJournal RecoveryJournal()
    {
        var current = Volatile.Read(ref _recoveryJournal);
        if (current is not null)
        {
            return current;
        }

        lock (_recoveryInitializationGate)
        {
            current = _recoveryJournal ?? new DiscoveryFeedbackRecoveryJournal(Path.Join(Path.GetDirectoryName(_filePath)!, "jellyfin-helper-feedback-recovery"), WriteRecovery, DeleteRecovery, EnumerateRecoveryFiles)
            {
                CounterPeers = () => PreparedRecoveryJournal().Pending().Concat(DeferredIntentJournal().Pending()).ToArray()
            };
            Volatile.Write(ref _recoveryJournal, current);
            return current;
        }
    }

    private DiscoveryFeedbackRecoveryJournal PreparedRecoveryJournal()
    {
        lock (_recoveryInitializationGate)
        {
            return _preparedRecoveryJournal ??= CreateIntentJournal(
                "jellyfin-helper-request-intents",
                PreparedRecoveryByteBudget,
                MaximumPreparedRecoveryBytes,
                () => RecoveryJournal().Pending().Concat(DeferredIntentJournal().Pending()).ToArray());
        }
    }

    private DiscoveryFeedbackRecoveryJournal DeferredIntentJournal()
    {
        lock (_recoveryInitializationGate)
        {
            return _deferredIntentJournal ??= CreateIntentJournal(
                "jellyfin-helper-deferred-request-intents",
                DeferredRecoveryByteBudget,
                MaximumDeferredRecoveryBytes,
                () => RecoveryJournal().Pending().Concat(PreparedRecoveryJournal().Pending()).ToArray());
        }
    }

    private DiscoveryFeedbackRecoveryJournal CreateIntentJournal(string name, long budget, long maximum, Func<DiscoveryFeedbackRecoveryJournal.Ticket[]> peers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, DiscoveryFeedbackRecoveryJournal.MetadataReservationBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, maximum);
        var data = Path.GetDirectoryName(_filePath)!;
        return new DiscoveryFeedbackRecoveryJournal(Path.Join(data, name), WriteRecovery, DeleteRecovery, EnumerateRecoveryFiles, budget, Path.Join(data, "jellyfin-helper-feedback-recovery"))
        {
            CounterPeers = peers
        };
    }

    private DiscoveryFeedbackRecoveryJournal[] RecoveryJournals()
        => [RecoveryJournal(), PreparedRecoveryJournal(), DeferredIntentJournal()];

    private IEnumerable<DiscoveryFeedbackRecoveryJournal> PublishedRecoveryJournals()
        => new[] { _recoveryJournal, _preparedRecoveryJournal, _deferredIntentJournal }.OfType<DiscoveryFeedbackRecoveryJournal>();

    private DiscoveryFeedbackRecoveryJournal PreparedJournalContaining(Guid id)
        => RecoveryJournals().FirstOrDefault(journal => journal.Find(id) is not null)
            ?? throw new IOException("The original prepared request is unavailable.");

    private void MoveLegacyPreparedOriginals(DiscoveryFeedbackRecoveryJournal[] journals)
    {
        var source = journals[0];
        lock (_preparedRequestGate)
        {
            foreach (var ticket in source.Pending().Where(ticket => ticket.Kind == nameof(ReceiptKind.RequestIntent)).Take(MaximumIntentMaintenancePerRead))
            {
                lock (_writerGate)
                {
                    if (_preparedAttempts.ContainsKey(ticket.Id) || _activeRecoveryTickets.Contains(ticket.Id) || _knownAcceptedIntents.Contains(ticket.Id))
                    {
                        continue;
                    }
                }

                try
                {
                    var payload = source.Read(ticket);
                    var destination = journals[1].HasImportCapacity(ticket, payload) ? journals[1] : journals[2];
                    destination.Import(ticket, payload);
                    // Migration is not a negative outcome: even a late acceptance
                    // remains recoverable from the same original in either index.
                    source.Complete(ticket.Id);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _pluginLog.LogWarning(LogSource, "Legacy prepared originals remain retained because their independent storage migration is deferred.", exception, _logger);
                    break;
                }
            }
        }
    }

    internal void ObserveCompleteRequestSnapshot(Guid user, IReadOnlySet<(int TmdbId, string MediaType)> requested, string? sourceScope)
    {
        var journals = RecoveryJournals();
        var archive = DeferredIntentJournal();
        var now = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        lock (_preparedRequestGate)
        {
            var candidates = journals.Where(journal => !ReferenceEquals(journal, archive)).SelectMany(journal => journal.Pending().Select(ticket => (Journal: journal, Ticket: ticket)))
                .Where(candidate => candidate.Ticket.User == user && candidate.Ticket.Kind == nameof(ReceiptKind.RequestIntent) && candidate.Ticket.RequestSourceScope == sourceScope)
                .Take(MaximumIntentMaintenancePerRead).ToArray();
            foreach (var candidate in candidates)
            {
                var ticket = candidate.Ticket;
                var submitted = ticket.LastSubmissionAtUtc ?? ticket.Acquired;
                if (requested.Contains((ticket.Tmdb, ticket.Type)) || _preparedAttempts.ContainsKey(ticket.Id) || now - submitted < TimeSpan.FromMinutes(15))
                {
                    continue;
                }

                lock (_writerGate)
                {
                    if (_knownAcceptedIntents.Contains(ticket.Id) || _activeRecoveryTickets.Contains(ticket.Id))
                    {
                        continue;
                    }
                }

                if (!ticket.FirstNegativeAtUtc.HasValue)
                {
                    candidate.Journal.ObserveNegative(ticket.Id, now);
                    continue;
                }

                if (now - ticket.FirstNegativeAtUtc.Value < TimeSpan.FromMinutes(5))
                {
                    continue;
                }

                // Two complete, spaced absence snapshots permit dormancy, not a
                // negative preference or destruction of possibly accepted evidence.
                // Copy first; a later matching GET can still recover the exact card.
                archive.Import(ticket, candidate.Journal.Read(ticket));
                candidate.Journal.Complete(ticket.Id);
            }
        }
    }

    private DiscoveryDismissalIdentityStore DismissalIdentities()
    {
        var current = Volatile.Read(ref _dismissalIdentityStore);
        if (current is not null)
        {
            return current;
        }

        lock (_recoveryInitializationGate)
        {
            current = _dismissalIdentityStore ?? new DiscoveryDismissalIdentityStore(
                Path.Join(Path.GetDirectoryName(_filePath)!, "jellyfin-helper-discovery-dismissals"),
                WriteDismissalIdentities,
                OpenDismissalIdentityRead,
                EnumerateDismissalIdentityFiles,
                DismissalIdentityByteBudget);
            Volatile.Write(ref _dismissalIdentityStore, current);
            return current;
        }
    }

    private void JoinRecoveryBacklog()
    {
        var journals = RecoveryJournals();
        MoveLegacyPreparedOriginals(journals);
        lock (_writerGate)
        {
            if (_disposed)
            {
                return;
            }

            PromoteRecoveryTickets(journals);
            StartWriterIfNeeded();
        }
    }

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

    private async Task<bool> RunWriteAsync(Func<bool> operation, Receipt[] receipts, bool observed, CancellationToken cancellationToken, Func<bool>? canAdmit = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            ReleaseUnadmittedRecovery(receipts);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writerGate)
        {
            if (_disposed)
            {
                ReleaseUnadmittedRecovery(receipts);
                throw new ObjectDisposedException(nameof(DiscoveryFeedbackStore));
            }

            if (canAdmit is not null && !canAdmit())
            {
                ReleaseUnadmittedRecovery(receipts);
                return false;
            }

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
                    ReleaseUnadmittedRecovery(receipts);
                }

                RestartRetainedWrites(activeWriter);
                return false;
            }

            while (_retryWrites.Count > 0)
            {
                _writes.Enqueue(_retryWrites.Dequeue());
            }

            var dependencies = PendingRequestDependencies(receipts);
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

    private void ReleaseUnadmittedRecovery(IEnumerable<Receipt> receipts)
    {
        lock (_writerGate)
        {
            foreach (var receipt in receipts)
            {
                if (receipt.RecoveryTicket is { } ticket)
                {
                    _activeRecoveryTickets.Remove(ticket.Id);
                }
            }

            ScheduleRetainedRetry();
        }
    }

    private void RestartRetainedWrites(bool activeWriter)
    {
        if (activeWriter)
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

        StartWriterIfNeeded();
    }

    private void StartWriterIfNeeded()
    {
        if ((_writes.Count > 0 || _priorityRecovery is not null) && !(_writerRunning && _pendingGeneration is { IsCompleted: false }))
        {
            _writerRunning = true;
            _pendingGeneration = Task.Run(ProcessWrites, CancellationToken.None);
        }
    }

    private void TrackRejectedReceipts(IEnumerable<Receipt> receipts)
    {
        Interlocked.Increment(ref _servingRevision);
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
        if (receipt.PredictionHash is null)
        {
            return;
        }

        if (_predictionReplays.TryGetValue(receipt.Key, out var prior))
        {
            var incoming = receipt.RecoveryTicket;
            var earlierOrder = IsEarlierAdmission(incoming?.AdmissionEpoch ?? Guid.Empty, incoming?.AdmissionOrder ?? 0, prior.Epoch, prior.Order);
            var keepPrior = receipt.Key.Kind == ReceiptKind.Request
                ? prior.Acquired >= receipt.Acquired
                : prior.Acquired < receipt.Acquired || (prior.Acquired == receipt.Acquired && !earlierOrder);
            if (keepPrior)
            {
                return;
            }

            _replayPayloadBytes -= prior.Payload?.LongLength ?? 0;
        }

        byte[]? payload = null;
        if (receipt.Prediction is { } bytes && _replayPayloadBytes + bytes.LongLength <= ReplayPayloadBudget)
        {
            payload = bytes;
            _replayPayloadBytes += bytes.LongLength;
        }

        _predictionReplays[receipt.Key] = new(receipt.PredictionHash, receipt.Acquired, payload)
        {
            Epoch = receipt.RecoveryTicket?.AdmissionEpoch,
            Order = receipt.RecoveryTicket?.AdmissionOrder
        };
        // Every admitted original also has a bounded durable recovery ticket.
        // Exhausting retry memory never discards its server-validated evidence.
    }

    private Receipt CapturePredictionReceipt(ReceiptKey key, DateTime acquired, DiscoveryRecommendation item, DateTime generatedAt, string? requestScope = null)
    {
        lock (_writerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PredictionPayload(item, generatedAt, item.KnownPeople?.ToArray()), JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        lock (_writerGate)
        {
            if (_predictionReplays.TryGetValue(key, out var replay) && replay.Hash == hash)
            {
                acquired = replay.Acquired;
            }
        }

        var id = Guid.NewGuid();
        lock (_writerGate)
        {
            _activeRecoveryTickets.Add(id);
        }

        try
        {
            var journal = key.Kind == ReceiptKind.RequestIntent ? PreparedRecoveryJournal() : RecoveryJournal();
            var ticket = journal.Admit(new DiscoveryFeedbackRecoveryJournal.Ticket(id, key.Title.User, key.Title.TmdbId, key.Title.MediaType, key.Kind.ToString(), acquired, hash) { RequestSourceScope = requestScope }, bytes);
            if (key.Kind != ReceiptKind.RequestIntent)
            {
                Interlocked.Increment(ref _servingRevision);
            }

            return new Receipt(key, acquired, bytes, hash, ticket, key.Kind == ReceiptKind.Impression ? new ReceiptProcessing() : null) { Journal = journal };
        }
        catch
        {
            lock (_writerGate)
            {
                _activeRecoveryTickets.Remove(id);
            }

            throw;
        }
    }

    private void PromoteRetainedPredictions()
    {
        if (_disposed)
        {
            return;
        }

        PromoteRecoveryTickets(PublishedRecoveryJournals());

        StartWriterIfNeeded();
    }

    private void PromoteRecoveryTickets(IEnumerable<DiscoveryFeedbackRecoveryJournal> journals)
    {
        var dependencies = _retryWrites.SelectMany(write => write.Dependencies).ToHashSet();
        var pending = journals.SelectMany(journal => journal.Pending().Select(ticket => (Journal: journal, Ticket: ticket)))
            .GroupBy(candidate => candidate.Ticket.Id).Select(SelectRecoveryOriginal)
            .Where(candidate => IsRunnableRecoveryTicket(candidate.Ticket))
            .OrderBy(candidate => IsRequestRecoveryTicket(candidate.Ticket) && dependencies.Contains(new TitleKey(candidate.Ticket.User, candidate.Ticket.Tmdb, candidate.Ticket.Type)) ? 0 : 1)
            .ThenBy(candidate => candidate.Ticket.Acquired).ThenBy(candidate => candidate.Ticket.AdmissionOrder);
        foreach (var candidate in pending)
        {
            var journal = candidate.Journal;
            var ticket = candidate.Ticket;
            if (!IsRunnableRecoveryTicket(ticket))
            {
                continue;
            }

            var priority = _writes.Count + _retryWrites.Count >= MaximumPendingWrites;
            if (priority && !CanAdmitPriorityRecovery())
            {
                break;
            }

            if (priority && (!IsRequestRecoveryTicket(ticket) || !dependencies.Contains(new TitleKey(ticket.User, ticket.Tmdb, ticket.Type))))
            {
                // The one reserved slot exists to break a dependency barrier.
                // An unrelated failing impression must not occupy it instead.
                continue;
            }

            AdmitRecoveryTicket(journal, ticket, priority);
        }
    }

    private void AdmitRecoveryTicket(DiscoveryFeedbackRecoveryJournal journal, DiscoveryFeedbackRecoveryJournal.Ticket ticket, bool priority)
    {
        var kind = ticket.Kind == nameof(ReceiptKind.RequestIntent) ? ReceiptKind.Request : Enum.Parse<ReceiptKind>(ticket.Kind);
        var key = new ReceiptKey(new TitleKey(ticket.User, ticket.Tmdb, ticket.Type), kind);
        var receipt = new Receipt(key, ticket.Acquired, null, ticket.Hash, ticket, key.Kind == ReceiptKind.Impression ? new ReceiptProcessing() : null) { Journal = journal };
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var write = new PendingWrite(() => ReplayPrediction(receipt), true, completion, [receipt], [], priority);
        if (priority)
        {
            _priorityRecovery = write;
            _priorityRecoveryOwner = write;
        }
        else
        {
            _writes.Enqueue(write);
        }

        _activeRecoveryTickets.Add(ticket.Id);
        TrackRejectedReceipts([receipt]);
        Interlocked.Increment(ref _pendingObservedWrites);
        AddPendingActions([receipt]);
    }

    private static (DiscoveryFeedbackRecoveryJournal Journal, DiscoveryFeedbackRecoveryJournal.Ticket Ticket) SelectRecoveryOriginal(
        IGrouping<Guid, (DiscoveryFeedbackRecoveryJournal Journal, DiscoveryFeedbackRecoveryJournal.Ticket Ticket)> replicas)
    {
        var original = replicas.First();
        foreach (var replica in replicas.Skip(1))
        {
            var requestStates = original.Ticket.Kind is nameof(ReceiptKind.Request) or nameof(ReceiptKind.RequestIntent)
                && replica.Ticket.Kind is nameof(ReceiptKind.Request) or nameof(ReceiptKind.RequestIntent);
            if (!DiscoveryFeedbackRecoveryJournal.SameOriginal(original.Ticket, replica.Ticket)
                || (original.Ticket.Kind != replica.Ticket.Kind && !requestStates))
            {
                throw new IOException("Conflicting originals occupy the same recovery identity; no replica was admitted.");
            }

            // Copy-before-delete transitions can retain an older intent replica.
            // Its identical inputs must not hide the durably accepted request state.
            if (replica.Ticket.Kind == nameof(ReceiptKind.Request))
            {
                original = replica;
            }
        }

        return original;
    }

    private bool IsRunnableRecoveryTicket(DiscoveryFeedbackRecoveryJournal.Ticket ticket)
        => !_activeRecoveryTickets.Contains(ticket.Id) && (ticket.Kind != nameof(ReceiptKind.RequestIntent) || _knownAcceptedIntents.Contains(ticket.Id));

    private bool IsRequestRecoveryTicket(DiscoveryFeedbackRecoveryJournal.Ticket ticket)
        => ticket.Kind == nameof(ReceiptKind.Request) || (ticket.Kind == nameof(ReceiptKind.RequestIntent) && _knownAcceptedIntents.Contains(ticket.Id));

    private bool CanAdmitPriorityRecovery()
        => _priorityRecoveryOwner is null && _priorityRecovery is null && _retainedPriorityRecovery is null && _writes.Count == 0 && _retryWrites.All(HasUndurableDependency);

    private bool ReplayPrediction(Receipt receipt)
    {
        // Deserialization and file I/O run on the managed worker, never while the
        // serving/readiness gate is held.
        var ticket = receipt.RecoveryTicket;
        var journal = receipt.Journal ?? RecoveryJournal();
        if (ticket?.Kind == nameof(ReceiptKind.RequestIntent))
        {
            ticket = journal.UpdateIntent(ticket.Id, accepted: true, uncertain: true);
        }

        var bytes = receipt.Prediction ?? journal.Read(ticket!);
        var payload = JsonSerializer.Deserialize<PredictionPayload>(bytes, JsonOptions)!;
        payload.Item.KnownPeople = payload.KnownPeople?.ToArray();
        return receipt.Key.Kind == ReceiptKind.Impression
            ? RecordImpressionsCore(receipt.Key.Title.User, [payload.Item], payload.GeneratedAt, receipt.Acquired, [receipt])
            : RecordReceiptActionCore(receipt, payload.Item, payload.GeneratedAt);
    }

    private bool RecordReceiptActionCore(Receipt receipt, DiscoveryRecommendation? item, DateTime? generatedAt)
    {
        var outcomeAt = receipt.Acquired;
        if (receipt.Key.Kind == ReceiptKind.Dismissal)
        {
            lock (_writerGate)
            {
                var rejectedAt = _rejectedReceipts.GetValueOrDefault(receipt.Key, outcomeAt);
                outcomeAt = rejectedAt > outcomeAt ? rejectedAt : outcomeAt;
            }
        }

        return RecordActionCore(receipt.Key.Title.User, receipt.Key.Title.TmdbId, receipt.Key.Title.MediaType, receipt.Key.Kind == ReceiptKind.Request, item, generatedAt, new ActionTiming(receipt.Acquired, outcomeAt, receipt.RecoveryTicket?.AdmissionEpoch ?? Guid.Empty, receipt.RecoveryTicket?.AdmissionOrder ?? 0));
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
        Interlocked.Increment(ref _servingRevision);
        foreach (var receipt in receipts)
        {
            var key = receipt.Key;
            if (key.Kind == ReceiptKind.Watched)
            {
                if (!_pendingWatchTimes.TryGetValue(key.Title, out var times))
                {
                    times = [];
                    _pendingWatchTimes[key.Title] = times;
                }

                times[receipt.Acquired] = times.GetValueOrDefault(receipt.Acquired) + 1;
            }
            else if (key.Kind is ReceiptKind.Request or ReceiptKind.Dismissal)
            {
                var pending = key.Kind == ReceiptKind.Request ? _pendingRequests : _pendingDismissals;
                pending[key.Title] = pending.GetValueOrDefault(key.Title) + 1;
            }
        }
    }

    private bool HasPendingRequest(TitleKey title)
        => _pendingRequests.ContainsKey(title) || _rejectedReceipts.ContainsKey(new ReceiptKey(title, ReceiptKind.Request));

    private TitleKey[] PendingRequestDependencies(Receipt[] receipts)
        => receipts.Where(receipt => receipt.Key.Kind == ReceiptKind.Watched)
            .Select(receipt => receipt.Key.Title).Where(HasPendingRequest).Distinct().ToArray();

    private bool HasUndurableDependency(PendingWrite write)
    {
        var snapshot = Volatile.Read(ref _memoryCache);
        return write.Receipts.Any(receipt => receipt.Key.Kind == ReceiptKind.Request && HasEarlierPendingWatch(receipt.Key.Title, receipt.Acquired))
            || write.Dependencies.Any(title => HasPendingRequest(title)
            && !(snapshot?.FirstOrDefault(user => user.UserId == title.User)?.Entries
                .Any(entry => entry.TmdbId == title.TmdbId && entry.MediaType == title.MediaType && entry.RequestedAtUtc.HasValue) ?? false));
    }

    private bool HasEarlierPendingWatch(TitleKey title, DateTime requestAt)
    {
        var watchedAt = _pendingWatchTimes.TryGetValue(title, out var times) ? times.Keys.First() : (DateTime?)null;
        if (_rejectedReceipts.TryGetValue(new ReceiptKey(title, ReceiptKind.Watched), out var rejected)
            && (!watchedAt.HasValue || rejected < watchedAt.Value))
        {
            watchedAt = rejected;
        }

        var entry = Volatile.Read(ref _memoryCache)?.FirstOrDefault(user => user.UserId == title.User)?.Entries
            .FirstOrDefault(entry => entry.TmdbId == title.TmdbId && entry.MediaType == title.MediaType);
        return watchedAt < requestAt && entry?.RequestedAtUtc <= watchedAt && entry?.WasWatched != true;
    }

    private bool ProcessWrites()
    {
        var successful = true;
        while (true)
        {
            PendingWrite write;
            lock (_writerGate)
            {
                if (_writes.Count == 0 && _priorityRecovery is null)
                {
                    _writerRunning = false;
                    ScheduleRetainedRetry();
                    return successful;
                }

                write = DequeueNextWrite();
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
                if (saved)
                {
                    CompleteRecoveryTickets(write);
                }
            }
            catch (Exception exception) when (!exception.IsFatal())
            {
                saved = false;
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

    private PendingWrite DequeueNextWrite()
    {
        if (_priorityRecovery is not { } priority)
        {
            return _writes.Dequeue();
        }

        _priorityRecovery = null;
        return priority;
    }

    private bool ReleaseWriterIfIdle()
    {
        lock (_writerGate)
        {
            if (_writes.Count > 0 || _priorityRecovery is not null)
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
                if (ReferenceEquals(_priorityRecoveryOwner, write))
                {
                    // The owner survives dequeue and failed writes. Only the
                    // successful feedback-and-ticket persistence barrier releases it.
                    _priorityRecoveryOwner = null;
                }

                RetireObservedWrite(write);
                PromoteRetainedPredictions();
                PromoteDependentWrites(write);
            }
            else
            {
                // Failed requests and dependent watches retain their original receipts.
                // One backoff timer restarts runnable retained work without requiring a new action.
                if (write.PriorityRecovery)
                {
                    // This reserved slot remains admitted but is restarted only
                    // by the backoff timer, never an immediate failing replay loop.
                    _retainedPriorityRecovery = write;
                }
                else if (_writes.Count + _retryWrites.Count < MaximumPendingWrites)
                {
                    _retryWrites.Enqueue(write);
                }
                else if (_priorityRecoveryOwner is null && IsRequestDependencyBarrier(write))
                {
                    // An identity-only request has no journal payload to rebuild.
                    // Retain its exact operation in the existing dependency slot.
                    var retained = write with { PriorityRecovery = true };
                    _priorityRecoveryOwner = retained;
                    _retainedPriorityRecovery = retained;
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

    private bool IsRequestDependencyBarrier(PendingWrite write)
        => write.Receipts.Any(receipt => receipt.Key.Kind == ReceiptKind.Request
            && _writes.Concat(_retryWrites).Any(waiting => waiting.Dependencies.Contains(receipt.Key.Title)));

    private bool HasRunnableRetainedWrites()
        => _retainedPriorityRecovery is not null || _retryWrites.Any(write => !HasUndurableDependency(write))
            || PublishedRecoveryJournals().Any(journal => journal.Pending().Any(IsRunnableRecoveryTicket));

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

            if (_retainedPriorityRecovery is { } retained)
            {
                _priorityRecovery = retained;
                _retainedPriorityRecovery = null;
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
        Interlocked.Increment(ref _servingRevision);
        Interlocked.Decrement(ref _pendingObservedWrites);
        if (_pendingObservedWrites == 0 && _retryWrites.Count == 0)
        {
            _retryBackoff = 0;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }

        foreach (var receipt in write.Receipts)
        {
            if (receipt.RecoveryTicket is { } ticket)
            {
                _activeRecoveryTickets.Remove(ticket.Id);
                if (recovered)
                {
                    _knownAcceptedIntents.Remove(ticket.Id);
                }
            }

            RetirePendingAction(receipt);

            if (recovered && _rejectedReceipts.TryGetValue(receipt.Key, out var rejectedAt) && CoversRejectedReceipt(receipt, rejectedAt))
            {
                _rejectedReceipts.Remove(receipt.Key);
                if (_predictionReplays.Remove(receipt.Key, out var replay))
                {
                    _replayPayloadBytes -= replay.Payload?.LongLength ?? 0;
                }
            }
        }
    }

    private void RetirePendingAction(Receipt receipt)
    {
        var key = receipt.Key;
        if (key.Kind == ReceiptKind.Watched && _pendingWatchTimes.TryGetValue(key.Title, out var times))
        {
            var remaining = times.GetValueOrDefault(receipt.Acquired) - 1;
            if (remaining <= 0)
            {
                times.Remove(receipt.Acquired);
            }
            else
            {
                times[receipt.Acquired] = remaining;
            }

            if (times.Count == 0)
            {
                _pendingWatchTimes.Remove(key.Title);
            }
        }

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
        var title = receipt.Key.Title;
        var entry = Volatile.Read(ref _memoryCache)?.FirstOrDefault(user => user.UserId == title.User)?.Entries
            .FirstOrDefault(entry => entry.TmdbId == title.TmdbId && entry.MediaType == title.MediaType);
        if (CoversDurableDismissal(receipt.Key, entry, rejectedAt))
        {
            // Permanent dismissal is an idempotent outcome. Its covering durable
            // time can acknowledge a later card without replacing original inputs.
            return true;
        }

        if (_predictionReplays.TryGetValue(receipt.Key, out var prediction))
        {
            if (receipt.PredictionHash != prediction.Hash)
            {
                return false;
            }

            if (receipt.Key.Kind == ReceiptKind.Impression && receipt.Processing?.OmittedByRetention == true)
            {
                // The exact original was processed by a successful save, then
                // deliberately excluded by retention. Missing is not this proof.
                return true;
            }

            // Exact prediction provenance alone cannot acknowledge an outcome's
            // timestamp. Impressions and dismissals also need their durable time.
            if (receipt.Key.Kind is not (ReceiptKind.Dismissal or ReceiptKind.Impression))
            {
                return true;
            }
        }
        else if (receipt.Key.Kind != ReceiptKind.Impression && receipt.Acquired >= rejectedAt)
        {
            return true;
        }

        // Reconciled requests and watches are idempotent states. An older exact
        // receipt becoming durable also covers a rejected duplicate confirmation;
        // Seerr will otherwise skip it as already recorded and never resubmit it.
        // Dismissals still require the rejected action's timestamp to be covered.
        return receipt.Key.Kind switch
        {
            ReceiptKind.Request => entry?.RequestedAtUtc.HasValue == true,
            ReceiptKind.Watched => entry?.WasWatched == true,
            ReceiptKind.Dismissal => entry?.DismissedAtUtc >= rejectedAt,
            // The store preserves first visibility. Its durable earlier receipt also
            // covers a later duplicate, but an absent impression never proves recovery.
            ReceiptKind.Impression => entry?.ImpressionAtUtc <= rejectedAt,
            _ => false,
        };
    }

    private bool CoversDurableDismissal(ReceiptKey key, DiscoveryFeedbackEntry? entry, DateTime rejectedAt)
    {
        if (key.Kind != ReceiptKind.Dismissal)
        {
            return false;
        }

        var title = key.Title;
        var permanentDismissalAt = _dismissalIdentityStore?.CachedSnapshot(title.User).GetValueOrDefault((title.TmdbId, title.MediaType));
        return entry?.DismissedAtUtc >= rejectedAt || permanentDismissalAt >= rejectedAt;
    }

    private void PromoteDependentWrites(PendingWrite completed)
    {
        if (_disposed || !completed.Receipts.Any(receipt => receipt.Key.Kind is ReceiptKind.Request or ReceiptKind.Watched))
        {
            return;
        }

        var requests = completed.Receipts.Where(receipt => receipt.Key.Kind == ReceiptKind.Request).Select(receipt => receipt.Key.Title).ToHashSet();
        var watches = completed.Receipts.Where(receipt => receipt.Key.Kind == ReceiptKind.Watched).Select(receipt => receipt.Key.Title).ToHashSet();
        var count = _retryWrites.Count;
        for (var index = 0; index < count; index++)
        {
            var waiting = _retryWrites.Dequeue();
            var resolved = waiting.Dependencies.Any(requests.Contains)
                || waiting.Receipts.Any(receipt => receipt.Key.Kind == ReceiptKind.Request && watches.Contains(receipt.Key.Title));
            if (resolved && !HasUndurableDependency(waiting))
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
                    HasRatingMetadata = item.HasRatingMetadata,
                    HasPopularityMetadata = item.HasPopularityMetadata,
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
        var receipts = CaptureImpressionReceipts(userId, snapshot, acquired, generatedAtUtc);
        var write = new PendingWrite(() => RecordImpressionsCore(userId, snapshot, generatedAtUtc, acquired, receipts), true, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), receipts, []);
        if (!ExecuteSynchronousWrite(write))
        {
            throw new IOException("Could not persist confirmed discovery impressions.");
        }
    }

    private Receipt[] CaptureImpressionReceipts(Guid userId, DiscoveryRecommendation[] items, DateTime acquired, DateTime generatedAtUtc)
    {
        var receipts = new List<Receipt>(items.Length);
        try
        {
            foreach (var item in items)
            {
                receipts.Add(CapturePredictionReceipt(new ReceiptKey(new TitleKey(userId, item.TmdbId, NormalizeMediaType(item.MediaType)), ReceiptKind.Impression), acquired, item, generatedAtUtc));
            }

            return receipts.ToArray();
        }
        catch
        {
            // Partial admission is still server-owned original evidence. Release
            // its capture leases so the managed worker can finish without a token.
            lock (_writerGate)
            {
                foreach (var receipt in receipts)
                {
                    _activeRecoveryTickets.Remove(receipt.RecoveryTicket!.Id);
                }

                ScheduleRetainedRetry();
            }

            throw;
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

                var ticket = receipts?[index].RecoveryTicket;
                var confirmation = new PredictionConfirmation(itemAcquired, false, false, ticket?.AdmissionEpoch ?? Guid.Empty, ticket?.AdmissionOrder ?? 0);
                if (entry.ImpressionAtUtc < itemAcquired || (entry.ImpressionAtUtc == itemAcquired && !IsEarlierReceipt(confirmation, entry)))
                {
                    continue;
                }

                // A verified action can establish the prediction before any viewport acknowledgement.
                // Later impressions may record visibility but must not rewrite that action's evidence.
                ConfirmPrediction(entry, item, generatedAtUtc, confirmation);
                entry.ImpressionAtUtc = itemAcquired;
                modified = true;
            }

            var saved = !modified || SaveInternal(data);
            if (saved && receipts is not null)
            {
                CaptureRetainedImpressionDisposition(userResult, receipts);
            }

            return saved;
        }
    }

    private void CaptureRetainedImpressionDisposition(DiscoveryFeedbackResult processed, IReadOnlyList<Receipt> receipts)
    {
        var durable = Volatile.Read(ref _memoryCache)?.FirstOrDefault(user => user.UserId == processed.UserId)?.Entries ?? [];
        foreach (var receipt in receipts)
        {
            if (receipt.Processing is not { } outcome)
            {
                continue;
            }

            var title = receipt.Key.Title;
            var applied = processed.Entries.FirstOrDefault(entry => entry.TmdbId == title.TmdbId && NormalizeMediaType(entry.MediaType) == title.MediaType);
            outcome.OmittedByRetention = applied?.ImpressionAtUtc <= receipt.Acquired
                && !durable.Any(entry => entry.TmdbId == title.TmdbId && NormalizeMediaType(entry.MediaType) == title.MediaType);
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
        if (!existing.HasConfirmedPrediction && existing.HasRatingMetadata == false && item.HasRatingMetadata == true)
        {
            existing.TmdbRating = item.TmdbRating;
            existing.HasRatingMetadata = true;
            modified = true;
        }
        else if (!existing.HasConfirmedPrediction && existing.HasRatingMetadata is null && existing.TmdbRating.Equals(0.0) && item.TmdbRating > 0)
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

        if (!existing.HasConfirmedPrediction && existing.HasPopularityMetadata == false && item.HasPopularityMetadata == true)
        {
            existing.Popularity = item.Popularity;
            existing.HasPopularityMetadata = true;
            modified = true;
        }
        else if (!existing.HasConfirmedPrediction && existing.HasPopularityMetadata is null && existing.Popularity.Equals(0.0) && item.Popularity > 0)
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
        => RecordRequestedCoreAsync(userId, tmdbId, mediaType, cancellationToken);

    /// <summary>Checks captured request provenance at the managed writer's actual receipt admission point.</summary>
    /// <param name="userId">The request owner.</param>
    /// <param name="tmdbId">The verified title.</param>
    /// <param name="mediaType">The verified namespace.</param>
    /// <param name="sourceScope">The Seerr source that supplied the complete request snapshot.</param>
    /// <param name="cancellationToken">The bounded caller wait.</param>
    /// <returns>False without admitting a receipt when the source changed.</returns>
    internal Task<bool> RecordRequestedForSourceAsync(Guid userId, int tmdbId, string mediaType, string? sourceScope, CancellationToken cancellationToken)
        => RecordRequestedCoreAsync(userId, tmdbId, mediaType, cancellationToken, () => SeerrDiscoveryService.RequestSourceScope(Plugin.Instance?.Configuration) == sourceScope);

    private Task<bool> RecordRequestedCoreAsync(Guid userId, int tmdbId, string mediaType, CancellationToken cancellationToken, Func<bool>? canAdmit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        mediaType = NormalizeMediaType(mediaType);
        var key = new ReceiptKey(new TitleKey(userId, tmdbId, mediaType), ReceiptKind.Request);
        acquired = FirstRequestTime(key, acquired);
        var receipt = new Receipt(key, acquired);
        return RunWriteAsync(() => RecordActionCore(userId, tmdbId, mediaType, requested: true, item: null, generatedAtUtc: null, acquired), [receipt], observed: true, cancellationToken, canAdmit);
    }

    private DateTime FirstRequestTime(ReceiptKey key, DateTime acquired)
    {
        lock (_writerGate)
        {
            if (_rejectedReceipts.TryGetValue(key, out var rejectedAt) && rejectedAt < acquired)
            {
                acquired = rejectedAt;
            }
        }

        return acquired;
    }

    private void RecordAction(Guid userId, int tmdbId, string mediaType, bool requested, DiscoveryRecommendation? item, DateTime? generatedAtUtc)
    {
        var snapshot = item?.Clone();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        mediaType = NormalizeMediaType(mediaType);
        var key = new ReceiptKey(new TitleKey(userId, tmdbId, mediaType), requested ? ReceiptKind.Request : ReceiptKind.Dismissal);
        if (requested && snapshot is null)
        {
            acquired = FirstRequestTime(key, acquired);
        }

        var receipt = snapshot is not null && generatedAtUtc.HasValue ? CapturePredictionReceipt(key, acquired, snapshot, generatedAtUtc.Value) : new Receipt(key, acquired);
        var write = new PendingWrite(
            () => RecordReceiptActionCore(receipt, snapshot, generatedAtUtc),
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
        bool blocked;
        lock (_writerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            write = write with { Dependencies = PendingRequestDependencies(write.Receipts) };
            Interlocked.Increment(ref _pendingObservedWrites);
            AddPendingActions(write.Receipts);
            blocked = HasUndurableDependency(write);
        }

        var saved = false;
        try
        {
            saved = !blocked && write.Operation();
            if (saved)
            {
                CompleteRecoveryTickets(write);
            }

            return saved;
        }
        catch
        {
            saved = false;
            throw;
        }
        finally
        {
            CompleteObservedWrite(write, saved);
        }
    }

    private void CompleteRecoveryTickets(PendingWrite write)
    {
        foreach (var receipt in write.Receipts.Where(receipt => receipt.RecoveryTicket is not null).DistinctBy(receipt => receipt.RecoveryTicket!.Id))
        {
            var original = receipt.RecoveryTicket!;
            var id = original.Id;
            lock (_preparedRequestGate)
            {
                var owner = receipt.Journal ?? RecoveryJournal();
                foreach (var journal in PublishedRecoveryJournals().Where(journal => !ReferenceEquals(journal, owner)))
                {
                    if (journal.Find(id) is { } replica)
                    {
                        if (!DiscoveryFeedbackRecoveryJournal.SameOriginal(original, replica))
                        {
                            throw new IOException("A conflicting original occupies another recovery journal.");
                        }

                        journal.Complete(id);
                    }
                }

                owner.Complete(id);
                AcknowledgePreparedCompletion(id);
            }
        }
    }

    private void AcknowledgePreparedCompletion(Guid id)
    {
        lock (_writerGate)
        {
            if (!_knownAcceptedIntents.Contains(id) || !_completedPreparedRequests.Add(id))
            {
                return;
            }

            _uncertainPreparedIntents.Remove(id);
            _preparedCompletionOrder.Enqueue(id);
            while (_preparedCompletionOrder.Count > Math.Clamp(PreparedAcknowledgmentLimit, 1, MaximumRejectedReceipts))
            {
                _completedPreparedRequests.Remove(_preparedCompletionOrder.Dequeue());
            }
        }
    }

    private bool RecordActionCore(Guid userId, int tmdbId, string mediaType, bool requested, DiscoveryRecommendation? item, DateTime? generatedAtUtc, DateTime acquired)
        => RecordActionCore(userId, tmdbId, mediaType, requested, item, generatedAtUtc, new ActionTiming(acquired, acquired));

    private bool RecordActionCore(Guid userId, int tmdbId, string mediaType, bool requested, DiscoveryRecommendation? item, DateTime? generatedAtUtc, ActionTiming timing)
    {
        if (tmdbId <= 0)
        {
            return true;
        }

        lock (_fileLock)
        {
            var data = LoadInternal();
            lock (_writerGate)
            {
                if (requested && HasEarlierPendingWatch(new TitleKey(userId, tmdbId, NormalizeMediaType(mediaType)), timing.PredictionAt))
                {
                    return false;
                }
            }

            var userResult = GetOrCreateUserResult(data, userId, userName: null);
            var normalizedType = NormalizeMediaType(mediaType);
            var entry = GetOrCreateEntry(userResult, tmdbId, normalizedType);
            UpdateActionPrediction(entry, item, generatedAtUtc, timing, requested);
            RecordActionTimestamp(entry, requested, timing.OutcomeAt);

            return SaveInternal(data);
        }
    }

    private static void UpdateActionPrediction(DiscoveryFeedbackEntry entry, DiscoveryRecommendation? item, DateTime? generatedAtUtc, ActionTiming timing, bool requested)
    {
        if (item is not null && generatedAtUtc.HasValue)
        {
            ConfirmPrediction(entry, item, generatedAtUtc.Value, new PredictionConfirmation(timing.PredictionAt, true, requested, timing.Epoch, timing.Order));
        }
        else if (!entry.HasConfirmedPrediction)
        {
            entry.ScoringFeatures = null;
            entry.TitleMetadata = null;
            if (!entry.RequestedAtUtc.HasValue && !entry.DismissedAtUtc.HasValue)
            {
                entry.ShownAtUtc = timing.PredictionAt;
            }
        }
    }

    private static void RecordActionTimestamp(DiscoveryFeedbackEntry entry, bool requested, DateTime acquired)
    {
        if (requested && entry.WasWatched && entry.RequestedAtUtc.HasValue)
        {
            return;
        }

        if (!requested)
        {
            entry.DismissedAtUtc = entry.DismissedAtUtc > acquired ? entry.DismissedAtUtc : acquired;
        }
        else if (entry.CardRequestedAtUtc is { } cardAt)
        {
            entry.RequestedAtUtc = cardAt;
        }
        else
        {
            entry.RequestedAtUtc = entry.RequestedAtUtc.HasValue && entry.RequestedAtUtc.Value < acquired ? entry.RequestedAtUtc : acquired;
        }
    }

    private static void ConfirmPrediction(DiscoveryFeedbackEntry entry, DiscoveryRecommendation item, DateTime generatedAtUtc, PredictionConfirmation confirmation)
    {
        var acquired = confirmation.Acquired;
        if (KeepConfirmedPrediction(entry, confirmation, generatedAtUtc))
        {
            return;
        }

        // An older exact receipt can reach disk after a later impression. Restore
        // the first actually confirmed inputs, never whichever write finished first.
        // A genuinely newer observed action remains authoritative over an older retry.
        entry.Title = item.Title;
        if (confirmation.Action)
        {
            entry.PredictionActionAtUtc = acquired;
            if (confirmation.Requested)
            {
                entry.CardRequestedAtUtc = acquired;
            }
        }

        entry.Year = item.Year;
        entry.Genres = item.Genres.ToArray();
        entry.KnownPeople = item.KnownPeople?.ToList();
        entry.TmdbRating = item.TmdbRating;
        entry.Popularity = item.Popularity;
        entry.HasRatingMetadata = item.HasRatingMetadata;
        entry.HasPopularityMetadata = item.HasPopularityMetadata;
        entry.Score = item.Score;
        entry.ScoringFeatures = item.ScoringFeatures?.Copy();
        entry.TitleMetadata = item.TitleMetadata;
        entry.ShownAtUtc = DateTimeNormalization.ToUtc(generatedAtUtc);
        entry.PredictionEvidenceAtUtc = acquired;
        entry.PredictionReceiptEpoch = confirmation.Epoch == Guid.Empty ? null : confirmation.Epoch;
        entry.PredictionReceiptOrder = confirmation.Order == 0 ? null : confirmation.Order;
    }

    private static bool KeepConfirmedPrediction(DiscoveryFeedbackEntry entry, PredictionConfirmation confirmation, DateTime generatedAt)
    {
        if (entry.CardRequestedAtUtc >= confirmation.Acquired
            || (confirmation.Action && confirmation.Requested && entry.WasWatched && entry.RequestedAtUtc.HasValue && !CanRepairCompletedRequest(entry, confirmation.Acquired, generatedAt)))
        {
            return true;
        }

        if (!confirmation.Action)
        {
            return entry.PredictionActionAtUtc.HasValue || PrecedesReceipt(entry.PredictionEvidenceAtUtc ?? entry.ImpressionAtUtc, confirmation, entry);
        }

        return !confirmation.Requested && PrecedesReceipt(entry.PredictionActionAtUtc, confirmation, entry);
    }

    private static bool PrecedesReceipt(DateTime? committed, PredictionConfirmation incoming, DiscoveryFeedbackEntry entry)
        => committed < incoming.Acquired || (committed == incoming.Acquired && !IsEarlierReceipt(incoming, entry));

    private static bool IsEarlierReceipt(PredictionConfirmation incoming, DiscoveryFeedbackEntry entry)
        => IsEarlierAdmission(incoming.Epoch, incoming.Order, entry.PredictionReceiptEpoch, entry.PredictionReceiptOrder);

    private static bool IsEarlierAdmission(Guid incomingEpoch, long incomingOrder, Guid? committedEpoch, long? committedOrder)
        => incomingEpoch != Guid.Empty && incomingOrder > 0 && committedEpoch == incomingEpoch && committedOrder > incomingOrder;

    private static bool CanRepairCompletedRequest(DiscoveryFeedbackEntry entry, DateTime acquired, DateTime generatedAt)
        => entry.CardRequestedAtUtc is null && acquired <= entry.RequestedAtUtc
            && DateTimeNormalization.ToUtc(generatedAt) <= entry.RequestedAtUtc;

    /// <inheritdoc />
    public void MarkWatched(Guid userId, IReadOnlySet<(int TmdbId, string MediaType)> watchedItems)
    {
        ArgumentNullException.ThrowIfNull(watchedItems);
        if (watchedItems.Count == 0)
        {
            return;
        }

        var snapshot = watchedItems.Select(title => (title.TmdbId, MediaType: NormalizeMediaType(title.MediaType))).ToHashSet();
        var acquired = ReceiptTimeProvider.GetUtcNow().UtcDateTime;
        var receipts = snapshot.Select(title => new Receipt(new ReceiptKey(new TitleKey(userId, title.TmdbId, title.MediaType), ReceiptKind.Watched), acquired)).ToArray();
        var write = new PendingWrite(() => MarkWatchedCore(userId, snapshot, acquired), true, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), receipts, []);
        if (!ExecuteSynchronousWrite(write))
        {
            throw new IOException("Could not persist watched feedback; its original operation remains pending.");
        }
    }

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

    private bool MarkWatchedCore(Guid userId, HashSet<(int TmdbId, string MediaType)> watchedItems, DateTime acquired)
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
                          && entry.RequestedAtUtc.Value <= acquired
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
        JoinRecoveryBacklog();
        List<DiscoveryFeedbackResult>? snapshot;
        do
        {
            // Cold loading happens outside the readiness gate. Warm snapshot selection
            // and readiness form one linearization point, independent of stalled I/O.
            BeforeTrainingSnapshotSelection?.Invoke();
            lock (_writerGate)
            {
                if (_pendingObservedWrites > 0 || _rejectedReceipts.Count > 0 || _untrackedRejection || _knownAcceptedIntents.Count > 0 || PublishedRecoveryJournals().Any(journal => journal.HasPending))
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
        var permanent = DismissalIdentities().Snapshot(userId);
        if (result is null && permanent.Count == 0)
        {
            return null;
        }

        var copy = result is null ? new DiscoveryFeedbackResult { UserId = userId } : CloneResult(result);
        var identities = (copy.DismissedItems ?? []).OfType<DiscoveryDismissedItem>()
            .Concat(permanent.Keys.Select(key => new DiscoveryDismissedItem(key.TmdbId, key.MediaType))).Distinct().ToArray();
        copy.DismissedItems = identities.Length == 0 ? null : identities;
        return copy;
    }

    /// <inheritdoc />
    public IReadOnlySet<(int TmdbId, string MediaType)> GetDismissedItems(Guid userId)
    {
        JoinRecoveryBacklog();
        var identities = DismissalIdentities();
        while (true)
        {
            // Cold disk I/O must not run under the receipt gate. The subsequent
            // published/pending selection is one atomic handoff with retirement.
            ReadSnapshot();
            var permanent = identities.Snapshot(userId);
            var dismissed = permanent.Keys.ToHashSet();
            BeforeDismissalSnapshotSelection?.Invoke();
            lock (_writerGate)
            {
                var snapshot = Volatile.Read(ref _memoryCache);
                if (snapshot is null || !ReferenceEquals(permanent, identities.CachedSnapshot(userId)))
                {
                    continue;
                }

                var user = snapshot.FirstOrDefault(result => result.UserId == userId);
                dismissed.UnionWith((user?.Entries ?? []).Where(entry => entry.DismissedAtUtc.HasValue).Select(entry => (entry.TmdbId, NormalizeMediaType(entry.MediaType))));
                dismissed.UnionWith((user?.DismissedItems ?? []).OfType<DiscoveryDismissedItem>().Where(item => item.TmdbId > 0)
                    .Select(item => (item.TmdbId, NormalizeMediaType(item.MediaType))));
                dismissed.UnionWith(_pendingDismissals.Keys.Concat(_rejectedReceipts.Keys.Where(key => key.Kind == ReceiptKind.Dismissal).Select(key => key.Title))
                    .Where(title => title.User == userId).Select(title => (title.TmdbId, title.MediaType)));
                dismissed.UnionWith(_recoveryJournal!.Pending().Where(ticket => ticket.User == userId && ticket.Kind == nameof(ReceiptKind.Dismissal)).Select(ticket => (ticket.Tmdb, ticket.Type)));
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
        JoinRecoveryBacklog();
        lock (_writerGate)
        {
            var pending = _pendingRequests.Keys.Concat(_rejectedReceipts.Keys.Where(key => key.Kind == ReceiptKind.Request).Select(key => key.Title))
                .Where(title => title.User == userId).Select(title => (title.TmdbId, title.MediaType)).ToHashSet();
            pending.UnionWith(PublishedRecoveryJournals().SelectMany(journal => journal.Pending()).Where(ticket => ticket.User == userId && (ticket.Kind == nameof(ReceiptKind.Request) || _knownAcceptedIntents.Contains(ticket.Id))).Select(ticket => (ticket.Tmdb, ticket.Type)));
            return pending;
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
                HasRatingMetadata = e.HasRatingMetadata,
                HasPopularityMetadata = e.HasPopularityMetadata,
                Score = e.Score,
                ScoringFeatures = e.ScoringFeatures?.Copy(),
                TitleMetadata = e.TitleMetadata,
                ImpressionAtUtc = e.ImpressionAtUtc,
                PredictionEvidenceAtUtc = e.PredictionEvidenceAtUtc,
                PredictionActionAtUtc = e.PredictionActionAtUtc,
                PredictionReceiptEpoch = e.PredictionReceiptEpoch,
                PredictionReceiptOrder = e.PredictionReceiptOrder,
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
                throw new IOException("Discovery feedback exceeds the bounded recovery limit; its durable state is unavailable.");
            }

            List<DiscoveryFeedbackResult> recovered;
            using (var stream = OpenFeedbackRead(_filePath))
            {
                recovered = JsonSerializer.Deserialize<List<DiscoveryFeedbackResult>>(stream, JsonOptions) ?? [];
            }

            // A repaired replacement has now been read and validated under the
            // file lock. Compaction and retained writes may proceed again.
            _recoveryBlocked = false;

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
            _recoveryBlocked = false;
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
            // Copy permanent identities durably before removing the legacy field
            // or trimming training rows. A failed migration leaves the original
            // feedback file intact; identity growth never charges fitting storage.
            var dismissed = (userResult.DismissedItems ?? []).OfType<DiscoveryDismissedItem>().Where(item => item.TmdbId > 0)
                .Select(item => new DiscoveryDismissalIdentityStore.Identity(item.TmdbId, NormalizeMediaType(item.MediaType)))
                .Concat(userResult.Entries.Where(entry => entry.TmdbId > 0 && entry.DismissedAtUtc.HasValue)
                    .Select(entry => new DiscoveryDismissalIdentityStore.Identity(entry.TmdbId, NormalizeMediaType(entry.MediaType), entry.DismissedAtUtc))).ToArray();
            DismissalIdentities().Remember(userResult.UserId, dismissed);
            userResult.DismissedItems = null;

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
            Interlocked.Increment(ref _servingRevision);
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

    private readonly record struct Receipt(ReceiptKey Key, DateTime Acquired, byte[]? Prediction = null, string? PredictionHash = null, DiscoveryFeedbackRecoveryJournal.Ticket? RecoveryTicket = null, ReceiptProcessing? Processing = null)
    {
        internal DiscoveryFeedbackRecoveryJournal? Journal { get; init; }
    }

    private readonly record struct ActionTiming(DateTime PredictionAt, DateTime OutcomeAt, Guid Epoch = default, long Order = 0);

    private readonly record struct PredictionConfirmation(DateTime Acquired, bool Action, bool Requested, Guid Epoch, long Order);

    private sealed record PredictionPayload(DiscoveryRecommendation Item, DateTime GeneratedAt, IReadOnlyList<string>? KnownPeople);

    private sealed record PredictionReplay(string Hash, DateTime Acquired, byte[]? Payload)
    {
        internal Guid? Epoch { get; init; }

        internal long? Order { get; init; }
    }

    private sealed record PendingWrite(Func<bool> Operation, bool Observed, TaskCompletionSource<bool> Completion, Receipt[] Receipts, TitleKey[] Dependencies, bool PriorityRecovery = false);

    private sealed class ReceiptProcessing
    {
        internal bool OmittedByRetention { get; set; }
    }

    internal sealed record PreparedRequest(Guid Id, bool PreviouslyUncertain, Guid Attempt = default);
}
