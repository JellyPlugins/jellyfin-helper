using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Baseline behavioral tests for BuildCandidatePeopleLookup and the weighted ComputePeopleSimilarity overload.
/// </summary>
public sealed class SimilarityComputerTests
{
    private static (SimilarityComputer Computer, Mock<ILibraryManager> Library) CreateSut()
    {
        var library = new Mock<ILibraryManager>();
        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger>();
        var computer = new SimilarityComputer(library.Object, pluginLog.Object, logger.Object);
        return (computer, library);
    }

    private static Movie NewCandidate(string name = "Movie")
        => new() { Id = Guid.NewGuid(), Name = name };

    private static PersonInfo Person(string name, PersonKind kind)
        => new() { Name = name, Type = kind };

    [Fact]
    public void BuildCandidatePeopleLookup_EmptyCandidateList_ReturnsEmptyDictionary()
    {
        var (computer, _) = CreateSut();
        var result = computer.BuildCandidatePeopleLookup([]);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_CandidateWithNoPeople_IsOmittedFromLookup()
    {
        // Contract: items with no matching people are omitted from the result dictionary
        // (matching GetPeopleNamesByItems' behavior: items with no matches are not present).
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Empty");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>());

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.False(result.ContainsKey(candidate.Id));
    }

    [Fact]
    public void BuildCandidatePeopleLookup_GetPeopleReturnsNull_ItemIsOmitted()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Null People");
        library.Setup(l => l.GetPeople(candidate)).Returns((IReadOnlyList<PersonInfo>?)null!);

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.False(result.ContainsKey(candidate.Id));
    }

    [Fact]
    public void BuildCandidatePeopleLookup_ActorsAndDirectors_AreIncluded()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Cast");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("Actor Alice", PersonKind.Actor),
            Person("Director Bob", PersonKind.Director)
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.TryGetValue(candidate.Id, out var names));
        Assert.Contains("Actor Alice", names);
        Assert.Contains("Director Bob", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_WriterProducerComposer_AreExcluded()
    {
        // The people lookup must filter to Actor+Director only. Other roles (Writer,
        // Producer, Composer, ...) add noise without predictive value and are dropped.
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Full Crew");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("Actor Alice", PersonKind.Actor),
            Person("Writer Carol", PersonKind.Writer),
            Person("Producer Dan", PersonKind.Producer),
            Person("Composer Eve", PersonKind.Composer),
            Person("Guest Star Frank", PersonKind.GuestStar)
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.TryGetValue(candidate.Id, out var names));
        Assert.Contains("Actor Alice", names);
        Assert.DoesNotContain("Writer Carol", names);
        Assert.DoesNotContain("Producer Dan", names);
        Assert.DoesNotContain("Composer Eve", names);
        Assert.DoesNotContain("Guest Star Frank", names);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_OnlyIrrelevantRoles_ItemIsOmitted()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Only Writers");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("Writer Xavier", PersonKind.Writer),
            Person("Producer Yvette", PersonKind.Producer)
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.False(result.ContainsKey(candidate.Id));
    }

    [Fact]
    public void BuildCandidatePeopleLookup_NullOrEmptyNames_AreSkipped()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Weird Names");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("", PersonKind.Actor),
            Person("   ", PersonKind.Actor),
            Person(null!, PersonKind.Director),
            Person("Actor Valid", PersonKind.Actor)
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.TryGetValue(candidate.Id, out var names));
        Assert.Single(names);
        Assert.Contains("Actor Valid", names);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_DuplicateNames_AreDeduplicated()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Duplicates");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("John Doe", PersonKind.Actor),
            Person("John Doe", PersonKind.Director),
            Person("JOHN DOE", PersonKind.Actor)
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.TryGetValue(candidate.Id, out var names));
        Assert.Single(names);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_CaseInsensitiveLookup_MatchesBothCases()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Case Test");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("Alice", PersonKind.Actor)
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.TryGetValue(candidate.Id, out var names));
        Assert.Contains("alice", names);
        Assert.Contains("ALICE", names);
        Assert.Contains("Alice", names);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_MultipleCandidates_EachHasOwnPeople()
    {
        var (computer, library) = CreateSut();
        var a = NewCandidate("A");
        var b = NewCandidate("B");
        library.Setup(l => l.GetPeople(a)).Returns(new List<PersonInfo> { Person("Alice", PersonKind.Actor) });
        library.Setup(l => l.GetPeople(b)).Returns(new List<PersonInfo> { Person("Bob", PersonKind.Director) });

        var result = computer.BuildCandidatePeopleLookup([a, b]);

        Assert.Equal(2, result.Count);
        Assert.Contains("Alice", result[a.Id]);
        Assert.Contains("Bob", result[b.Id]);
        Assert.DoesNotContain("Bob", result[a.Id]);
        Assert.DoesNotContain("Alice", result[b.Id]);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_GetPeopleThrows_CandidateIsSkippedOthersRemain()
    {
        var (computer, library) = CreateSut();
        var good = NewCandidate("Good");
        var bad = NewCandidate("Bad");
        library.Setup(l => l.GetPeople(good)).Returns(new List<PersonInfo> { Person("Alice", PersonKind.Actor) });
        library.Setup(l => l.GetPeople(bad)).Throws(new InvalidOperationException("boom"));

        var result = computer.BuildCandidatePeopleLookup([good, bad]);

        Assert.True(result.TryGetValue(good.Id, out var goodPeople));
        Assert.Contains("Alice", goodPeople);
        Assert.False(result.ContainsKey(bad.Id));
    }

    [Fact]
    public void BuildCandidatePeopleLookup_OperationCanceled_IsPropagated()
    {
        // OperationCanceledException must NOT be swallowed by the per-candidate catch.
        // It's the caller's signal (via CancellationToken) that the operation is aborted.
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Cancelled");
        library.Setup(l => l.GetPeople(candidate)).Throws(new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => computer.BuildCandidatePeopleLookup([candidate]));
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiReturnsData_UsesBatchPathAndSkipsPerItemFallback()
    {
        var (computer, library) = CreateSut();
        var a = NewCandidate("A");
        var b = NewCandidate("B");
        var batch = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [a.Id] = new List<string> { "Alice", "Bob" },
            [b.Id] = new List<string> { "Carol" }
        };
        library
            .Setup(l => l.GetPeopleNamesByItems(
                It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(a.Id) && ids.Contains(b.Id)),
                It.IsAny<IReadOnlyList<string>>()))
            .Returns(batch);

        var result = computer.BuildCandidatePeopleLookup([a, b]);

        Assert.Equal(2, result.Count);
        Assert.Contains("Alice", result[a.Id]);
        Assert.Contains("Bob", result[a.Id]);
        Assert.Contains("Carol", result[b.Id]);
        library.Verify(l => l.GetPeople(It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiPassesRelevantPersonTypes()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("A");
        IReadOnlyList<string>? capturedTypes = null;
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<Guid>, IReadOnlyList<string>>((_, types) => capturedTypes = types)
            .Returns(new Dictionary<Guid, IReadOnlyList<string>>());

        computer.BuildCandidatePeopleLookup([candidate]);

        Assert.NotNull(capturedTypes);
        Assert.Contains(nameof(PersonKind.Actor), capturedTypes!);
        Assert.Contains(nameof(PersonKind.Director), capturedTypes);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiReturnsCaseVariants_DeduplicatesCaseInsensitively()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Case");
        var batch = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [candidate.Id] = new List<string> { "Alice", "alice", "ALICE", "Bob" }
        };
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(batch);

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.TryGetValue(candidate.Id, out var people));
        Assert.Equal(2, people.Count);
        Assert.Contains("alice", people);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiReturnsEmptyNamesForItem_ItemIsOmitted()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("EmptyBatch");
        var batch = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [candidate.Id] = new List<string>()
        };
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(batch);

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.False(result.ContainsKey(candidate.Id));
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiThrows_FallsBackToPerItemPath()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Fallback");
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Throws(new InvalidOperationException("batch API not available"));
        library
            .Setup(l => l.GetPeople(candidate))
            .Returns(new List<PersonInfo> { Person("FallbackActor", PersonKind.Actor) });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.TryGetValue(candidate.Id, out var names));
        Assert.Contains("FallbackActor", names);
        library.Verify(l => l.GetPeople(candidate), Times.Once);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiCancelled_PropagatesWithoutFallback()
    {
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("CancelledBatch");
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Throws(new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => computer.BuildCandidatePeopleLookup([candidate]));
        library.Verify(l => l.GetPeople(It.IsAny<BaseItem>()), Times.Never);
    }

    // Weighted ComputePeopleSimilarity overload === These tests exercise the weighted-budget denominator: matched-weight / max(|candidate| × avg(preferredWeight), MinDenominatorFloor).

    [Fact]
    public void ComputePeopleSimilarityWeighted_EmptyCandidate_ReturnsZero()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { { "Nolan", 8.0 } };

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_EmptyWeights_ReturnsZero()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Nolan" };
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_NoOverlap_ReturnsZero()
    {
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice", "Bob" };
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Carol", 5.0 },
            { "Dave", 3.0 }
        };

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_HeavyCollaboratorMatch_OutscoresRareCameo()
    {
        // A candidate featuring the user's dominant collaborator (weight 8) must score STRICTLY higher than a candidate featuring only a rare cameo person (weight 1).
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Christopher Nolan", 8.0 },
            { "Random Cameo", 1.0 }
        };

        var candidateWithHeavyHitter = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Christopher Nolan", "Some Newcomer"
        };
        var candidateWithRareCameo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Random Cameo", "Some Newcomer"
        };

        var heavyScore = SimilarityComputer.ComputePeopleSimilarity(candidateWithHeavyHitter, weights);
        var rareScore = SimilarityComputer.ComputePeopleSimilarity(candidateWithRareCameo, weights);

        Assert.True(heavyScore > rareScore,
            $"Heavy collaborator match should outscore rare cameo (heavy={heavyScore:F4}, rare={rareScore:F4})");
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_HighMatchedShareOfBudget_HitsCeiling()
    {
        // Rich preferred profile that fully overlaps the candidate cast should hit the 1.0 ceiling. avg = 6/3 = 2.0; budget = 2 × 2.0 = 4.0; floor(5) -> denom=5.0 matched = 3 + 2 = 5.0 -> score = 5.0/5.0 = 1.0.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Alice", 3.0 },
            { "Bob", 2.0 },
            { "Carol", 1.0 }
        };
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice", "Bob" };

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        Assert.Equal(1.0, result, 10);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_NonPositiveWeights_AreIgnored()
    {
        // Weight entries with zero or negative values must be excluded from BOTH the matched weight sum AND the positive-entry count that feeds avg().
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Alice", 5.0 },
            { "Bob", 0.0 },
            { "Carol", -1.0 }
        };
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice", "Bob", "Carol" };

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        Assert.InRange(result, 0.30, 0.35);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_CaseInsensitiveKeyLookup()
    {
        // |candidate|=1, positive=1, total=4.0, avg=4.0. budget=1×4.0=4.0 < floor(5) -> denom=5.
        // matched=4 -> 4/5 = 0.8.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Tom Hanks", 4.0 }
        };
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TOM HANKS" };

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        Assert.Equal(0.8, result, 10);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_SparseProfileSingleMatch_DoesNotOvershoot()
    {
        // Sparse-user overshoot fix.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Alice", 2.0 }
        };
        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Alice", "B", "C", "D", "E", "F", "G", "H", "I", "J"
        };

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        Assert.InRange(result, 0.08, 0.12);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_RichProfileMultipleHeavyMatches_PreservesMonotonicOrdering()
    {
        // Ceiling-compression fix.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 200; i++)
        {
            weights[$"filler_{i}"] = 3.0;
        }

        weights["HeavyDirector"] = 8.0;
        weights["HeavyActor"] = 5.0;
        weights["MidActor"] = 3.0;
        weights["MinorActor"] = 2.0;
        weights["Cameo"] = 1.0;

        var castTwoHeavies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HeavyDirector", "HeavyActor",
            "u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8"
        };
        var castFiveHitters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HeavyDirector", "HeavyActor", "MidActor", "MinorActor", "Cameo",
            "u1", "u2", "u3", "u4", "u5"
        };

        var twoHeaviesScore = SimilarityComputer.ComputePeopleSimilarity(castTwoHeavies, weights);
        var fiveHittersScore = SimilarityComputer.ComputePeopleSimilarity(castFiveHitters, weights);

        // Monotone ordering: adding more matched weight must produce a strictly higher score,
        // NOT collapse to the same 1.0 ceiling as the old min-formula would have done.
        Assert.True(fiveHittersScore > twoHeaviesScore,
            $"Five-match candidate must outscore two-match candidate (5-hit={fiveHittersScore:F4}, 2-hit={twoHeaviesScore:F4})");

        // Both scores must remain within [0, 1] and neither should hit exactly 1.0 with these inputs (matched weight 13 vs 19 against the ~33.5 top-K weighted budget).
        Assert.InRange(twoHeaviesScore, 0.35, 0.50);
        Assert.InRange(fiveHittersScore, 0.55, 0.75);
    }

    [Fact]
    public void ComputePeopleSimilarityWeighted_TopKAveraging_KeepsGranularityForHeavyHitters()
    {
        // Asymmetric preferred profile: 95 one-off cameos (weight 1) and 5 heavy hitters (weight 8). With averaging over the whole set, avg ≈ 1.35, budget ≈ 13.5.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 95; i++)
        {
            weights[$"cameo_{i}"] = 1.0;
        }

        for (var i = 0; i < 5; i++)
        {
            weights[$"heavy_{i}"] = 8.0;
        }

        var candidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "heavy_0", "heavy_1", "u1", "u2", "u3", "u4", "u5", "u6", "u7", "u8"
        };

        var result = SimilarityComputer.ComputePeopleSimilarity(candidate, weights);

        // Top-20 sorted descending: 5×8 + 15×1 = 55. avg = 55/20 = 2.75. Budget = 10 × 2.75 = 27.5.
        // Matched = 8+8 = 16. Score = 16/27.5 ≈ 0.582. Two heavy matches must not clamp to 1.0.
        Assert.InRange(result, 0.5, 0.7);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_PerItemGetPeopleThrows_LogsSkipAtDebugLevel()
    {
        // With Debug logging enabled, a per-item GetPeople failure must be recorded via LogDebug (so an admin running at Debug can see which candidate was skipped) while still gracefully omitting that candidate.
        var library = new Mock<ILibraryManager>();
        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
        var computer = new SimilarityComputer(library.Object, pluginLog.Object, logger.Object);

        var candidate = NewCandidate("DebugSkip");
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Throws(new InvalidOperationException("batch unavailable"));
        library.Setup(l => l.GetPeople(candidate)).Throws(new InvalidOperationException("metadata corrupt"));

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.False(result.ContainsKey(candidate.Id));
        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void ComputeGenreSimilarity_AllWhitespaceCandidateGenres_ReturnsZero()
    {
        // A candidate whose only genre strings are blank carries no genre signal: after
        // whitespace filtering the unique-genre set is empty and the score must be exactly 0.
        var candidateGenres = new List<string> { " ", "", "\t" };
        var preferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 3.0 }
        };

        var result = SimilarityComputer.ComputeGenreSimilarity(candidateGenres, preferences, 9.0);

        Assert.Equal(0.0, result);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void ComputeGenreSimilarity_NonFiniteUserNorm_ReturnsZero(double userNormSq)
    {
        // A corrupt/degenerate precomputed user norm (Inf or NaN) must short-circuit to 0
        // rather than propagate a non-finite value into the ranking, even when a real genre matches.
        var candidateGenres = new List<string> { "Action" };
        var preferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 3.0 }
        };

        var result = SimilarityComputer.ComputeGenreSimilarity(candidateGenres, preferences, userNormSq);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeGenreSimilarity_ZeroUserNorm_ReturnsZero()
    {
        // A zero-magnitude user vector has no cosine direction: even with a positive dot product
        // the score must short-circuit to 0 instead of dividing by zero.
        var candidateGenres = new List<string> { "Action" };
        var preferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", 3.0 }
        };

        var result = SimilarityComputer.ComputeGenreSimilarity(candidateGenres, preferences, 0.0);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeAveragePreferredWeight_AllNonPositiveWeights_ReturnsZero()
    {
        // Every weight is zero or negative, so there are no positive entries to average.
        // Without a positive preference mass there is no meaningful denominator anchor.
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "A", 0.0 },
            { "B", -2.0 }
        };

        var result = SimilarityComputer.ComputeAveragePreferredWeight(weights);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeTagSimilarity_OverlappingTags_ReturnsJaccard()
    {
        // Jaccard = |A ∩ B| / |A ∪ B|. Two of the candidate's three tags match the preferred set
        // (case-insensitively), so the score is 2/3.
        var candidate = NewCandidate("Tagged");
        candidate.Tags = new[] { "Space", "Robots", "Drama" };
        var preferredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "space", "robots" };

        var result = SimilarityComputer.ComputeTagSimilarity(candidate, preferredTags);

        Assert.Equal(2.0 / 3.0, result, 10);
    }

    [Fact]
    public void ComputeTagSimilarity_DisjointTags_ReturnsZero()
    {
        // No overlap means an empty intersection, so Jaccard collapses to 0.
        var candidate = NewCandidate("Disjoint");
        candidate.Tags = new[] { "Comedy", "Musical" };
        var preferredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Horror", "Thriller" };

        var result = SimilarityComputer.ComputeTagSimilarity(candidate, preferredTags);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ComputeTagSimilarity_CaseVariantTags_MatchOrdinalIgnoreCase()
    {
        // Tag matching is OrdinalIgnoreCase: a fully case-mismatched but otherwise identical tag set
        // yields perfect overlap (Jaccard = 1).
        var candidate = NewCandidate("CaseTags");
        candidate.Tags = new[] { "SPACE", "robots" };
        var preferredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "space", "ROBOTS" };

        var result = SimilarityComputer.ComputeTagSimilarity(candidate, preferredTags);

        Assert.Equal(1.0, result, 10);
    }

    [Fact]
    public void ExtractBilledPeople_DuplicateNameHigherWeightWins_KeepsMaxWeight()
    {
        // The same actor appears twice: first low-billed (high SortOrder), then top-billed (SortOrder 0 -> weight 1.0).
        var people = new List<PersonInfo>
        {
            new() { Name = "Sigourney Weaver", Type = PersonKind.Actor, SortOrder = 9 },
            new() { Name = "Sigourney Weaver", Type = PersonKind.Actor, SortOrder = 0 }
        };

        var (names, weights) = SimilarityComputer.ExtractBilledPeople(people);

        Assert.Single(names);
        Assert.Equal("Sigourney Weaver", names[0]);
        Assert.Equal(EngineConstants.ComputeBillingWeight(0), weights[0]);
    }

    [Fact]
    public void ExtractBilledPeople_DuplicateNameLowerWeightLater_DoesNotOverwriteMax()
    {
        // When the later occurrence is LOWER billed than the first, the earlier (higher) weight
        // must be retained - proving the code keeps the max rather than overwriting with the last.
        var people = new List<PersonInfo>
        {
            new() { Name = "Ripley", Type = PersonKind.Actor, SortOrder = 0 },
            new() { Name = "Ripley", Type = PersonKind.Actor, SortOrder = 9 }
        };

        var (names, weights) = SimilarityComputer.ExtractBilledPeople(people);

        Assert.Single(names);
        Assert.Equal(EngineConstants.ComputeBillingWeight(0), weights[0]);
    }
}
