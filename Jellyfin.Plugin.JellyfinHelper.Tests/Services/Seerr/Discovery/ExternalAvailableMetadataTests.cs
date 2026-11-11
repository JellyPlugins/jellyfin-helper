using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>Real provider metadata survives imputation, localized collection labels and prediction snapshots.</summary>
public sealed class ExternalAvailableMetadataTests
{
    [Theory]
    [InlineData(0, 0, false, null)]
    [InlineData(1, 0, false, "reasonCollectionContinuation")]
    [InlineData(0, 1, false, "reasonFranchiseNamed")]
    [InlineData(0, 0, true, "reasonStudioNamed")]
    public void MetadataReasons_UseObservedSignalsAndActualAnchors(double progress, double franchise, bool studio, string? expected)
    {
        var candidate = new TmdbDiscoverItem { CollectionId = 123, CollectionName = "Actual saga", KnownStudios = ["Unrelated Studio", "Studio A"] };
        var context = ExternalCandidateFeatureBuilder.PrepareUserContext(History());
        var features = new CandidateFeatures { CollectionProgressionBoost = progress, FranchiseAffinity = franchise, StudioMatch = studio };
        Assert.Null(SeerrDiscoveryService.DetermineMetadataReason(features, candidate, context));
        features.MarkExternalFeatureKnown(FeatureIndex.CollectionProgressionBoost);
        features.MarkExternalFeatureKnown(FeatureIndex.FranchiseAffinity);
        features.MarkExternalFeatureKnown(FeatureIndex.StudioMatch);
        var reason = SeerrDiscoveryService.DetermineMetadataReason(features, candidate, context);
        Assert.Equal(expected, reason?.ReasonKey);
        if (franchise > 0)
        {
            Assert.Equal("Actual saga", reason?.RelatedInfo);
            candidate.CollectionName = null;
            Assert.Equal("reasonFranchise", SeerrDiscoveryService.DetermineMetadataReason(features, candidate, context)?.ReasonKey);
        }

        if (studio)
        {
            Assert.Equal("Studio A", reason?.RelatedInfo);
            candidate.KnownStudios = ["Unrelated Studio"];
            Assert.Null(SeerrDiscoveryService.DetermineMetadataReason(features, candidate, context));
        }
    }
    [Fact]
    public void CollectionIdentity_MatchesAcrossLanguagesAndSurvivesModelImputation()
    {
        var profile = History();
        var candidate = new TmdbDiscoverItem { HasCollectionMetadata = true, CollectionId = 123, CollectionName = "Different localized collection label", GenreIds = [16] };
        var features = Build(candidate, profile);
        Assert.Equal(1, features.FranchiseAffinity);
        Assert.True(features.IsExternalFeatureKnown(FeatureIndex.FranchiseAffinity));
        Assert.Equal(1, features.ToModelVector(Means())[(int)FeatureIndex.FranchiseAffinity]);
        Assert.Equal(1, features.Copy().FranchiseAffinity);
        Assert.Equal(features.KnownExternalFeatures, features.Copy().KnownExternalFeatures);
    }

    [Fact]
    public void ExternalBoxsetContinuation_IsComputedRatherThanMeanImputed()
    {
        var profile = History();
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var candidate = new TmdbDiscoverItem { CollectionId = 123, HasCollectionMetadata = true };
        var features = ExternalCandidateFeatureBuilder.Build(candidate, preferences, [], 0, PreferenceBuilder.BuildGenreExposureAnalysis(preferences, profile), profile, Means(), new Dictionary<int, int> { [123] = 3 });
        Assert.Equal(0.7, features.CollectionProgressionBoost, 12);
        Assert.Equal(0.7, features.ToModelVector(Means())[(int)FeatureIndex.CollectionProgressionBoost], 12);
    }

    [Fact]
    public void MembershipFailure_DoesNotBecomeAKnownZeroInTheModelVector()
    {
        var profile = History();
        var boxset = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        var unknown = CollectionMembershipResolver.ExternalCounts(profile.WatchedItems, [boxset], _ => throw new InvalidOperationException("offline"));
        var empty = CollectionMembershipResolver.ExternalCounts(profile.WatchedItems, [boxset], _ => []);
        var candidate = new TmdbDiscoverItem { HasCollectionMetadata = true, CollectionId = 123 };
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(preferences, profile);
        var uncertain = ExternalCandidateFeatureBuilder.Build(candidate, preferences, [], 0, exposure, profile, Means(), unknown);
        var confirmed = ExternalCandidateFeatureBuilder.Build(candidate, preferences, [], 0, exposure, profile, Means(), empty);
        Assert.False(uncertain.IsExternalFeatureKnown(FeatureIndex.CollectionProgressionBoost));
        Assert.Equal(0.37, uncertain.ToModelVector(Means())[(int)FeatureIndex.CollectionProgressionBoost]);
        Assert.True(confirmed.IsExternalFeatureKnown(FeatureIndex.CollectionProgressionBoost));
        Assert.Equal(0, confirmed.ToModelVector(Means())[(int)FeatureIndex.CollectionProgressionBoost]);
    }

    [Fact]
    public void KeywordsAndStudios_ArePersonalMetadataRatherThanGenreOrAgeGuesses()
    {
        var candidate = new TmdbDiscoverItem { KnownKeywords = ["based on manga"], KnownStudios = ["Studio A"], GenreIds = [16] };
        var features = Build(candidate, History());
        Assert.Equal(1, features.TagSimilarity);
        Assert.True(features.StudioMatch);
        var vector = features.ToModelVector(Means());
        Assert.Equal(1, vector[(int)FeatureIndex.TagSimilarity]);
        Assert.Equal(1, vector[(int)FeatureIndex.StudioMatch]);
    }

    [Fact]
    public void ExplicitlyEmptyMetadata_PreservesObservedZeroInsteadOfInventingAffinity()
    {
        var candidate = new TmdbDiscoverItem { HasCollectionMetadata = true, KnownKeywords = [], KnownStudios = [] };
        var features = Build(candidate, History());
        var vector = features.ToModelVector(Means());
        Assert.Equal(0, features.FranchiseAffinity);
        Assert.Equal(0, features.TagSimilarity);
        Assert.Equal(0, vector[(int)FeatureIndex.FranchiseAffinity]);
        Assert.Equal(0, vector[(int)FeatureIndex.TagSimilarity]);
        Assert.Equal(0, vector[(int)FeatureIndex.StudioMatch]);
    }

    [Fact]
    public void UnknownMetadata_StillUsesModelSpecificMeans()
    {
        var features = Build(new TmdbDiscoverItem(), History());
        var vector = features.ToModelVector(Means());
        Assert.Equal(0.37, features.FranchiseAffinity);
        Assert.Equal(0.37, features.TagSimilarity);
        Assert.Equal(0.37, vector[(int)FeatureIndex.StudioMatch]);
        Assert.Equal(0UL, features.KnownExternalFeatures);
    }

    [Fact]
    public void AvailableMetadataWithoutHistory_DoesNotInventPersonalMatches()
    {
        var candidate = new TmdbDiscoverItem { HasCollectionMetadata = true, CollectionId = 123, KnownKeywords = ["anime"], KnownStudios = ["Studio A"] };
        var features = Build(candidate, null);
        Assert.Equal(0, features.FranchiseAffinity);
        Assert.Equal(0, features.TagSimilarity);
        Assert.False(features.StudioMatch);
    }

    [Fact]
    public void ProviderCollectionPresence_DistinguishesMissingFromExplicitNull()
    {
        var missing = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("{}")!;
        var empty = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""{"collection":null,"keywords":[],"productionCompanies":[]}""")!;
        var known = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""{"collection":{"id":123,"name":"Collection"},"keywords":[{"id":210024,"name":"anime"}]}""")!;
        Assert.False(missing.HasCollectionMetadata);
        Assert.True(empty.HasCollectionMetadata);
        Assert.Null(empty.Collection);
        Assert.True(known.HasCollectionMetadata);
        Assert.Equal(123, known.Collection!.Id);
        Assert.Equal(210024, Assert.Single(known.Keywords!).Id);
    }

    [Fact]
    public void LegacyProfilesWithNullOwnMetadata_RemainSafeToPrepare()
    {
        var row = JsonSerializer.Deserialize<WatchedItemInfo>("""{"Played":true,"Studios":null,"Tags":null}""")!;
        Assert.Empty(row.Studios);
        Assert.Empty(row.Tags);
        var context = ExternalCandidateFeatureBuilder.PrepareUserContext(new UserWatchProfile { WatchedItems = [row] });
        Assert.Empty(context.Studios);
        Assert.Empty(context.Tags);
    }

    [Fact]
    public void KnownFeatureMask_ValidatesSlotsAndRetainsCounterfactualZeros()
    {
        var features = new CandidateFeatures { IsExternalCandidate = true, FranchiseAffinity = 1 };
        features.MarkExternalFeatureKnown(FeatureIndex.FranchiseAffinity);
        Assert.Equal(0, features.WithoutSignal(FeatureIndex.FranchiseAffinity).ToModelVector(Means())[(int)FeatureIndex.FranchiseAffinity]);
        Assert.Throws<ArgumentOutOfRangeException>(() => features.MarkExternalFeatureKnown((FeatureIndex)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => features.MarkExternalFeatureKnown((FeatureIndex)CandidateFeatures.FeatureCount));
    }

    [Fact]
    public void BoxsetContinuation_HasStrongerDefaultSignalThanFranchiseOnlyAffinity()
    {
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1);
        var boxset = new CandidateFeatures { GenreSimilarity = 1, CollectionProgressionBoost = EngineConstants.ComputeCollectionProgressionBoost(1) };
        var franchise = new CandidateFeatures { GenreSimilarity = 1, FranchiseAffinity = 1 };
        Assert.True(heuristic.Score(boxset) > heuristic.Score(franchise));
        Assert.Equal(5, LearnedScoringStrategy.CurrentWeightsVersion);
        Assert.Equal(5, NeuralScoringStrategy.CurrentWeightsVersion);
    }

    private static double[] Means() => Enumerable.Repeat(0.37, CandidateFeatures.FeatureCount).ToArray();

    private static UserWatchProfile History() => new()
    {
        WatchedItems =
        [
            new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 1, TmdbCollectionId = 123, TmdbCollectionName = "Lokale Filmreihe", Genres = ["Animation"], Tags = ["based on manga"], InheritedTags = ["folder-only tag"], Studios = ["Studio A"] }
        ]
    };

    private static CandidateFeatures Build(TmdbDiscoverItem candidate, UserWatchProfile? profile)
    {
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile ?? new UserWatchProfile());
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(preferences, profile ?? new UserWatchProfile());
        return ExternalCandidateFeatureBuilder.Build(candidate, preferences, new HashSet<string>(), 0, exposure, profile, Means());
    }
}
