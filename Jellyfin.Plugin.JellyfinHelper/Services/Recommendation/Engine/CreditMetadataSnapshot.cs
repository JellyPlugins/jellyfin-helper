using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Detached own/parent credits and raw production countries with independent, truthful field acquisition.</summary>
public sealed record CreditMetadataSnapshot
{
    /// <summary>Gets the native item owning this observation, when observed through Jellyfin.</summary>
    public Guid SourceItemId { get; init; }

    /// <summary>Gets the optional provider title identity binding an external observation.</summary>
    public int TmdbId { get; init; }

    /// <summary>Gets the title namespace, never a person identity namespace.</summary>
    public string MediaType { get; init; } = "movie";

    /// <summary>Gets observed cast, or null when that field was unavailable.</summary>
    public CreditField? Cast { get; init; }

    /// <summary>Gets observed relevant crew, or null when that field was unavailable.</summary>
    public CreditField? Crew { get; init; }

    /// <summary>Gets raw production countries; origin/language/location guesses are not substituted.</summary>
    public ImmutableArray<string>? ProductionCountries { get; init; }

    /// <summary>Gets the actual country-field acquisition time.</summary>
    public DateTime? CountriesAcquiredAtUtc { get; init; }

    /// <summary>Gets a value indicating whether actor/director nonmatches can be established.</summary>
    internal bool PeopleComplete => Cast?.IsComplete == true && Crew?.IsComplete == true;

    /// <summary>Gets a value indicating whether writer nonmatches can be established.</summary>
    internal bool WritersComplete => Crew?.IsComplete == true;

    /// <summary>Enumerates valid observed people while retaining unknown/partial completeness separately.</summary>
    /// <param name="writers">True selects writers; false selects actors/directors.</param>
    /// <returns>Original immutable credits, with no display-name identity inference.</returns>
    internal IEnumerable<PersonCredit> People(bool writers = false)
        => Entries(Cast).Concat(Entries(Crew)).Where(person => !string.IsNullOrWhiteSpace(person.Name)
            && (writers ? person.Kind == PersonKind.Writer : person.Kind is PersonKind.Actor or PersonKind.Director));

    private static IEnumerable<PersonCredit> Entries(CreditField? field)
        => field?.People is { IsDefault: false } entries ? entries.OfType<PersonCredit>() : [];

    /// <summary>Preserves unobserved fields, including their original acquisition times; explicit empty lists replace.</summary>
    /// <param name="previous">An older detached observation of this same title.</param>
    /// <returns>A detached projection; neither input is changed.</returns>
    internal CreditMetadataSnapshot WithFallback(CreditMetadataSnapshot? previous)
        => this with
        {
            Cast = Latest(Cast, previous?.Cast),
            Crew = Latest(Crew, previous?.Crew),
            ProductionCountries = ProductionCountries ?? previous?.ProductionCountries,
            CountriesAcquiredAtUtc = ProductionCountries.HasValue ? CountriesAcquiredAtUtc : previous?.CountriesAcquiredAtUtc
        };

    private static CreditField? Latest(CreditField? current, CreditField? previous)
        => current is null || (previous?.AcquiredAtUtc is { } old && (current.AcquiredAtUtc is not { } newer || old > newer)) ? previous : current;

    /// <summary>Filters each field independently; future or undated metadata remains unavailable.</summary>
    /// <param name="cutoff">The normalized prediction cutoff.</param>
    /// <returns>A detached historical metadata view.</returns>
    internal CreditMetadataSnapshot At(DateTime cutoff)
        => this with
        {
            Cast = Cast?.At(cutoff),
            Crew = Crew?.At(cutoff),
            ProductionCountries = CountriesAcquiredAtUtc is { } acquired && DateTimeNormalization.ToUtc(acquired) <= cutoff ? ProductionCountries : null,
            CountriesAcquiredAtUtc = CountriesAcquiredAtUtc is { } time && DateTimeNormalization.ToUtc(time) <= cutoff ? CountriesAcquiredAtUtc : null
        };
}
