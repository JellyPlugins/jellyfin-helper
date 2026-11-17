using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>One exact, checksummed title-identity set, independent of bounded model history and training attempts.</summary>
/// <param name="path">The single scope-bound ledger path, or null for memory-only fitting.</param>
/// <param name="write">Atomic text persistence, injectable for deterministic failure tests.</param>
internal sealed class AcceptanceObservationLedger(string? path, Action<string, string> write)
{
    private const string Header = "JFH-ACCEPTANCE-1";

    /// <summary>Reads spent identities; missing required or damaged ledgers fail closed rather than recycle evidence.</summary>
    /// <param name="legacyIds">Cumulative identities from a legacy accepted bundle.</param>
    /// <param name="required">Whether a committed version already depends on this ledger.</param>
    /// <returns>Exact distinct identities, without labels, feature vectors or an arbitrary lifetime count cap.</returns>
    internal HashSet<Guid> Read(IEnumerable<Guid> legacyIds, bool required)
    {
        var ids = legacyIds.ToHashSet();
        if (path is null)
        {
            return ids;
        }

        if (!File.Exists(path))
        {
            if (required)
            {
                throw new IOException("The required acceptance ledger is missing; spent evidence cannot be recovered safely.");
            }

            return ids;
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        if (reader.ReadLine() != Header)
        {
            throw new InvalidDataException("Unsupported acceptance ledger header.");
        }

        var expectedHash = reader.ReadLine();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length != 32 || !Guid.TryParseExact(line, "N", out var id) || id == Guid.Empty)
            {
                throw new InvalidDataException("Malformed acceptance ledger identity.");
            }

            hash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
            ids.Add(id);
        }

        if (!string.Equals(expectedHash, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Acceptance ledger checksum mismatch.");
        }

        return ids;
    }

    /// <summary>Spends identities before model publication; failed publication may conservatively spend, never reuse, them.</summary>
    /// <param name="observations">The fixed evaluated block's identities.</param>
    /// <param name="legacyIds">Previously spent identities during additive migration.</param>
    /// <param name="required">Whether a previous version requires an existing ledger.</param>
    internal void Spend(IReadOnlyCollection<Guid> observations, IEnumerable<Guid> legacyIds, bool required)
    {
        if (path is null)
        {
            return;
        }

        var existed = File.Exists(path);
        var ids = Read([], required);
        var previousCount = ids.Count;
        ids.UnionWith(legacyIds);
        ids.UnionWith(observations);
        if (existed && ids.Count == previousCount)
        {
            return;
        }

        var payload = string.Concat(ids.Order().Select(id => id.ToString("N") + "\n"));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        write(path, Header + "\n" + digest + "\n" + payload);
    }
}
