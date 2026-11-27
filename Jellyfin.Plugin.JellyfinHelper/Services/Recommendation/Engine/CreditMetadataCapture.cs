using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Captures already-read credit metadata without extra per-episode or network lookups.</summary>
internal static class CreditMetadataCapture
{
    /// <summary>Detaches native credits and raw countries with actual acquisition, not playback time.</summary>
    /// <param name="item">The owning allowed library item.</param>
    /// <param name="people">An already observed people list, or null after an unavailable lookup.</param>
    /// <param name="acquiredAtUtc">The actual read boundary.</param>
    /// <returns>Detached field-level provenance.</returns>
    internal static CreditMetadataSnapshot Native(BaseItem item, IReadOnlyList<PersonInfo>? people, DateTime acquiredAtUtc)
    {
        var cast = new List<PersonCredit>();
        var crew = new List<PersonCredit>();
        var complete = true;
        var fallbackOrder = 0;
        foreach (var person in people ?? [])
        {
            var credit = CaptureNativePerson(person, fallbackOrder, out var readable);
            complete &= readable;
            if (credit is null)
            {
                continue;
            }

            (credit.Kind == PersonKind.Actor ? cast : crew).Add(credit);
            if (credit.Kind != PersonKind.Writer)
            {
                fallbackOrder++;
            }
        }

        // This is an already-read native array, not a provider/IO operation.
        var countries = item.ProductionLocations?.ToImmutableArray();

        return new CreditMetadataSnapshot
        {
            SourceItemId = item.Id,
            TmdbId = int.TryParse(item.ProviderIds.GetValueOrDefault("Tmdb"), out var id) && id > 0 ? id : 0,
            MediaType = item is Series or Episode ? "tv" : "movie",
            Cast = people is null ? null : Field(cast, complete, acquiredAtUtc),
            Crew = people is null ? null : Field(crew, complete, acquiredAtUtc),
            ProductionCountries = countries,
            CountriesAcquiredAtUtc = countries.HasValue ? acquiredAtUtc : null
        };
    }

    /// <summary>Complements unavailable native credit fields from an already scoped same-title provider observation.</summary>
    /// <param name="item">The original physical library item, including independently supplied provider links.</param>
    /// <param name="native">Its detached native observation.</param>
    /// <param name="metadata">The scoped provider projection.</param>
    /// <returns>A derived same-title observation; native provider IDs and original snapshots remain unchanged.</returns>
    internal static CreditMetadataSnapshot WithAvailableTitleMetadata(BaseItem item, CreditMetadataSnapshot native, TitleMetadataSnapshot? metadata)
    {
        var identity = new TitleMetadataSnapshot
        {
            TmdbId = native.TmdbId, MediaType = native.MediaType,
            TvdbId = TitleProviderIdentity.Tvdb(item), ImdbId = TitleProviderIdentity.Imdb(item)
        };
        if (native.SourceItemId != item.Id || metadata is null || !TitleProviderIdentity.Compatible(identity, metadata) || !TitleProviderIdentity.SharesIdentity(identity, metadata))
        {
            return native;
        }

        var bound = native.TmdbId > 0 ? native : native with { TmdbId = metadata.TmdbId };
        return WithAvailableTitleMetadata(bound, metadata);
    }

    /// <summary>Complements unavailable fields after a direct typed TMDb binding was established.</summary>
    /// <param name="native">The actual physical library item's observation; known empty fields remain authoritative.</param>
    /// <param name="metadata">Current or historical provider metadata from the same validated title.</param>
    /// <returns>Detached field provenance, never a new network read or a renewed acquisition.</returns>
    internal static CreditMetadataSnapshot WithAvailableTitleMetadata(CreditMetadataSnapshot native, TitleMetadataSnapshot? metadata)
    {
        if (metadata is null || native.TmdbId <= 0 || metadata.TmdbId != native.TmdbId || metadata.MediaType != native.MediaType)
        {
            return native;
        }

        var provider = metadata.Credits;
        if (provider is not null && (provider.TmdbId != native.TmdbId || provider.MediaType != native.MediaType))
        {
            provider = null;
        }

        provider ??= new CreditMetadataSnapshot { TmdbId = metadata.TmdbId, MediaType = metadata.MediaType };
        if (metadata.ProductionCountries is { IsDefault: false } countries)
        {
            provider = provider with
            {
                ProductionCountries = countries,
                CountriesAcquiredAtUtc = metadata.FieldFetchedAtUtc?.GetValueOrDefault(nameof(TitleMetadataSnapshot.ProductionCountries), metadata.FetchedAtUtc) ?? metadata.FetchedAtUtc
            };
        }

        // Native observations stay authoritative: only unavailable fields are complemented by the
        // scoped provider observation, so a known-empty native cast/crew is never overridden.
        return native with
        {
            Cast = native.Cast ?? provider.Cast,
            Crew = native.Crew ?? provider.Crew,
            ProductionCountries = native.ProductionCountries ?? provider.ProductionCountries,
            CountriesAcquiredAtUtc = native.ProductionCountries is { IsDefault: false }
                ? native.CountriesAcquiredAtUtc
                : provider.CountriesAcquiredAtUtc
        };
    }

    /// <summary>Detaches field presence and valid provider identities from an actual detail acquisition.</summary>
    /// <param name="credits">An observed credit container.</param>
    /// <param name="acquiredAtUtc">Actual response-body acquisition, including retained cache times.</param>
    /// <param name="tmdbId">The verified owning title.</param>
    /// <param name="mediaType">The verified title namespace.</param>
    /// <returns>Unknown fields remain null, even when their container exists.</returns>
    internal static CreditMetadataSnapshot External(SeerrCredits credits, DateTime acquiredAtUtc, int tmdbId = 0, string mediaType = "movie")
    {
        var cast = credits.Cast?.OfType<SeerrCastMember>().Where(person => !string.IsNullOrWhiteSpace(person.Name))
            .OrderBy(person => person.Order ?? int.MaxValue).Select((person, index) => new PersonCredit
            {
                Name = person.Name,
                Kind = PersonKind.Actor,
                Identities = TmdbIdentity(person.Id),
                BillingWeight = EngineConstants.ComputeBillingWeight(person.Order ?? index)
            }).ToList();
        var crew = credits.Crew?.OfType<SeerrCrewMember>().Where(person => !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => (Person: person, Kind: CrewKind(person.Job))).Where(entry => entry.Kind.HasValue)
            .Select(entry => new PersonCredit
            {
                Name = entry.Person.Name,
                Kind = entry.Kind!.Value,
                Identities = TmdbIdentity(entry.Person.Id),
                BillingWeight = entry.Kind == PersonKind.Director ? 1 : 0
            }).ToList();
        return new CreditMetadataSnapshot
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Cast = cast is null ? null : Field(cast, credits.Cast!.All(person => person is not null && !string.IsNullOrWhiteSpace(person.Name)), acquiredAtUtc),
            Crew = crew is null ? null : Field(crew, credits.Crew!.All(person => person is not null && !string.IsNullOrWhiteSpace(person.Name) && !string.IsNullOrWhiteSpace(person.Job)), acquiredAtUtc)
        };
    }

    private static PersonCredit? CaptureNativePerson(PersonInfo? person, int fallbackOrder, out bool readable)
    {
        readable = person is not null;
        if (person is null || person.Type is not (PersonKind.Actor or PersonKind.Director or PersonKind.Writer))
        {
            return null;
        }

        readable = !string.IsNullOrWhiteSpace(person.Name);
        if (!readable)
        {
            return null;
        }

        return new PersonCredit
        {
            Name = person.Name,
            Kind = person.Type,
            Identities = Identities(person.ProviderIds),
            BillingWeight = person.Type == PersonKind.Writer ? 0 : EngineConstants.ComputeBillingWeight(person.SortOrder ?? fallbackOrder)
        };
    }

    private static PersonKind? CrewKind(string? job)
        => job?.Trim() switch
        {
            var name when string.Equals(name, "Director", StringComparison.OrdinalIgnoreCase) => PersonKind.Director,
            var name when string.Equals(name, "Writer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Screenplay", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Creator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Teleplay", StringComparison.OrdinalIgnoreCase) => PersonKind.Writer,
            _ => null
        };

    private static CreditField Field(List<PersonCredit> people, bool complete, DateTime acquiredAtUtc)
        => new() { People = people.ToImmutableArray(), Complete = complete, AcquiredAtUtc = acquiredAtUtc };

    private static ImmutableArray<ProviderPersonIdentity> TmdbIdentity(int id)
        => id > 0 ? [new("tmdb", id.ToString(CultureInfo.InvariantCulture))] : [];

    private static ImmutableArray<ProviderPersonIdentity> Identities(IEnumerable<KeyValuePair<string, string>>? providers)
        => (providers ?? []).Select(entry => Normalize(entry.Key, entry.Value)).OfType<ProviderPersonIdentity>()
            .Distinct().OrderBy(identity => identity.Provider, StringComparer.Ordinal).ThenBy(identity => identity.Id, StringComparer.Ordinal).ToImmutableArray();

    /// <summary>Validates a person namespace, never borrowing an item's title/provider identity.</summary>
    /// <param name="provider">The source person provider.</param>
    /// <param name="id">The source person identifier.</param>
    /// <returns>A canonical identity, or unavailable.</returns>
    internal static ProviderPersonIdentity? Normalize(string? provider, string? id)
    {
        var name = provider?.Trim().ToLowerInvariant();
        var value = id?.Trim();
        if (name is "tmdb" or "tvdb" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            return new(name, number.ToString(CultureInfo.InvariantCulture));
        }

        if (name == "imdb" && value is { Length: > 2 } && value.StartsWith("nm", StringComparison.OrdinalIgnoreCase)
            && value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return new(name, value.ToLowerInvariant());
        }

        return null;
    }
}
