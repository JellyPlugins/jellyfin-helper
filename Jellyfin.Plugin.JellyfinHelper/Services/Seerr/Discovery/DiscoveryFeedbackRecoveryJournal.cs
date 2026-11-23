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
    private const int MaximumTickets = 4096;
    private const long MaximumPayloadBytes = 16 * 1024 * 1024;
    private const long MaximumJournalBytes = 256 * 1024 * 1024;
    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Ticket> _tickets = [];
    private readonly Action<string, string> _write;
    private readonly Action<string> _delete;
    private long _bytes;
    private int _reserved;

    internal DiscoveryFeedbackRecoveryJournal(string directory, Action<string, string> write, Action<string>? delete = null)
    {
        _directory = directory;
        _write = write;
        _delete = delete ?? File.Delete;
        Load();
    }

    internal bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _reserved > 0 || _tickets.Count > 0;
            }
        }
    }

    internal Ticket[] Pending()
    {
        lock (_gate)
        {
            return _tickets.Values.OrderBy(ticket => ticket.Acquired).ToArray();
        }
    }

    internal Ticket Admit(Ticket ticket, byte[] payload)
    {
        if (payload.LongLength > MaximumPayloadBytes)
        {
            throw new IOException("Original feedback evidence exceeds the bounded recovery admission limit.");
        }

        if (ticket.Id == Guid.Empty || ticket.User == Guid.Empty || ticket.Tmdb <= 0 || ticket.Type is not ("movie" or "tv")
            || ticket.Kind is not ("Request" or "Dismissal" or "Impression") || Convert.ToHexString(SHA256.HashData(payload)) != ticket.Hash)
        {
            throw new IOException("Original feedback recovery admission has invalid provenance.");
        }

        var contents = JsonSerializer.Serialize(new Envelope(ticket, payload), JsonDefaults.Options);
        var length = Encoding.UTF8.GetByteCount(contents);
        ticket = ticket with { FileBytes = length };
        lock (_gate)
        {
            if (_tickets.Count + _reserved >= MaximumTickets || _bytes + length > MaximumJournalBytes)
            {
                throw new IOException("The bounded feedback recovery journal is full; new evidence was not admitted.");
            }

            _reserved++;
            _bytes += length;
        }

        try
        {
            // I/O never owns the metadata gate: readiness reads stay bounded even
            // while an admission or deletion is waiting on the filesystem.
            _write(PathFor(ticket.Id), contents);
            lock (_gate)
            {
                _tickets.Add(ticket.Id, ticket);
                _reserved--;
            }

            return ticket;
        }
        catch
        {
            lock (_gate)
            {
                _reserved--;
                _bytes -= length;
            }

            throw;
        }
    }

    internal byte[] Read(Ticket ticket)
    {
        var envelope = ReadEnvelope(PathFor(ticket.Id));
        if (envelope.Ticket.Id != ticket.Id || envelope.Ticket.User != ticket.User || envelope.Ticket.Tmdb != ticket.Tmdb
            || envelope.Ticket.Type != ticket.Type || envelope.Ticket.Kind != ticket.Kind || envelope.Ticket.Acquired != ticket.Acquired
            || envelope.Ticket.Hash != ticket.Hash)
        {
            throw new IOException("Original feedback recovery provenance does not match its admitted receipt.");
        }

        return envelope.Payload;
    }

    internal void Complete(Guid id)
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
                _bytes -= ticket.FileBytes;
            }
        }
    }

    private string PathFor(Guid id) => Path.Join(_directory, id.ToString("N") + ".json");

    private void Load()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(MaximumTickets + 1))
        {
            var length = new FileInfo(path).Length;
            if (_tickets.Count >= MaximumTickets || _bytes + length > MaximumJournalBytes)
            {
                throw new IOException("The existing feedback recovery journal exceeds its bounded admission limits.");
            }

            var envelope = ReadEnvelope(path);
            var ticket = envelope.Ticket with { FileBytes = length };
            if (Path.GetFileName(path) != ticket.Id.ToString("N") + ".json" || !_tickets.TryAdd(ticket.Id, ticket))
            {
                throw new IOException("Feedback recovery journal identities are inconsistent.");
            }

            _bytes += length;
        }
    }

    private static Envelope ReadEnvelope(string path)
    {
        if (new FileInfo(path).Length > 2 * MaximumPayloadBytes)
        {
            throw new IOException("A feedback recovery envelope exceeds the bounded read limit.");
        }

        using var stream = File.OpenRead(path);
        var envelope = JsonSerializer.Deserialize<Envelope>(stream, JsonDefaults.Options) ?? throw new IOException("A feedback recovery envelope is unavailable.");
        if (envelope.Ticket is null || envelope.Payload is null || envelope.Payload.LongLength > MaximumPayloadBytes || envelope.Ticket.Id == Guid.Empty || envelope.Ticket.User == Guid.Empty
            || envelope.Ticket.Tmdb <= 0 || envelope.Ticket.Type is not ("movie" or "tv") || envelope.Ticket.Kind is not ("Request" or "Dismissal" or "Impression")
            || Convert.ToHexString(SHA256.HashData(envelope.Payload)) != envelope.Ticket.Hash)
        {
            throw new IOException("Original feedback recovery evidence is invalid.");
        }

        return envelope;
    }

    internal sealed record Ticket(Guid Id, Guid User, int Tmdb, string Type, string Kind, DateTime Acquired, string Hash)
    {
        internal long FileBytes { get; init; }
    }

    private sealed record Envelope(Ticket Ticket, byte[] Payload);
}
