using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Bounded server-owned original evidence, independent of response delivery tokens and retry-memory capacity.</summary>
internal sealed class DiscoveryFeedbackRecoveryJournal
{
    /// <summary>Bounded counter metadata reserved inside the existing total journal budget.</summary>
    internal const long MetadataReservationBytes = 128;

    private const int MaximumTickets = 4096;
    private const string RequestKind = "Request";
    private const string IntentKind = "RequestIntent";
    private const long MaximumPayloadBytes = 16 * 1024 * 1024;
    private const long MaximumJournalBytes = 256 * 1024 * 1024;
    private readonly string _directory;
    private readonly string _counterDirectory;
    private readonly Lock _gate = new();
    private readonly Lock _intentGate = new();
    private readonly Dictionary<Guid, Ticket> _tickets = [];
    private readonly HashSet<Guid> _reservedIds = [];
    private readonly Action<string, string> _write;
    private readonly Action<string> _delete;
    private readonly Func<string, IEnumerable<string>> _enumerate;
    private readonly long _byteBudget;
    private long _bytes = MetadataReservationBytes;
    private int _reserved;
    private CounterState? _lastCounter;

    internal DiscoveryFeedbackRecoveryJournal(string directory, Action<string, string> write, Action<string>? delete = null, Func<string, IEnumerable<string>>? enumerate = null, long byteBudget = MaximumJournalBytes, string? counterDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteBudget, MetadataReservationBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteBudget, MaximumJournalBytes);
        _directory = directory;
        _counterDirectory = counterDirectory ?? directory;
        _write = write;
        _delete = delete ?? File.Delete;
        _enumerate = enumerate ?? (path => Directory.EnumerateFiles(path, "*.json"));
        _byteBudget = byteBudget;
        Load();
    }

    /// <summary>Gets the atomic admission-counter write boundary, separate from stalled payload I/O.</summary>
    internal Action<string, string> WriteCounterState { get; init; } = (path, contents) => AtomicFile.WriteAllText(path, contents);

    /// <summary>Gets the original-envelope read boundary; unavailable evidence cannot become an empty journal.</summary>
    internal Func<string, Stream> OpenEnvelopeRead { get; init; } = path => File.OpenRead(path);

    /// <summary>Gets the other bounded journals sharing the same durable admission counter.</summary>
    internal Func<Ticket[]>? CounterPeers { get; init; }

    internal bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _reserved > 0 || _tickets.Values.Any(ticket => ticket.Kind != IntentKind);
            }
        }
    }

    internal Ticket[] Pending()
    {
        lock (_gate)
        {
            return _tickets.Values.OrderBy(ticket => ticket.Acquired).ThenBy(ticket => ticket.AdmissionOrder).ToArray();
        }
    }

    internal Ticket Admit(Ticket ticket, byte[] payload)
        => AdmitCore(ticket, payload, preserveOrder: false);

    /// <summary>Copies original evidence without renewing its acquisition, prediction hash, or admission order.</summary>
    /// <param name="ticket">The validated original receipt, including its existing order.</param>
    /// <param name="payload">The exact server-owned prediction payload.</param>
    /// <returns>The same original indexed durably in this journal.</returns>
    internal Ticket Import(Ticket ticket, byte[] payload)
    {
        var existing = Find(ticket.Id);
        if (existing is not null)
        {
            return SameOriginal(existing, ticket) ? existing : throw new IOException("A different original already owns this recovery identity.");
        }

        return AdmitCore(ticket, payload, preserveOrder: true);
    }

    internal static bool SameOriginal(Ticket left, Ticket right)
        => left.Id == right.Id && left.User == right.User && left.Tmdb == right.Tmdb && left.Type == right.Type && left.Acquired == right.Acquired
            && left.Hash == right.Hash && left.AdmissionEpoch == right.AdmissionEpoch && left.AdmissionOrder == right.AdmissionOrder && left.RequestSourceScope == right.RequestSourceScope;

    internal bool HasImportCapacity(Ticket ticket, byte[] payload)
    {
        if (Find(ticket.Id) is { } existing)
        {
            return SameOriginal(existing, ticket) ? true : throw new IOException("A different original already occupies the destination identity.");
        }

        if (ticket.Kind == IntentKind)
        {
            ticket = ticket with { IntentSchemaVersion = 1 };
        }

        var length = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new Envelope(ticket, payload), JsonDefaults.Options));
        var reservation = ReservedLength(ticket, payload, length);
        lock (_gate)
        {
            // Import still performs the atomic reservation; this is only a
            // destination-selection hint and never authorizes a source deletion.
            return _tickets.Count + _reserved < MaximumTickets && _bytes + reservation <= _byteBudget;
        }
    }

    private Ticket AdmitCore(Ticket ticket, byte[] payload, bool preserveOrder)
    {
        if (payload.LongLength > MaximumPayloadBytes)
        {
            throw new IOException("Original feedback evidence exceeds the bounded recovery admission limit.");
        }

        if (ticket.Id == Guid.Empty || ticket.User == Guid.Empty || ticket.Tmdb <= 0 || ticket.Type is not ("movie" or "tv")
            || ticket.Kind is not (RequestKind or "Dismissal" or "Impression" or IntentKind) || Convert.ToHexString(SHA256.HashData(payload)) != ticket.Hash
            || ticket.AdmissionOrder < 0 || (ticket.AdmissionEpoch == Guid.Empty) != (ticket.AdmissionOrder == 0) || ticket.IntentSchemaVersion is < 0 or > 1)
        {
            throw new IOException("Original feedback recovery admission has invalid provenance.");
        }

        lock (_gate)
        {
            if (_tickets.ContainsKey(ticket.Id) || !_reservedIds.Add(ticket.Id))
            {
                throw new IOException("The original feedback identity is already admitted or being persisted.");
            }
        }

        try
        {
            if (ticket.Kind == IntentKind)
            {
                ticket = ticket with { IntentSchemaVersion = 1 };
            }

            var admission = preserveOrder ? ReserveEnvelope(ticket, payload) : ReserveOrderedAdmission(ticket, payload);
            ticket = admission.Ticket;
            try
            {
                _write(PathFor(ticket.Id), admission.Contents);
                lock (_gate)
                {
                    _tickets.Add(ticket.Id, ticket);
                    _reserved--;
                }

                return ticket;
            }
            catch
            {
                ReleaseReservation(ticket.ReservedBytes);
                throw;
            }
        }
        finally
        {
            lock (_gate)
            {
                _reservedIds.Remove(ticket.Id);
            }
        }
    }

    private (Ticket Ticket, string Contents) ReserveOrderedAdmission(Ticket ticket, byte[] payload)
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_counterDirectory);
        using var admission = OpenAdmissionLease();
        var counter = ReadCounter();
        var peers = CounterPeers?.Invoke() ?? [];
        lock (_gate)
        {
            var existing = _tickets.Values.Concat(peers).Where(value => value.AdmissionEpoch != Guid.Empty).ToArray();
            if (counter is null)
            {
                var epochs = existing.Select(value => value.AdmissionEpoch).Distinct().ToArray();
                if (epochs.Length > 1)
                {
                    throw new IOException("The original feedback admission epochs are inconsistent.");
                }

                counter = _lastCounter ?? new CounterState(epochs.FirstOrDefault(Guid.NewGuid()), existing.Select(value => value.AdmissionOrder).DefaultIfEmpty(0).Max());
            }

            if (existing.Any(value => value.AdmissionEpoch != counter.Epoch))
            {
                throw new IOException("The original feedback admission counter is inconsistent.");
            }

            if (_lastCounter is { } allocated && allocated.Epoch != counter.Epoch)
            {
                throw new IOException("The live feedback admission epoch changed unexpectedly.");
            }

            var order = Math.Max(Math.Max(counter.Order, _lastCounter?.Order ?? 0), existing.Select(value => value.AdmissionOrder).DefaultIfEmpty(0).Max());
            if (order == long.MaxValue)
            {
                throw new IOException("The feedback admission order is exhausted.");
            }

            counter = counter with { Order = order + 1 };
        }

        ticket = ticket with { AdmissionEpoch = counter.Epoch, AdmissionOrder = counter.Order };
        var envelope = ReserveEnvelope(ticket, payload);

        try
        {
            // Only the bounded counter update owns the cross-instance file lease.
            // Payload I/O may stall independently without blocking later admission.
            WriteCounterState(CounterPath(), JsonSerializer.Serialize(counter, JsonDefaults.Options));
            lock (_gate)
            {
                _lastCounter = counter;
            }

            return envelope;
        }
        catch
        {
            ReleaseReservation(envelope.Ticket.ReservedBytes);
            throw;
        }
    }

    private (Ticket Ticket, string Contents) ReserveEnvelope(Ticket ticket, byte[] payload)
    {
        var contents = JsonSerializer.Serialize(new Envelope(ticket, payload), JsonDefaults.Options);
        var length = Encoding.UTF8.GetByteCount(contents);
        var reservation = ReservedLength(ticket, payload, length);
        ValidateEnvelopeLength(reservation);
        lock (_gate)
        {
            if (_tickets.Count + _reserved >= MaximumTickets || _bytes + reservation > _byteBudget)
            {
                throw new IOException("The bounded feedback recovery journal is full; new evidence was not admitted.");
            }

            _reserved++;
            _bytes += reservation;
        }

        return (ticket with { FileBytes = length, ReservedBytes = reservation }, contents);
    }

    private static long ReservedLength(Ticket ticket, byte[] payload, long length)
    {
        if (ticket.IntentSchemaVersion == 0)
        {
            return length;
        }

        var maximum = ticket with
        {
            Kind = IntentKind, SubmissionUncertain = false,
            LastSubmissionAtUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
            LastSubmissionAttempt = ticket.Id,
            FirstNegativeAtUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
        };
        return Math.Max(length, Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new Envelope(maximum, payload), JsonDefaults.Options)));
    }

    private void ReleaseReservation(long length)
    {
        lock (_gate)
        {
            _reserved--;
            _bytes -= length;
        }
    }

    internal Ticket Get(Guid id)
    {
        lock (_gate)
        {
            return _tickets.GetValueOrDefault(id) ?? throw new IOException("The prepared original request is unavailable.");
        }
    }

    internal Ticket? Find(Guid id)
    {
        lock (_gate)
        {
            return _tickets.GetValueOrDefault(id);
        }
    }

    internal Ticket UpdateIntent(Guid id, bool accepted, bool uncertain, DateTime? submittedAtUtc = null, Guid? submissionAttempt = null)
    {
        lock (_intentGate)
        {
            return UpdateIntentCore(id, accepted, uncertain, submittedAtUtc, submissionAttempt);
        }
    }

    private Ticket UpdateIntentCore(Guid id, bool accepted, bool uncertain, DateTime? submittedAtUtc, Guid? submissionAttempt)
    {
        var current = Get(id);
        if (current.Kind == RequestKind)
        {
            return current;
        }

        if (current.Kind != IntentKind)
        {
            throw new IOException("Only prepared original request evidence can be confirmed.");
        }

        var next = current with
        {
            Kind = accepted ? RequestKind : IntentKind,
            SubmissionUncertain = uncertain,
            LastSubmissionAtUtc = submittedAtUtc ?? current.LastSubmissionAtUtc,
            LastSubmissionAttempt = submittedAtUtc.HasValue ? submissionAttempt : current.LastSubmissionAttempt,
            FirstNegativeAtUtc = submittedAtUtc.HasValue ? null : current.FirstNegativeAtUtc
        };
        return SaveStatus(current, next);
    }

    internal Ticket ObserveNegative(Guid id, DateTime observedAtUtc)
    {
        lock (_intentGate)
        {
            var current = Get(id);
            return current.Kind != IntentKind || current.FirstNegativeAtUtc.HasValue
                ? current
                : SaveStatus(current, current with { FirstNegativeAtUtc = observedAtUtc });
        }
    }

    private Ticket SaveStatus(Ticket current, Ticket next)
    {
        var payload = Read(current);
        var contents = JsonSerializer.Serialize(new Envelope(next, payload), JsonDefaults.Options);
        var length = Encoding.UTF8.GetByteCount(contents);
        var reservation = ReservedLength(next, payload, length);
        ValidateEnvelopeLength(reservation);
        var growth = Math.Max(0, reservation - current.ReservedBytes);
        lock (_gate)
        {
            if (_bytes + growth > _byteBudget)
            {
                throw new IOException("Prepared request confirmation exceeds the recovery journal limit.");
            }

            _bytes += growth;
        }

        try
        {
            _write(PathFor(current.Id), contents);
            lock (_gate)
            {
                _tickets[current.Id] = next with { FileBytes = length, ReservedBytes = reservation };
                _bytes -= current.ReservedBytes + growth - reservation;
                return _tickets[current.Id];
            }
        }
        catch
        {
            lock (_gate)
            {
                _bytes -= growth;
            }

            throw;
        }
    }

    private string CounterPath() => Path.Join(_counterDirectory, ".admission-order");

    private static void ValidateEnvelopeLength(long length)
    {
        if (length > 2 * MaximumPayloadBytes)
        {
            throw new IOException("Original feedback evidence cannot be admitted beyond its envelope read limit.");
        }
    }

    private FileStream OpenAdmissionLease()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return File.Open(Path.Join(_counterDirectory, ".admission-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        }

        return File.Open(Path.Join(_counterDirectory, ".admission-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private CounterState? ReadCounter()
    {
        try
        {
            using var stream = File.OpenRead(CounterPath());
            if (stream.Length > MetadataReservationBytes)
            {
                throw new IOException("The feedback admission counter exceeds its bounded size.");
            }

            var counter = JsonSerializer.Deserialize<CounterState>(stream, JsonDefaults.Options);
            return counter is { Epoch: var epoch, Order: >= 0 } && epoch != Guid.Empty ? counter : throw new IOException("The feedback admission counter is invalid.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException exception)
        {
            throw new IOException("The feedback admission counter is malformed.", exception);
        }
    }

    internal byte[] Read(Ticket ticket)
    {
        var envelope = ReadEnvelope(PathFor(ticket.Id));
        if (envelope.Ticket.Id != ticket.Id || envelope.Ticket.User != ticket.User || envelope.Ticket.Tmdb != ticket.Tmdb
            || envelope.Ticket.Type != ticket.Type || envelope.Ticket.Kind != ticket.Kind || envelope.Ticket.Acquired != ticket.Acquired
            || envelope.Ticket.Hash != ticket.Hash || envelope.Ticket.AdmissionEpoch != ticket.AdmissionEpoch || envelope.Ticket.AdmissionOrder != ticket.AdmissionOrder
            || envelope.Ticket.RequestSourceScope != ticket.RequestSourceScope || envelope.Ticket.SubmissionUncertain != ticket.SubmissionUncertain
            || envelope.Ticket.LastSubmissionAtUtc != ticket.LastSubmissionAtUtc || envelope.Ticket.LastSubmissionAttempt != ticket.LastSubmissionAttempt
            || envelope.Ticket.FirstNegativeAtUtc != ticket.FirstNegativeAtUtc || envelope.Ticket.IntentSchemaVersion != ticket.IntentSchemaVersion)
        {
            throw new IOException("Original feedback recovery provenance does not match its admitted receipt.");
        }

        return envelope.Payload;
    }

    internal void Complete(Guid id)
    {
        lock (_intentGate)
        {
            CompleteCore(id);
        }
    }

    internal void AbandonIntent(Guid id)
    {
        lock (_intentGate)
        {
            Ticket? ticket;
            lock (_gate)
            {
                _tickets.TryGetValue(id, out ticket);
            }

            if (ticket?.Kind == IntentKind)
            {
                CompleteCore(id);
            }
        }
    }

    private void CompleteCore(Guid id)
    {
        Ticket? ticket;
        lock (_gate)
        {
            _tickets.TryGetValue(id, out ticket);
        }

        if (ticket is null)
        {
            return;
        }

        _delete(PathFor(id));
        lock (_gate)
        {
            if (_tickets.Remove(id))
            {
                _bytes -= ticket.ReservedBytes;
            }
        }
    }

    private string PathFor(Guid id) => Path.Join(_directory, id.ToString("N") + ".json");

    private void Load()
    {
        try
        {
            LoadCore();
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("The original feedback recovery journal is inaccessible.", exception);
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
                throw new IOException("The original feedback recovery path is not a directory.");
            }

            paths = _enumerate(_directory).Take(MaximumTickets + 1).ToArray();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only initial absence is empty; a failed observed inventory must
            // remain unpublished so restoration can recover its original evidence.
            if (directoryObserved)
            {
                throw;
            }

            return;
        }

        foreach (var path in paths)
        {
            var length = new FileInfo(path).Length;
            if (_tickets.Count >= MaximumTickets || _bytes + length > _byteBudget)
            {
                throw new IOException("The existing feedback recovery journal exceeds its bounded admission limits.");
            }

            var envelope = ReadEnvelope(path);
            var reservation = ReservedLength(envelope.Ticket, envelope.Payload, length);
            if (_bytes + reservation > _byteBudget)
            {
                throw new IOException("Existing feedback intent status reservations exceed the bounded journal limit.");
            }

            var ticket = envelope.Ticket with { FileBytes = length, ReservedBytes = reservation };
            if (Path.GetFileName(path) != ticket.Id.ToString("N") + ".json" || !_tickets.TryAdd(ticket.Id, ticket))
            {
                throw new IOException("Feedback recovery journal identities are inconsistent.");
            }

            _bytes += reservation;
        }
    }

    private Envelope ReadEnvelope(string path)
    {
        try
        {
            return ReadEnvelopeCore(path);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Original feedback recovery evidence is inaccessible.", exception);
        }
    }

    private Envelope ReadEnvelopeCore(string path)
    {
        if (new FileInfo(path).Length > 2 * MaximumPayloadBytes)
        {
            throw new IOException("A feedback recovery envelope exceeds the bounded read limit.");
        }

        using var stream = OpenEnvelopeRead(path);
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(stream, JsonDefaults.Options);
        }
        catch (JsonException exception)
        {
            throw new IOException("A feedback recovery envelope is malformed.", exception);
        }

        if (envelope is null)
        {
            throw new IOException("A feedback recovery envelope is unavailable.");
        }

        if (envelope.Ticket is null || envelope.Payload is null || envelope.Payload.LongLength > MaximumPayloadBytes || envelope.Ticket.Id == Guid.Empty || envelope.Ticket.User == Guid.Empty
            || envelope.Ticket.Tmdb <= 0 || envelope.Ticket.Type is not ("movie" or "tv") || envelope.Ticket.Kind is not (RequestKind or "Dismissal" or "Impression" or IntentKind)
            || Convert.ToHexString(SHA256.HashData(envelope.Payload)) != envelope.Ticket.Hash
            || envelope.Ticket.AdmissionOrder < 0 || (envelope.Ticket.AdmissionEpoch == Guid.Empty) != (envelope.Ticket.AdmissionOrder == 0)
            || envelope.Ticket.IntentSchemaVersion is < 0 or > 1 || envelope.Ticket.LastSubmissionAttempt == Guid.Empty)
        {
            throw new IOException("Original feedback recovery evidence is invalid.");
        }

        return envelope;
    }

    internal sealed record Ticket(Guid Id, Guid User, int Tmdb, string Type, string Kind, DateTime Acquired, string Hash)
    {
        public Guid AdmissionEpoch { get; init; }

        public long AdmissionOrder { get; init; }

        public string? RequestSourceScope { get; init; }

        public bool SubmissionUncertain { get; init; }

        public int IntentSchemaVersion { get; init; }

        public DateTime? LastSubmissionAtUtc { get; init; }

        public Guid? LastSubmissionAttempt { get; init; }

        public DateTime? FirstNegativeAtUtc { get; init; }

        internal long FileBytes { get; init; }

        internal long ReservedBytes { get; init; }
    }

    private sealed record CounterState(Guid Epoch, long Order);

    private sealed record Envelope(Ticket Ticket, byte[] Payload);
}
