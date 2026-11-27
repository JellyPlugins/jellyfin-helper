using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Shared own/parent credit resolution, typed title units and person affinities; no outcomes or network calls.</summary>
internal sealed class CreditPreferenceProfile
{
    private static readonly CreditPreferenceProfile Empty = new(new CreditIdentityIndex([]), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), false, false);
    private readonly CreditIdentityIndex _identities;
    private readonly Dictionary<string, double> _people;
    private readonly Dictionary<string, double> _writers;

    private CreditPreferenceProfile(CreditIdentityIndex identities, Dictionary<string, double> people, Dictionary<string, double> writers, bool peopleComplete, bool writersComplete)
    {
        _identities = identities;
        _people = people;
        _writers = writers;
        PeopleComplete = peopleComplete;
        WritersComplete = writersComplete;
    }

    /// <summary>Gets a value indicating whether missing personal actor/director matches can be established.</summary>
    internal bool PeopleComplete { get; }

    /// <summary>Gets a value indicating whether missing personal writer matches can be established.</summary>
    internal bool WritersComplete { get; }

    /// <summary>Builds one boundary-owned identity/preference context; repeated episodes/copies are not extra titles.</summary>
    /// <param name="profile">The current or strictly cutoff-filtered personal history.</param>
    /// <param name="lookup">Already acquired current/cached library credit snapshots.</param>
    /// <param name="referenceTimeUtc">The captured prediction clock; historical profiles retain their original cutoff.</param>
    /// <returns>Prepared typed affinity inputs independent of recommendation publication.</returns>
    internal static CreditPreferenceProfile Create(UserWatchProfile profile, IReadOnlyDictionary<Guid, CreditMetadataSnapshot>? lookup = null, DateTime? referenceTimeUtc = null)
    {
        if (!profile.WatchedItems.Any(row => HasCreditFields(row.Credits) || HasCreditFields(row.SeriesCredits))
            && !(lookup?.Values.Any(HasCreditFields) ?? false))
        {
            return Empty;
        }

        var reference = DateTimeNormalization.ToUtc(referenceTimeUtc ?? profile.CreditMetadataCutoffUtc ?? DateTime.UtcNow);
        var rows = profile.WatchedItems.Where(PreferenceBuilder.IsEligibleForPreferenceWeighting).Distinct().ToArray();
        var sources = rows.ToDictionary(row => row, row => Resolve(row, lookup, profile.CreditMetadataCutoffUtc));
        var metadata = (lookup?.Values ?? []).Select(snapshot => At(snapshot, profile.CreditMetadataCutoffUtc))
            .Concat(sources.Values).OfType<CreditMetadataSnapshot>();
        var identities = new CreditIdentityIndex(metadata.SelectMany(snapshot => snapshot.People().Concat(snapshot.People(writers: true))));
        var people = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var writers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var peopleComplete = rows.Length > 0;
        var writersComplete = rows.Length > 0;
        foreach (var title in rows.GroupBy(TitleKey, StringComparer.OrdinalIgnoreCase))
        {
            var titleSources = title.Select(row => sources[row]).OfType<CreditMetadataSnapshot>().ToArray();
            peopleComplete &= titleSources.Any(snapshot => snapshot.PeopleComplete)
                && titleSources.SelectMany(snapshot => snapshot.People()).All(person => identities.Key(person) is not null);
            writersComplete &= titleSources.Any(snapshot => snapshot.WritersComplete)
                && titleSources.SelectMany(snapshot => snapshot.People(writers: true)).All(person => identities.Key(person) is not null);
            Accumulate(people, titleSources.SelectMany(snapshot => snapshot.People()), identities, 1);
            var writerWeight = title.Select(row => PreferenceBuilder.ComputeCreditRowWeight(row, reference)).DefaultIfEmpty(0).Max();
            Accumulate(writers, titleSources.SelectMany(snapshot => snapshot.People(writers: true)), identities, writerWeight);
        }

        return new CreditPreferenceProfile(identities, people, writers, peopleComplete, writersComplete);
    }

    private static bool HasCreditFields(CreditMetadataSnapshot? snapshot) => snapshot?.Cast is not null || snapshot?.Crew is not null;

    /// <summary>Preserves the current caller's legacy name preferences without inventing past acquisitions.</summary>
    /// <param name="names">Current explicit preference input.</param>
    /// <param name="candidate">Observed candidate identities used to reject homonym ambiguity.</param>
    /// <returns>A current-only identity-safe preference context.</returns>
    internal static CreditPreferenceProfile Legacy(IReadOnlySet<string> names, CreditMetadataSnapshot candidate)
    {
        var people = names.Select(name => new PersonCredit { Name = name, Kind = Jellyfin.Data.Enums.PersonKind.Actor }).ToArray();
        var identities = new CreditIdentityIndex(people.Concat(candidate.People()).Concat(candidate.People(writers: true)));
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Accumulate(weights, people, identities, 1);
        return new CreditPreferenceProfile(identities, weights, new Dictionary<string, double>(), names.Count > 0 && people.All(person => identities.Key(person) is not null), false);
    }

    private static string TitleKey(WatchedItemInfo row)
    {
        var series = row.SeriesId.GetValueOrDefault();
        var television = series != Guid.Empty || string.Equals(row.ItemType, "Series", StringComparison.OrdinalIgnoreCase);
        var provider = series != Guid.Empty ? row.SeriesTmdbId : row.TmdbId;
        var item = series != Guid.Empty ? series : row.ItemId;
        var identity = provider > 0 ? provider.ToString(System.Globalization.CultureInfo.InvariantCulture) : item.ToString("N");
        return (television ? "tv:" : "movie:") + identity;
    }

    private static void Accumulate(Dictionary<string, double> weights, IEnumerable<PersonCredit> people, CreditIdentityIndex identities, double amount)
    {
        foreach (var key in people.Select(identities.Key).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            weights[key] = weights.GetValueOrDefault(key) + amount;
        }
    }

    private static CreditMetadataSnapshot? At(CreditMetadataSnapshot? snapshot, DateTime? cutoff)
        => cutoff.HasValue ? snapshot?.At(cutoff.Value) : snapshot;

    /// <summary>Resolves own/parent fields independently; current known empty clears, unavailable retains captured evidence.</summary>
    /// <param name="row">One allowed detached history record.</param>
    /// <param name="lookup">Known metadata at this boundary, not other users' outcomes.</param>
    /// <param name="cutoff">Optional prediction cutoff; undated acquisitions cannot establish history.</param>
    /// <returns>Available title credit metadata, without retimestamping anything.</returns>
    internal static CreditMetadataSnapshot? Resolve(WatchedItemInfo row, IReadOnlyDictionary<Guid, CreditMetadataSnapshot>? lookup = null, DateTime? cutoff = null)
    {
        var own = ResolveOne(row.ItemId, row.Credits, lookup, cutoff);
        var parent = row.SeriesId is { } id ? ResolveOne(id, row.SeriesCredits, lookup, cutoff) : null;
        if (parent is null)
        {
            return own;
        }

        var countrySource = own?.ProductionCountries is { IsDefault: false, Length: > 0 } ? own : parent;
        return parent with
        {
            Cast = Combine(own?.Cast, parent.Cast),
            Crew = Combine(own?.Crew, parent.Crew),
            ProductionCountries = countrySource.ProductionCountries,
            CountriesAcquiredAtUtc = countrySource.CountriesAcquiredAtUtc
        };
    }

    private static CreditField? Combine(CreditField? own, CreditField? parent)
    {
        if (own?.People is not { IsDefault: false } first)
        {
            return parent;
        }

        if (parent?.People is not { IsDefault: false } second)
        {
            return own;
        }

        return new CreditField
        {
            People = [.. first, .. second],
            Complete = own.IsComplete && parent.IsComplete,
            AcquiredAtUtc = own.AcquiredAtUtc.HasValue && parent.AcquiredAtUtc.HasValue
                ? new[] { own.AcquiredAtUtc.Value, parent.AcquiredAtUtc.Value }.Max()
                : null
        };
    }

    private static CreditMetadataSnapshot? ResolveOne(Guid id, CreditMetadataSnapshot? captured, IReadOnlyDictionary<Guid, CreditMetadataSnapshot>? lookup, DateTime? cutoff)
    {
        var stored = captured?.SourceItemId == id || captured?.SourceItemId == Guid.Empty ? At(captured, cutoff) : null;
        var current = lookup?.GetValueOrDefault(id);
        if (current is not null && current.SourceItemId != Guid.Empty && current.SourceItemId != id)
        {
            current = null;
        }

        return At(current, cutoff)?.WithFallback(stored) ?? stored;
    }

    /// <summary>Gets canonical person keys for neighbor affinity; ambiguous legacy names supply no identity.</summary>
    /// <param name="snapshot">Available observed credit fields.</param>
    /// <param name="writers">Whether to select writers rather than actors/directors.</param>
    /// <returns>A case-insensitive set of typed match keys.</returns>
    internal HashSet<string> Keys(CreditMetadataSnapshot? snapshot, bool writers = false)
        => new(snapshot?.People(writers).Select(_identities.Key).OfType<string>() ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>Computes existing weighted-budget people affinity using safe identities.</summary>
    /// <param name="snapshot">Candidate credits from this boundary.</param>
    /// <returns>The raw score and whether zero/nonzero is genuine evidence.</returns>
    internal (double Score, bool Known) PeopleAffinity(CreditMetadataSnapshot? snapshot)
    {
        var score = SimilarityComputer.ComputePeopleSimilarity(Keys(snapshot), _people);
        return (score, score > 0 || (Complete(snapshot, writers: false) && PeopleComplete));
    }

    /// <summary>Computes writer affinity separately from the deliberate actor/director union.</summary>
    /// <param name="snapshot">Candidate credits from this boundary.</param>
    /// <returns>The raw score and observation availability.</returns>
    internal (double Score, bool Known) WriterAffinity(CreditMetadataSnapshot? snapshot)
    {
        var score = SimilarityComputer.ComputePeopleSimilarity(Keys(snapshot, writers: true), _writers);
        return (score, score > 0 || (Complete(snapshot, writers: true) && WritersComplete));
    }

    /// <summary>Computes bounded billing; raw personal occurrence magnitude cannot exceed matched candidate coverage.</summary>
    /// <param name="snapshot">Candidate credits from this boundary.</param>
    /// <returns>The score and metadata availability.</returns>
    internal (double Score, bool Known) BillingAffinity(CreditMetadataSnapshot? snapshot)
    {
        var billing = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in snapshot?.People() ?? [])
        {
            var key = _identities.Key(person);
            if (key is not null && double.IsFinite(person.BillingWeight) && person.BillingWeight > 0)
            {
                billing[key] = Math.Max(billing.GetValueOrDefault(key), person.BillingWeight);
            }
        }

        var score = SimilarityComputer.ComputeBillingWeightedPeople(billing, _people);
        return (score, score > 0 || (Complete(snapshot, writers: false) && PeopleComplete));
    }

    private bool Complete(CreditMetadataSnapshot? snapshot, bool writers)
        => (writers ? snapshot?.WritersComplete == true : snapshot?.PeopleComplete == true)
            && snapshot.People(writers).All(person => _identities.Key(person) is not null);

    /// <summary>Names only real identity matches, using the candidate's display spelling for reasons.</summary>
    /// <param name="snapshot">The actual candidate credits.</param>
    /// <returns>Display-name weights that cannot bridge a known homonym conflict.</returns>
    internal Dictionary<string, double> MatchedDisplayWeights(CreditMetadataSnapshot? snapshot)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in snapshot?.People() ?? [])
        {
            var key = _identities.Key(person);
            if (key is not null && _people.TryGetValue(key, out var weight))
            {
                result[person.Name] = Math.Max(result.GetValueOrDefault(person.Name), weight);
            }
        }

        return result;
    }

    /// <summary>Sets only the owned credit coordinates and explicit availability; saved vectors are never mutated.</summary>
    /// <param name="features">New prediction/organic features only.</param>
    /// <param name="snapshot">Candidate metadata permitted at its prediction boundary.</param>
    internal void Apply(CandidateFeatures features, CreditMetadataSnapshot? snapshot)
    {
        var people = PeopleAffinity(snapshot);
        var writers = WriterAffinity(snapshot);
        var billing = BillingAffinity(snapshot);
        features.PeopleSimilarity = people.Score;
        features.HasPeopleMetadata = people.Known;
        features.WriterAffinity = writers.Score;
        features.HasWriterMetadata = writers.Known;
        features.BillingWeightedPeople = billing.Score;
        features.HasBillingMetadata = billing.Known;
        features.Credits = snapshot;
        if (features.IsExternalCandidate)
        {
            if (people.Known)
            {
                features.MarkExternalFeatureKnown(FeatureIndex.PeopleSimilarity);
            }
            else
            {
                features.KnownExternalFeatures &= ~(1UL << (int)FeatureIndex.PeopleSimilarity);
            }

            if (writers.Known)
            {
                features.MarkExternalFeatureKnown(FeatureIndex.WriterAffinity);
            }
            else
            {
                features.KnownExternalFeatures &= ~(1UL << (int)FeatureIndex.WriterAffinity);
            }

            if (billing.Known)
            {
                features.MarkExternalFeatureKnown(FeatureIndex.BillingWeightedPeople);
            }
            else
            {
                features.KnownExternalFeatures &= ~(1UL << (int)FeatureIndex.BillingWeightedPeople);
            }
        }
    }
}
