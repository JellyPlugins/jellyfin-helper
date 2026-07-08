using System;
using System.Collections.Generic;
using System.Linq;
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
///     Baseline behavioral tests for <see cref="SimilarityComputer.BuildCandidatePeopleLookup"/>.
///     Locks in the observable contract of the people-lookup so that the internal fetch path
///     (per-item <c>GetPeople</c> vs. Jellyfin-12+ batch <c>GetPeopleNamesByItems</c>) can be
///     swapped without changing the assertions below.
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

    // === Empty / edge cases ===

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

    // === Type filtering ===

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

        Assert.True(result.ContainsKey(candidate.Id));
        var names = result[candidate.Id];
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

        Assert.True(result.ContainsKey(candidate.Id));
        var names = result[candidate.Id];
        Assert.Contains("Actor Alice", names);
        Assert.DoesNotContain("Writer Carol", names);
        Assert.DoesNotContain("Producer Dan", names);
        Assert.DoesNotContain("Composer Eve", names);
        Assert.DoesNotContain("Guest Star Frank", names);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_OnlyIrrelevantRoles_ItemIsOmitted()
    {
        // A candidate that has people, but none of them are Actor or Director, must not
        // appear in the lookup at all (matching the empty-people behavior).
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

    // === Name handling ===

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

        Assert.True(result.ContainsKey(candidate.Id));
        var names = result[candidate.Id];
        Assert.Single(names);
        Assert.Contains("Actor Valid", names);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_DuplicateNames_AreDeduplicated()
    {
        // HashSet<string> with OrdinalIgnoreCase must collapse duplicates.
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Duplicates");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("John Doe", PersonKind.Actor),
            Person("John Doe", PersonKind.Director),  // same name, different role
            Person("JOHN DOE", PersonKind.Actor)       // same name, different case
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.ContainsKey(candidate.Id));
        Assert.Single(result[candidate.Id]);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_CaseInsensitiveLookup_MatchesBothCases()
    {
        // Contract: the HashSet backing the name set uses OrdinalIgnoreCase.
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Case Test");
        library.Setup(l => l.GetPeople(candidate)).Returns(new List<PersonInfo>
        {
            Person("Alice", PersonKind.Actor)
        });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.ContainsKey(candidate.Id));
        var names = result[candidate.Id];
        Assert.Contains("alice", names);   // lower-case lookup must match
        Assert.Contains("ALICE", names);   // upper-case lookup must match
        Assert.Contains("Alice", names);   // canonical
    }

    // === Multi-candidate ===

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

    // === Exception handling ===

    [Fact]
    public void BuildCandidatePeopleLookup_GetPeopleThrows_CandidateIsSkippedOthersRemain()
    {
        // Per-candidate exception must not abort the whole lookup — the failing candidate
        // is silently skipped and the rest still populates the dictionary.
        var (computer, library) = CreateSut();
        var good = NewCandidate("Good");
        var bad = NewCandidate("Bad");
        library.Setup(l => l.GetPeople(good)).Returns(new List<PersonInfo> { Person("Alice", PersonKind.Actor) });
        library.Setup(l => l.GetPeople(bad)).Throws(new InvalidOperationException("boom"));

        var result = computer.BuildCandidatePeopleLookup([good, bad]);

        Assert.True(result.ContainsKey(good.Id));
        Assert.Contains("Alice", result[good.Id]);
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

    // === Batch fast-path (Jellyfin 12+ GetPeopleNamesByItems) ===
    // These tests exercise the fast path where the library manager exposes the batch API.
    // The batch-first strategy in BuildCandidatePeopleLookup should short-circuit and
    // never touch GetPeople(BaseItem) when the batch call succeeds.

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
        // Fast-path used → per-item GetPeople(BaseItem) must never have been invoked
        library.Verify(l => l.GetPeople(It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiPassesRelevantPersonTypes()
    {
        // The batch API must be called with the string names of PersonKind.Actor and .Director,
        // so that server-side type filtering yields the same set as the client-side filter
        // used in the per-item path (EngineConstants.RelevantPersonKinds).
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

        Assert.True(result.ContainsKey(candidate.Id));
        Assert.Equal(2, result[candidate.Id].Count); // Alice (dedup) + Bob
        Assert.Contains("alice", result[candidate.Id]); // case-insensitive lookup still works
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiReturnsEmptyNamesForItem_ItemIsOmitted()
    {
        // Contract: a candidate whose batch value is an empty list must be omitted from the result,
        // matching the per-item path's "no names → skip candidate" behavior.
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("EmptyBatch");
        var batch = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [candidate.Id] = new List<string>() // batch present but empty
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
        // If the batch call fails, the per-item fallback path must complete successfully
        // and produce the same lookup as the pre-Jellyfin-12 code would.
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("Fallback");
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Throws(new InvalidOperationException("batch API not available"));
        library
            .Setup(l => l.GetPeople(candidate))
            .Returns(new List<PersonInfo> { Person("FallbackActor", PersonKind.Actor) });

        var result = computer.BuildCandidatePeopleLookup([candidate]);

        Assert.True(result.ContainsKey(candidate.Id));
        Assert.Contains("FallbackActor", result[candidate.Id]);
        library.Verify(l => l.GetPeople(candidate), Times.Once);
    }

    [Fact]
    public void BuildCandidatePeopleLookup_BatchApiCancelled_PropagatesWithoutFallback()
    {
        // OperationCanceledException from the batch call must propagate to the caller
        // without triggering the per-item fallback (cancellation is a stop signal).
        var (computer, library) = CreateSut();
        var candidate = NewCandidate("CancelledBatch");
        library
            .Setup(l => l.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Throws(new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => computer.BuildCandidatePeopleLookup([candidate]));
        library.Verify(l => l.GetPeople(It.IsAny<BaseItem>()), Times.Never);
    }
}
