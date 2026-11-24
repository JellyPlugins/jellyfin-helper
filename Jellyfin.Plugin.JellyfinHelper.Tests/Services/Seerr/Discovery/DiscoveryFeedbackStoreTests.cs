using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for DiscoveryFeedbackStore composite key (TmdbId, MediaType) dedup logic. Verifies that Movie and TV items with the same TMDb ID are tracked independently.
/// </summary>
public class DiscoveryFeedbackStoreTests : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions CompactJsonOptions = new(Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options) { WriteIndented = false };
    private readonly string _tempDir;
    private readonly List<DiscoveryFeedbackStore> _ownedStores = [];

    public DiscoveryFeedbackStoreTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyANewerCardBackedRequestCanSupersedeTheFirstExactDismissalPrediction(bool cardBackedRequest)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var firstAt = clock.Now;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) => { if (fail) throw new IOException("offline"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var user = Guid.NewGuid();
        var first = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "first card", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        var later = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "later impression", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } };
        store.RecordGenerated(user, "viewer", [first], firstAt.AddMinutes(-1));
        fail = true;
        Assert.Throws<IOException>(() => store.RecordDismissed(user, first, firstAt.AddMinutes(-1)));
        clock.Now = firstAt.AddMinutes(1);
        fail = false;
        store.RecordImpressions(user, [later], clock.Now.AddSeconds(-1));
        clock.Now = firstAt.AddMinutes(2);
        var requestedAt = clock.Now;
        var requestedCard = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "requested card", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } };
        if (cardBackedRequest) store.RecordRequested(user, requestedCard, requestedAt.AddSeconds(-1));
        else store.RecordRequested(user, 700, "tv");
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(requestedAt, saved.RequestedAtUtc);
        Assert.Equal(firstAt, saved.DismissedAtUtc);
        Assert.Equal(cardBackedRequest ? .9 : .2, saved.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(cardBackedRequest ? "requested card" : "first card", saved.Title);
        Assert.Equal(cardBackedRequest ? requestedAt : firstAt, saved.PredictionActionAtUtc);
        Assert.Equal(saved.PredictionActionAtUtc, Assert.Single(CreateStore().LoadForUser(user)!.Entries).PredictionActionAtUtc);
        Assert.Equal(.2, first.ScoringFeatures!.GenreSimilarity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedDismissalsKeepTheFirstExactInputsUnlessANewerCardRequestOwnsThem(bool requested)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var firstAt = clock.Now;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) => { if (fail) throw new IOException("offline"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var user = Guid.NewGuid();
        var first = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "first dismissal", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        Assert.Throws<IOException>(() => store.RecordDismissed(user, first, firstAt.AddMinutes(-1)));
        clock.Now = firstAt.AddMinutes(1);
        fail = false;
        store.RecordDismissed(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "second dismissal", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } }, clock.Now.AddSeconds(-1));
        if (requested)
        {
            clock.Now = firstAt.AddMinutes(2);
            store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "requested", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, clock.Now.AddSeconds(-1));
        }

        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(firstAt.AddMinutes(1), entry.DismissedAtUtc);
        Assert.Equal(requested ? .9 : .2, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(requested ? "requested" : "first dismissal", entry.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADelayedIdentityRequestKeepsItsFirstTimeUnlessACardBackedRequestOwnsTheNewerTime(bool card)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var firstAt = clock.Now;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) => { if (fail) throw new IOException("offline"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var user = Guid.NewGuid();
        Assert.False(await store.RecordRequestedAsync(user, 700, "tv", TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        clock.Now = firstAt.AddMinutes(1);
        fail = false;
        store.RecordRequested(user, 700, "tv");
        if (card) store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, clock.Now.AddSeconds(-1));
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(card ? firstAt.AddMinutes(1) : firstAt, entry.RequestedAtUtc);
        Assert.Equal(card ? .9 : (double?)null, entry.ScoringFeatures?.GenreSimilarity);
    }

    [Theory]
    [InlineData(false, "known")]
    [InlineData(true, "known")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    public async Task SaturatedImpressionsRecoverOnlyTheOriginalPayloadOrItsExplicitExactReplay(bool replayOnly, string credits)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var firstAt = clock.Now;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry, ReplayPayloadBudget = replayOnly ? 1 : 64 * 1024 * 1024,
            WriteFeedback = (path, contents) => { if (fail) throw new IOException("offline"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var user = Guid.NewGuid();
        var first = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "original", KnownPeople = credits == "unknown" ? null : credits == "empty" ? [] : ["first actor", "first director"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        Assert.DoesNotContain("KnownPeople", System.Text.Json.JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
        var generated = firstAt.AddMinutes(-1);
        store.RecordGenerated(user, "viewer", [first], generated);
        fail = true;
        foreach (var id in Enumerable.Range(1000, 64))
        {
            Assert.False(await store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Throws<IOException>(() => store.RecordImpressions(user, [first], generated));
        Assert.InRange(RetainedReplayBytes(store), 0, store.ReplayPayloadBudget);
        Assert.Equal(!replayOnly, RetainedReplayBytes(store) > 0);
        clock.Now = firstAt.AddMinutes(1);
        fail = false;
        var later = first.Clone();
        later.KnownPeople = ["different later credits"];
        store.RecordImpressions(user, [later], generated);
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries, row => row.TmdbId == 700);
        Assert.Equal(firstAt, entry.ImpressionAtUtc);
        Assert.Equal(generated, entry.ShownAtUtc);
        Assert.Equal("original", entry.Title);
        Assert.Equal(first.KnownPeople, entry.KnownPeople);
        Assert.Equal(.2, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(0, RetainedReplayBytes(store));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedCardActionsReplayTheFirstDismissalButTheNewestExplicitRequest(bool requested)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var firstAt = clock.Now;
        var fail = true;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) => { if (fail) throw new IOException("offline"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var user = Guid.NewGuid();
        foreach (var id in Enumerable.Range(1000, 64))
        {
            Assert.False(await store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        void Record(double feature)
        {
            var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = feature } };
            if (requested) store.RecordRequested(user, card, clock.Now.AddSeconds(-1));
            else Assert.Throws<IOException>(() => store.RecordDismissed(user, card, clock.Now.AddSeconds(-1)));
        }

        Record(.2);
        var originalBytes = RetainedReplayBytes(store);
        Assert.InRange(originalBytes, 1, store.ReplayPayloadBudget);
        clock.Now = firstAt.AddMinutes(1);
        Record(.7);
        Assert.Equal(originalBytes, RetainedReplayBytes(store));
        Assert.Throws<IOException>(() => store.LoadAll());
        fail = false;
        // An unrelated success cannot certify either rejected card payload.
        store.RecordImpressions(user, [new DiscoveryRecommendation { TmdbId = 701, MediaType = "tv" }], firstAt);
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries, row => row.TmdbId == 700);
        Assert.Equal(requested ? .7 : .2, saved.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(requested ? clock.Now : firstAt, saved.PredictionEvidenceAtUtc);
        Assert.Equal(requested ? clock.Now : (DateTime?)null, saved.CardRequestedAtUtc);
        Assert.Equal(requested ? (DateTime?)null : clock.Now, saved.DismissedAtUtc);
        Assert.Equal(0, RetainedReplayBytes(store));
    }

    private static long RetainedReplayBytes(DiscoveryFeedbackStore store)
        => (long)typeof(DiscoveryFeedbackStore).GetField("_replayPayloadBytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;

    [Fact]
    public async Task PartialImpressionAdmissionReleasesAlreadyDurableOriginalsForManagedRecovery()
    {
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var admissions = 0;
        var offline = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = retry,
            WriteRecovery = (path, contents) => { if (++admissions == 2) throw new IOException("second admission unavailable"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); },
            WriteFeedback = (path, contents) =>
            {
                if (offline) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        Assert.Throws<IOException>(() => store.RecordImpressions(user, [new() { TmdbId = 700, MediaType = "tv" }, new() { TmdbId = 701, MediaType = "movie" }], DateTime.UtcNow.AddMinutes(-1)));
        await retry.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => store.LoadAll());
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        offline = false;
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(700, Assert.Single(Assert.Single(store.LoadAll()).Entries).TmdbId);
        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryDeletionFailureRetainsBackoffUntilCompletionOnBothWritePaths(bool managed)
    {
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var failDelete = true;
        var deletions = 0;
        var user = Guid.NewGuid();
        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .3 } };
        if (managed)
        {
            using var initial = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { RetryTimeProvider = retry, WriteFeedback = (_, _) => throw new IOException("offline") };
            Assert.Throws<IOException>(() => initial.RecordImpressions(user, [card], DateTime.UtcNow.AddMinutes(-1)));
        }

        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = retry,
            DeleteRecovery = path => { deletions++; if (failDelete) throw new IOException("completion unavailable"); File.Delete(path); }
        };
        if (managed)
        {
            Assert.Throws<IOException>(() => store.LoadAll());
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        else
        {
            Assert.Throws<IOException>(() => store.RecordImpressions(user, [card], DateTime.UtcNow.AddMinutes(-1)));
        }
        await retry.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, deletions);
        Assert.Single(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
        Assert.Throws<IOException>(() => store.LoadAll());
        Assert.Equal(1, deletions);
        failDelete = false;
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, deletions);
        Assert.Equal(.3, Assert.Single(Assert.Single(store.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedWatchCannotCompleteANewerCardRequest(bool superseded)
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv" }, clock.Now.AddMinutes(-1));
        clock.Now = clock.Now.AddMinutes(1);
        var watchAt = clock.Now;
        fail = true;
        Assert.False(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (700, "tv") }, TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        fail = false;
        if (superseded)
        {
            clock.Now = clock.Now.AddMinutes(1);
            store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv" }, clock.Now.AddSeconds(-1));
        }

        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(!superseded, entry.WasWatched);
        Assert.Equal(superseded ? (DateTime?)null : watchAt, entry.WatchedAtUtc);
    }

    [Fact]
    public async Task ConcurrentIdenticalImpressionCannotRetireTheEarlierTimestampBeforeRecoverySavesIt()
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var firstAt = clock.Now;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var firstAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        using var recoveryRelease = new ManualResetEventSlim();
        var admissions = 0;
        var writes = 0;
        var offline = true;
        var synchronized = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteRecovery = (path, contents) =>
            {
                if (Interlocked.Increment(ref admissions) == 1) { firstAdmission.TrySetResult(); firstRelease.Wait(TestContext.Current.CancellationToken); }
                else { secondAdmission.TrySetResult(); secondRelease.Wait(TestContext.Current.CancellationToken); }
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            },
            WriteFeedback = (path, contents) =>
            {
                if (synchronized && Interlocked.Increment(ref writes) == 3) { recoveryEntered.TrySetResult(); recoveryRelease.Wait(TestContext.Current.CancellationToken); }
                if (offline) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        foreach (var id in Enumerable.Range(1000, 64))
        {
            Assert.False(await store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        var generated = firstAt.AddMinutes(-1);
        synchronized = true;
        var first = Task.Run(() => Assert.Throws<IOException>(() => store.RecordImpressions(user, [card], generated)), CancellationToken.None);
        Task? second = null;
        try
        {
            await firstAdmission.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            clock.Now = firstAt.AddMinutes(1);
            second = Task.Run(() => store.RecordImpressions(user, [card], generated), CancellationToken.None);
            await secondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            firstRelease.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            offline = false;
            secondRelease.Set();
            await second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            retry.ExpireDeadlines();
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(clock.Now, Assert.Single(store.LoadForUser(user)!.Entries, row => row.TmdbId == 700).ImpressionAtUtc);
            Assert.True(RetainedReplayBytes(store) > 0);
            Assert.Throws<IOException>(() => store.LoadAll());
        }
        finally
        {
            firstRelease.Set(); secondRelease.Set(); recoveryRelease.Set();
            await Task.WhenAll(second is null ? [first] : [first, second]).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(firstAt, Assert.Single(Assert.Single(store.LoadAll()).Entries, row => row.TmdbId == 700).ImpressionAtUtc);
        Assert.Equal(0, RetainedReplayBytes(store));
    }

    [Fact]
    public void NumericSemanticsConfirmedAvailabilitySurvivesRestartWithoutLaterGenerationBackfill()
    {
        var user = Guid.NewGuid();
        var generated = DateTime.UtcNow.AddDays(-2);
        var features = new CandidateFeatures { IsExternalCandidate = true, HasRatingMetadata = false, HasPopularityMetadata = false, CombinedCriticScore = .65, PopularityScore = .2, GenreSimilarity = .8, RecencyScore = .7 };
        var raw = features.ToVector();
        var card = new DiscoveryRecommendation { TmdbId = 770, Title = "captured original", HasRatingMetadata = false, HasPopularityMetadata = false, ScoringFeatures = features };
        using var store = CreateStore();
        store.RecordRequested(user, card, generated);
        store.RecordGenerated(user, "viewer", [new() { TmdbId = 770, TmdbRating = 9, Popularity = 100, HasRatingMetadata = true, HasPopularityMetadata = true }]);
        using var restarted = CreateStore();
        var entry = Assert.Single(restarted.LoadForUser(user)!.Entries);
        Assert.False(entry.HasRatingMetadata);
        Assert.False(entry.HasPopularityMetadata);
        Assert.Equal(0, entry.TmdbRating);
        Assert.Equal(0, entry.Popularity);
        Assert.Equal(generated, entry.ShownAtUtc);
        Assert.Equal(raw, entry.ScoringFeatures!.ToVector());
        Assert.Equal(raw, features.ToVector());
        var means = Enumerable.Repeat(.37, 41).ToArray();
        means[2] = .25;
        means[16] = .75;
        Assert.Equal(.25, entry.ScoringFeatures.ToModelVector(means)[2]);
        Assert.Equal(.75, entry.ScoringFeatures.ToModelVector(means)[16]);
    }

    [Fact]
    public void NumericSemanticsUnseenGenerationCanAcquireKnownZeroButCannotReplaceItAsMissing()
    {
        var user = Guid.NewGuid();
        var generated = DateTime.UtcNow.AddMinutes(-2);
        using var store = CreateStore();
        store.RecordGenerated(user, "viewer", [new() { TmdbId = 771, HasRatingMetadata = false, HasPopularityMetadata = false }], generated);
        store.RecordGenerated(user, "viewer", [new() { TmdbId = 771, HasRatingMetadata = true, HasPopularityMetadata = true }], generated.AddMinutes(1));
        var zero = Assert.Single(store.LoadForUser(user)!.Entries);
        Assert.True(zero.HasRatingMetadata);
        Assert.True(zero.HasPopularityMetadata);
        store.RecordGenerated(user, "viewer", [new() { TmdbId = 771, TmdbRating = 8, Popularity = 100, HasRatingMetadata = true, HasPopularityMetadata = true }], generated.AddMinutes(2));
        var kept = Assert.Single(store.LoadForUser(user)!.Entries);
        Assert.Equal(0, kept.TmdbRating);
        Assert.Equal(0, kept.Popularity);
        Assert.Null(kept.ScoringFeatures);
    }

    private static DiscoveryFeedbackRecoveryJournal.Ticket JournalTicket(byte[] bytes)
        => new(Guid.NewGuid(), Guid.NewGuid(), 700, "tv", "Impression", DateTime.UtcNow, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

    [Theory]
    [InlineData("ticket")]
    [InlineData("user")]
    [InlineData("title")]
    [InlineData("type")]
    [InlineData("kind")]
    [InlineData("hash")]
    public void RecoveryJournalRefusesInvalidAdmissionBeforeWritingAnyOriginal(string invalid)
    {
        byte[] bytes = [1];
        var ticket = JournalTicket(bytes);
        ticket = invalid switch
        {
            "ticket" => ticket with { Id = Guid.Empty },
            "user" => ticket with { User = Guid.Empty },
            "title" => ticket with { Tmdb = 0 },
            "type" => ticket with { Type = "episode" },
            "kind" => ticket with { Kind = "Watched" },
            _ => ticket with { Hash = "invalid" }
        };
        var writes = 0;
        var journal = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "invalid-admission"), (_, _) => writes++);
        Assert.Throws<IOException>(() => journal.Admit(ticket, bytes));
        Assert.Equal(0, writes);
        Assert.False(journal.HasPending);
        Assert.Empty(journal.Pending());
    }

    [Fact]
    public void ADisposedFeedbackStoreCannotStartRecoveryForAnOtherwiseEmptyTrainingRead()
    {
        using var store = CreateStore();
        Assert.Empty(store.LoadAll());
        store.Dispose();
        Assert.Empty(store.LoadAll());
        Assert.Null(typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
    }

    [Fact]
    public void RecoveryJournalEnforces4096TicketsAndReleasesCapacityOnlyAfterCompletion()
    {
        var writes = 0;
        var deletes = 0;
        var journal = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "count-boundary"), (_, _) => writes++, _ => deletes++);
        byte[] bytes = [1];
        var tickets = Enumerable.Range(0, 4096).Select(_ => journal.Admit(JournalTicket(bytes), bytes)).ToArray();
        Assert.Equal(4096, writes);
        Assert.Throws<IOException>(() => journal.Admit(JournalTicket(bytes), bytes));
        Assert.Equal(4096, writes);
        journal.Complete(Guid.NewGuid());
        Assert.Equal(0, deletes);
        journal.Complete(tickets[0].Id);
        journal.Complete(tickets[0].Id);
        Assert.Equal(1, deletes);
        journal.Admit(JournalTicket(bytes), bytes);
        Assert.Equal(4097, writes);
        Assert.Equal(4096, journal.Pending().Length);
    }

    [Fact]
    public void RecoveryJournalEnforces16MiBPayloadAnd256MiBSerializedCapacityBeforeCallingTheWriter()
    {
        var envelopeCapacity = 256L * 1024 * 1024 - DiscoveryFeedbackRecoveryJournal.MetadataReservationBytes;
        var writes = 0;
        long serialized = 0;
        var journal = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "byte-boundary"), (_, contents) => { writes++; serialized += System.Text.Encoding.UTF8.GetByteCount(contents); }, _ => { });
        var bytes = new byte[16 * 1024 * 1024];
        var oversized = new byte[bytes.Length + 1];
        Assert.Throws<IOException>(() => journal.Admit(JournalTicket(oversized), oversized));
        Assert.Equal(0, writes);
        var first = journal.Admit(JournalTicket(bytes) with { Acquired = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc) }, bytes);
        Assert.Equal(16 * 1024 * 1024, bytes.Length);
        var envelopeBytes = serialized;
        while (serialized + envelopeBytes <= envelopeCapacity - 8)
        {
            journal.Admit(JournalTicket(bytes) with { Acquired = first.Acquired }, bytes);
        }

        var before = serialized;
        var count = writes;
        Assert.Throws<IOException>(() => journal.Admit(JournalTicket(bytes), bytes));
        Assert.Equal(count, writes);
        Assert.InRange(before, envelopeCapacity - envelopeBytes, envelopeCapacity);
        var remaining = envelopeCapacity - before;
        var nextOrder = journal.Pending().Max(ticket => ticket.AdmissionOrder) + 1;
        var candidates = new[]
        {
            first with { Id = Guid.NewGuid(), Kind = "Request", AdmissionOrder = nextOrder },
            first with { Id = Guid.NewGuid(), AdmissionOrder = nextOrder },
            first with { Id = Guid.NewGuid(), Acquired = first.Acquired.AddMilliseconds(100), AdmissionOrder = nextOrder },
            first with { Id = Guid.NewGuid(), Acquired = first.Acquired.AddMilliseconds(120), AdmissionOrder = nextOrder }
        };
        var exact = candidates.Select(ticket => (Ticket: ticket, Overhead: System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(new { ticket, payload = Array.Empty<byte>() }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options))))
            .First(candidate => (remaining - candidate.Overhead) % 4 == 0);
        var remainderPayload = new byte[3 * ((remaining - exact.Overhead) / 4)];
        journal.Admit(exact.Ticket with { Hash = JournalTicket(remainderPayload).Hash }, remainderPayload);
        Assert.Equal(envelopeCapacity, serialized);
        Assert.Throws<IOException>(() => journal.Admit(JournalTicket([1]), [1]));
        Assert.Equal(count + 1, writes);
        journal.Complete(journal.Pending().First(ticket => ticket.AdmissionOrder == 10).Id);
        journal.Admit(JournalTicket(bytes) with { Acquired = first.Acquired }, bytes);
        Assert.Equal(count + 2, writes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"ticket\":null,\"payload\":\"e30=\"}")]
    [InlineData("{")]
    [InlineData("[1")]
    public void RecoveryJournalRejectsMissingEnvelopeProvenance(string contents)
    {
        var directory = Path.Join(_tempDir, "invalid-envelope");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, Guid.NewGuid().ToString("N") + ".json"), contents);
        Assert.Throws<IOException>(() => new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecoveryInventoryDisappearanceCannotPublishAnEmptyStoreAndTheSameStoreRetriesRestoredOriginals(bool missingFile)
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        var generated = clock.Now.AddMinutes(-1);
        var unavailable = false;
        using (var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock,
            RetryTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                if (unavailable) throw new IOException("accepted request persistence unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        })
        {
            original.RecordImpressions(user, [new() { TmdbId = 100, Title = "old durable row", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .3 } }], generated);
            unavailable = true;
            original.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 500, Title = "accepted original", KnownPeople = ["original person"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } }, generated);
            Assert.Throws<IOException>(() => original.LoadAll());
        }

        var directory = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        var parked = Path.Join(_tempDir, "parked-recovery");
        var originalPath = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var originalContents = await File.ReadAllTextAsync(originalPath, TestContext.Current.CancellationToken);
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            EnumerateRecoveryFiles = path =>
            {
                if (!unavailable) return Directory.EnumerateFiles(path, "*.json");
                if (missingFile) throw new FileNotFoundException("inventory unavailable after the directory was observed");
                Directory.Move(path, parked);
                return Directory.EnumerateFiles(path, "*.json");
            }
        };
        try
        {
            Assert.ThrowsAny<IOException>(() => store.LoadAll());
            Assert.Null(typeof(DiscoveryFeedbackStore).GetField("_recoveryJournal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
            Assert.Null(typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
        }
        finally
        {
            if (Directory.Exists(parked)) Directory.Move(parked, directory);
        }

        Assert.Equal(originalContents, await File.ReadAllTextAsync(originalPath, TestContext.Current.CancellationToken));
        unavailable = false;
        try { store.LoadAll(); } catch (IOException) { /* The restored original owns its persistence barrier until its writer completes. */ }
        Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var rows = Assert.Single(store.LoadAll()).Entries;
        Assert.Equal(.3, Assert.Single(rows, entry => entry.TmdbId == 100).ScoringFeatures!.GenreSimilarity);
        var recovered = Assert.Single(rows, entry => entry.TmdbId == 500);
        Assert.Equal("accepted original", recovered.Title);
        Assert.Equal(.7, recovered.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(new[] { "original person" }, recovered.KnownPeople);
        Assert.Equal(generated, recovered.ShownAtUtc);
        Assert.Equal(clock.Now, recovered.RequestedAtUtc);
        Assert.Empty(store.GetPendingRequestedItems(user));
        Assert.Empty(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public void ARecoveryJournalCanBeEmptyOnFirstStartWithoutEnumeratingAnAbsentDirectory()
    {
        var directory = Path.Join(_tempDir, "absent-recovery");
        var enumerations = 0;
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }, enumerate: _ =>
        {
            enumerations++;
            throw new IOException("an absent directory must not be enumerated");
        });
        Assert.Empty(journal.Pending());
        Assert.False(journal.HasPending);
        Assert.Equal(0, enumerations);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task AnUnavailableJournalIsNotPublishedAsEmptyAndItsOriginalsRecoverOnTheSameStore()
    {
        var user = Guid.NewGuid();
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        using (var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = retry, WriteFeedback = (_, _) => throw new IOException("offline")
        })
        {
            Assert.Throws<IOException>(() => original.RecordImpressions(user, [new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }], DateTime.UtcNow.AddMinutes(-1)));
        }

        var inaccessible = true;
        var offline = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = retry,
            EnumerateRecoveryFiles = directory => inaccessible ? throw new UnauthorizedAccessException("unreadable directory") : Directory.EnumerateFiles(directory, "*.json"),
            WriteFeedback = (path, contents) =>
            {
                if (offline) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        Assert.Throws<IOException>(() => store.LoadAll());
        inaccessible = false;
        Assert.Throws<IOException>(() => store.LoadAll());
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        offline = false;
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(.2, Assert.Single(Assert.Single(store.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredImpressionDeliberatelyOmittedByRetentionDoesNotLeaveAnUnretryableFittingRejection(bool expired)
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddDays(expired ? -366 : -2) };
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        using (var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry, WriteFeedback = (_, _) => throw new IOException("offline")
        })
        {
            Assert.Throws<IOException>(() => original.RecordImpressions(user, [new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }], clock.Now.AddDays(-1)));
        }

        if (!expired)
        {
            using var seed = CreateStore();
            seed.RecordGenerated(user, "viewer", Enumerable.Range(1000, 200).Select(id => new DiscoveryRecommendation { TmdbId = id, MediaType = "movie" }).ToArray(), DateTime.UtcNow);
        }

        using var restarted = CreateStore();
        try { restarted.LoadAll(); } catch (IOException) { /* The exact original remains pending until the managed save completes. */ }
        await CurrentWriter(restarted).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var rows = restarted.LoadAll();
        Assert.Equal(expired ? 0 : 200, rows.SelectMany(result => result.Entries).Count());
        Assert.DoesNotContain(rows.SelectMany(result => result.Entries), row => row.TmdbId == 700);
        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task AJournaledRequestCanRecoverBefore64BlockedDependentWatchesWithoutExpandingTheNormalQueue(bool failsFirstReplay, bool preparedRequest, bool unrelatedImpressionFails)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var clock = new ReceiptClock { Now = DateTime.UtcNow };
        var offline = false;
        var blockImpression = unrelatedImpressionFails;
        const string blockedTitle = "blocked unrelated impression";
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WaitTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                if (offline || (blockImpression && contents.Contains(blockedTitle, StringComparison.Ordinal))) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var first = store.RecordRequestedAsync(user, 1, "movie", TestContext.Current.CancellationToken);
        Task<bool[]>? initial = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var queued = Enumerable.Range(2, 64).Select(id => store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken)).ToArray();
            initial = Task.WhenAll(queued.Append(first));
            Assert.False(await store.RecordRequestedAsync(user, 9000, "movie", TestContext.Current.CancellationToken));
            release.Set();
            Assert.All(await initial.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), saved => Assert.True(saved));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            foreach (var _ in Enumerable.Range(0, 64))
            {
                Assert.False(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (9000, "movie") }, TestContext.Current.CancellationToken));
                await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }

            if (unrelatedImpressionFails)
            {
                Assert.Throws<IOException>(() => store.RecordImpressions(user, [new() { TmdbId = 8000, MediaType = "movie", Title = blockedTitle, ScoringFeatures = new CandidateFeatures() }], clock.Now.AddMinutes(-1)));
            }

            offline = true;
            var card = new DiscoveryRecommendation { TmdbId = 9000, MediaType = "movie", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
            if (preparedRequest)
            {
                var prepared = store.PrepareRequestedCard(user, card, clock.Now.AddMinutes(-1), "validated-source");
                store.MarkPreparedSubmission(prepared);
                Assert.False(await store.ConfirmPreparedRequestAsync(user, prepared, "validated-source", TestContext.Current.CancellationToken));
            }
            else
            {
                store.RecordRequested(user, card, clock.Now.AddMinutes(-1));
            }
            await retry.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            offline = failsFirstReplay;
            retry.ExpireDeadlines();
            await CompleteFirstPriorityFailureAsync(store, retry, failsFirstReplay, () => offline = false);
            if (unrelatedImpressionFails)
            {
                Assert.False(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
                // The permanent unrelated failure must not keep the accepted
                // prepared request and its 64 watches from becoming durable.
                Assert.True(Assert.Single(store.LoadForUser(user)!.Entries, row => row.TmdbId == 9000).WasWatched);
                Assert.Throws<IOException>(() => store.LoadAll());
                blockImpression = false;
                await retry.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                retry.ExpireDeadlines();
            }

            Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries, row => row.TmdbId == 9000);
            Assert.True(entry.WasWatched);
            Assert.Equal(clock.Now, entry.RequestedAtUtc);
            Assert.Equal(clock.Now, entry.WatchedAtUtc);
            Assert.Equal(.2, entry.ScoringFeatures!.GenreSimilarity);
            Assert.Empty(store.GetPendingRequestedItems(user));
            var retained = (System.Collections.ICollection)typeof(DiscoveryFeedbackStore).GetField("_retryWrites", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            Assert.Empty(retained);
        }
        finally
        {
            release.Set();
            Task admitted = initial is null ? first : initial;
            await admitted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    private static async Task CompleteFirstPriorityFailureAsync(DiscoveryFeedbackStore store, Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider retry, bool failsFirstReplay, Action restore)
    {
        if (!failsFirstReplay)
        {
            return;
        }

        Assert.False(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await retry.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var blocked = (System.Collections.ICollection)typeof(DiscoveryFeedbackStore).GetField("_retryWrites", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        Assert.Equal(64, blocked.Count);
        Assert.NotNull(typeof(DiscoveryFeedbackStore).GetField("_retainedPriorityRecovery", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
        restore();
        retry.ExpireDeadlines();
    }

    [Theory]
    [InlineData(33)]
    [InlineData(257)]
    public void RecoveryJournalStartupRejectsOversizedEnvelopesAndAggregateBytesWithoutReadingThePayload(int mebibytes)
    {
        var directory = Path.Join(_tempDir, "startup-byte-boundary");
        Directory.CreateDirectory(directory);
        using (var stream = File.Create(Path.Join(directory, Guid.NewGuid().ToString("N") + ".json"))) stream.SetLength(mebibytes * 1024L * 1024);
        Assert.Throws<IOException>(() => new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }));
        Assert.Equal(mebibytes * 1024L * 1024, new FileInfo(Assert.Single(Directory.GetFiles(directory))).Length);
    }

    [Fact]
    public void RecoveryJournalStartupAggregateLimitRejectsIndividuallyValidOriginalsWithoutRemovingThem()
    {
        var directory = Path.Join(_tempDir, "valid-aggregate-boundary");
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents));
        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures() };
        var generatedAt = DateTime.UtcNow.AddMinutes(-1);
        var payload = new { item = card, generatedAt, knownPeople = (string[]?)null };
        var overhead = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options).Length;
        card.Title = new string('x', 16 * 1024 * 1024 - overhead);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        Assert.Equal(16 * 1024 * 1024, bytes.Length);
        foreach (var _ in Enumerable.Range(0, 11)) journal.Admit(JournalTicket(bytes), bytes);
        var restored = new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { });
        Assert.Equal(11, restored.Pending().Length);
        var isolatedDirectory = Path.Join(_tempDir, "valid-isolated-original");
        var isolated = new DiscoveryFeedbackRecoveryJournal(isolatedDirectory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents));
        var extra = isolated.Admit(JournalTicket(bytes), bytes);
        Assert.Equal(extra.Hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(isolated.Read(extra))));
        var extraFile = Assert.Single(Directory.GetFiles(isolatedDirectory, "*.json"));
        File.Copy(extraFile, Path.Join(directory, Path.GetFileName(extraFile)));
        var files = Directory.GetFiles(directory, "*.json");
        Assert.Equal(12, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 32L * 1024 * 1024));
        Assert.True(files.Sum(file => new FileInfo(file).Length) > 256L * 1024 * 1024);
        Assert.Throws<IOException>(() => new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }));
        Assert.Equal(files.Order(), Directory.GetFiles(directory, "*.json").Order());
    }

    [Fact]
    public void RecoveryJournalStartupRestores4096TicketsButRejectsA4097thWithoutDeletingOriginals()
    {
        var directory = Path.Join(_tempDir, "startup-count-boundary");
        Directory.CreateDirectory(directory);
        byte[] bytes = [1];
        string contents = string.Empty;
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (_, value) => contents = value);
        var ticket = journal.Admit(JournalTicket(bytes), bytes);
        foreach (var _ in Enumerable.Range(0, 4096))
        {
            var id = Guid.NewGuid();
            File.WriteAllText(Path.Join(directory, id.ToString("N") + ".json"), contents.Replace(ticket.Id.ToString(), id.ToString(), StringComparison.Ordinal));
        }

        var restored = new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { });
        Assert.Equal(4096, restored.Pending().Length);
        Assert.True(restored.HasPending);
        var extra = Guid.NewGuid();
        File.WriteAllText(Path.Join(directory, extra.ToString("N") + ".json"), contents.Replace(ticket.Id.ToString(), extra.ToString(), StringComparison.Ordinal));
        Assert.Throws<IOException>(() => new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }));
        Assert.Equal(4097, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public void RecoveryJournalReadRequiresTheAdmittedIdentityAndStartupRequiresTheServerFilename()
    {
        var directory = Path.Join(_tempDir, "identity-journal");
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, value) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, value));
        byte[] bytes = [1];
        var ticket = journal.Admit(JournalTicket(bytes), bytes);
        Assert.Throws<IOException>(() => journal.Read(ticket with { Tmdb = 701 }));
        var file = Assert.Single(Directory.GetFiles(directory, "*.json"));
        File.Move(file, Path.Join(directory, Guid.NewGuid().ToString("N") + ".json"));
        Assert.Throws<IOException>(() => new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }));
        Assert.Single(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public void DuplicateRecoveryIdsAreRejectedBeforeWritingAndExactImportsDoNotReplaceOrRenewAnOriginal()
    {
        var directory = Path.Join(_tempDir, "duplicate-ticket");
        var writes = 0;
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) => { writes++; Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); });
        byte[] original = [1];
        byte[] replacement = [2];
        var ticket = journal.Admit(JournalTicket(original), original);
        var different = JournalTicket(replacement) with { Id = ticket.Id };
        Assert.Throws<IOException>(() => journal.Admit(different, replacement));
        Assert.Throws<IOException>(() => journal.Import(different, replacement));
        Assert.Throws<IOException>(() => journal.HasImportCapacity(different, replacement));
        Assert.Equal(ticket, journal.Import(ticket, original));
        Assert.True(journal.HasImportCapacity(ticket, original));
        Assert.Equal(1, writes);
        Assert.Equal(original, journal.Read(ticket));
    }

    [Fact]
    public async Task AnInProgressEnvelopeOwnsItsIdEvenBeforeItHasBeenIndexed()
    {
        var directory = Path.Join(_tempDir, "reserved-ticket-id");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writes = 0;
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) =>
        {
            writes++;
            entered.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
            Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        });
        byte[] original = [1];
        byte[] replacement = [2];
        var ticket = JournalTicket(original);
        var writing = Task.Run(() => journal.Admit(ticket, original), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => journal.Admit(JournalTicket(replacement) with { Id = ticket.Id }, replacement));
            Assert.Equal(1, writes);
        }
        finally { release.Set(); await writing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        Assert.Equal(original, journal.Read(await writing));
    }

    [Fact]
    public void AnIntentReservesItsLargestOperationalStateBeforeItsSubmissionCanStart()
    {
        var directory = Path.Join(_tempDir, "intent-state-reservation");
        byte[] payload = [1];
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        var first = new DiscoveryFeedbackRecoveryJournal(directory, write);
        var original = first.Admit(JournalTicket(payload) with { Kind = "RequestIntent", RequestSourceScope = "source" }, payload);
        var limited = new DiscoveryFeedbackRecoveryJournal(directory, write, byteBudget: original.ReservedBytes + DiscoveryFeedbackRecoveryJournal.MetadataReservationBytes);
        var submittedAt = DateTime.UtcNow;
        var submitted = limited.UpdateIntent(original.Id, accepted: false, uncertain: true, submittedAt);
        var negative = limited.ObserveNegative(original.Id, submittedAt.AddMinutes(20));
        Assert.Equal(negative, limited.ObserveNegative(original.Id, submittedAt.AddMinutes(30)));
        var accepted = limited.UpdateIntent(original.Id, accepted: true, uncertain: true);
        Assert.Equal(original.Acquired, accepted.Acquired);
        Assert.Equal(original.AdmissionOrder, accepted.AdmissionOrder);
        Assert.Equal(original.ReservedBytes, accepted.ReservedBytes);
        Assert.Equal(submittedAt, submitted.LastSubmissionAtUtc);
        Assert.Equal(payload, limited.Read(accepted));
        limited.Complete(original.Id);
        Assert.Empty(limited.Pending());
    }

    [Fact]
    public void IndependentJournalsShareOneChronologicalCounterAndCopyOriginalsWithoutAllocatingAnotherOrder()
    {
        var primaryPath = Path.Join(_tempDir, "shared-counter");
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        DiscoveryFeedbackRecoveryJournal? primary = null;
        DiscoveryFeedbackRecoveryJournal? prepared = null;
        primary = new DiscoveryFeedbackRecoveryJournal(primaryPath, write) { CounterPeers = () => prepared?.Pending() ?? [] };
        prepared = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "isolated-intents"), write, counterDirectory: primaryPath) { CounterPeers = () => primary.Pending() };
        var first = primary.Admit(JournalTicket([1]), [1]);
        var second = prepared.Admit(JournalTicket([2]) with { Kind = "RequestIntent" }, [2]);
        Assert.Equal(first.AdmissionEpoch, second.AdmissionEpoch);
        Assert.True(second.AdmissionOrder > first.AdmissionOrder);
        var archive = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "isolated-archive"), write, counterDirectory: primaryPath);
        var copied = archive.Import(second, [2]);
        Assert.True(DiscoveryFeedbackRecoveryJournal.SameOriginal(second, copied));
        prepared.Complete(second.Id);
        var third = primary.Admit(JournalTicket([3]), [3]);
        Assert.Equal(second.AdmissionEpoch, third.AdmissionEpoch);
        Assert.Equal(second.AdmissionOrder + 1, third.AdmissionOrder);
        Assert.Equal(new byte[] { 2 }, archive.Read(copied));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompletedRequestKeepsItsPredictionAndWatchWhenANewerCardIsSubmitted(bool watched)
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { ReceiptTimeProvider = clock };
        var firstAt = clock.Now;
        store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, firstAt.AddMinutes(-1));
        clock.Now = firstAt.AddMinutes(1);
        if (watched) Assert.True(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (700, "tv") }, TestContext.Current.CancellationToken));
        clock.Now = firstAt.AddMinutes(2);
        store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, clock.Now.AddSeconds(-1));
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(watched ? firstAt : clock.Now, entry.RequestedAtUtc);
        Assert.Equal(watched ? .2 : .9, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(watched, entry.WasWatched);
        Assert.Equal(watched ? firstAt.AddMinutes(1) : (DateTime?)null, entry.WatchedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheOriginalRetainedCardCanRepairACompletedIdentityOnlyRequestWithoutReplacingItsWatchPair(bool reconstruction)
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var originalAt = clock.Now;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var offline = true;
        using var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) =>
            {
                if (offline) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        original.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, originalAt.AddMinutes(-1));
        offline = false;
        clock.Now = originalAt.AddMinutes(1);
        original.RecordRequested(user, 700, "tv");
        var requestedAt = clock.Now;
        clock.Now = originalAt.AddMinutes(2);
        original.MarkWatched(user, new HashSet<(int, string)> { (700, "tv") });
        var watchedAt = Assert.Single(original.LoadForUser(user)!.Entries).WatchedAtUtc;
        Assert.True(Assert.Single(original.LoadForUser(user)!.Entries).WasWatched);
        Assert.Null(Assert.Single(original.LoadForUser(user)!.Entries).CardRequestedAtUtc);
        DiscoveryFeedbackStore recovered = original;
        if (reconstruction)
        {
            original.Dispose();
            recovered = CreateStore();
            try { recovered.LoadAll(); } catch (IOException) { /* Original evidence is recovered by the managed writer. */ }
        }
        else
        {
            retry.ExpireDeadlines();
        }
        await CurrentWriter(recovered).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var repaired = Assert.Single(Assert.Single(recovered.LoadAll()).Entries);
        Assert.Equal(.2, repaired.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(originalAt, repaired.CardRequestedAtUtc);
        Assert.Equal(requestedAt, repaired.RequestedAtUtc);
        Assert.Equal(watchedAt, repaired.WatchedAtUtc);
        Assert.True(repaired.WasWatched);
        recovered.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, DateTime.UtcNow);
        Assert.Equal(.2, Assert.Single(recovered.LoadForUser(user)!.Entries).ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public async Task EqualTickOlderCardRetryCannotOverwriteTheFirstCommittedRequestInputs()
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var generated = clock.Now.AddMinutes(-1);
        store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, generated);
        fail = false;
        var firstCommitted = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } };
        store.RecordRequested(user, firstCommitted, generated);
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var replayed = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(.9, replayed.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(clock.Now, replayed.CardRequestedAtUtc);
        store.RecordRequested(user, firstCommitted, generated);
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(.9, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(clock.Now, entry.CardRequestedAtUtc);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FirstAdmittedEqualTickDismissalOrImpressionKeepsItsOriginalInputsAcrossRetryAndRestart(bool impression, bool reconstruction)
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var originalAt = clock.Now;
        var generated = originalAt.AddMinutes(-1);
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var offline = true;
        using var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) =>
            {
                if (offline) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        void Record(DiscoveryFeedbackStore store, double value)
        {
            var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = value } };
            if (impression) store.RecordImpressions(user, [card], generated);
            else store.RecordDismissed(user, card, generated);
        }

        Assert.Throws<IOException>(() => Record(original, .2));
        offline = false;
        Record(original, .7);
        var newer = Assert.Single(original.LoadForUser(user)!.Entries);
        Assert.Equal(.7, newer.ScoringFeatures!.GenreSimilarity);
        DiscoveryFeedbackStore recovered = original;
        if (reconstruction)
        {
            original.Dispose();
            recovered = CreateStore();
            try { recovered.LoadAll(); } catch (IOException) { /* Original recovery is managed independently of the delivery. */ }
        }
        else
        {
            retry.ExpireDeadlines();
        }

        await CurrentWriter(recovered).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var restored = Assert.Single(Assert.Single(recovered.LoadAll()).Entries);
        Assert.Equal(.2, restored.ScoringFeatures!.GenreSimilarity);
        Assert.True(restored.PredictionReceiptOrder < newer.PredictionReceiptOrder);
        Assert.Equal(newer.PredictionReceiptEpoch, restored.PredictionReceiptEpoch);
        Assert.Equal(generated, restored.ShownAtUtc);
        clock.Now = originalAt.AddMinutes(1);
        recovered.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, originalAt);
        Assert.Equal(.9, Assert.Single(recovered.LoadForUser(user)!.Entries).ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public void DurableAdmissionOrderContinuesAfterAllTicketsAreCompletedAndTheJournalIsReconstructed()
    {
        var directory = Path.Join(_tempDir, "admission-order");
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        var firstJournal = new DiscoveryFeedbackRecoveryJournal(directory, write);
        byte[] bytes = [1];
        var first = firstJournal.Admit(JournalTicket(bytes), bytes);
        firstJournal.Complete(first.Id);
        var secondJournal = new DiscoveryFeedbackRecoveryJournal(directory, write);
        var second = secondJournal.Admit(JournalTicket(bytes), bytes);
        Assert.Equal(first.AdmissionEpoch, second.AdmissionEpoch);
        Assert.True(second.AdmissionOrder > first.AdmissionOrder);
        Assert.InRange(new FileInfo(Path.Join(directory, ".admission-order")).Length, 1, DiscoveryFeedbackRecoveryJournal.MetadataReservationBytes);
        File.WriteAllText(Path.Join(directory, ".admission-order"), "{");
        Assert.Throws<IOException>(() => secondJournal.Admit(JournalTicket(bytes), bytes));
        Assert.Single(secondJournal.Pending());
    }

    [Fact]
    public void AFailedCounterWriteReleasesAdmissionCapacityBeforeAnyOriginalPayloadIsWritten()
    {
        var directory = Path.Join(_tempDir, "failed-counter-write");
        var failed = true;
        var payloadWrites = 0;
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) =>
        {
            payloadWrites++;
            Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        })
        {
            WriteCounterState = (path, contents) =>
            {
                if (failed) throw new IOException("counter unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        byte[] bytes = [1];
        Assert.Throws<IOException>(() => journal.Admit(JournalTicket(bytes), bytes));
        Assert.False(journal.HasPending);
        Assert.Empty(journal.Pending());
        Assert.Equal(0, payloadWrites);
        failed = false;
        Assert.NotNull(journal.Admit(JournalTicket(bytes), bytes));
        Assert.Equal(1, payloadWrites);
        Assert.Single(journal.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrRegressedCounterReconstructionUsesTheRetainedEpochAndHighestOrdinal(bool missing)
    {
        var directory = Path.Join(_tempDir, "repair-counter");
        byte[] bytes = [1];
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        var original = new DiscoveryFeedbackRecoveryJournal(directory, write);
        var first = original.Admit(JournalTicket(bytes), bytes);
        var counter = Path.Join(directory, ".admission-order");
        if (missing) File.Delete(counter);
        else File.WriteAllText(counter, System.Text.Json.JsonSerializer.Serialize(new { epoch = first.AdmissionEpoch, order = 0L }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options));
        var reconstructed = new DiscoveryFeedbackRecoveryJournal(directory, write);
        var next = reconstructed.Admit(JournalTicket(bytes), bytes);
        Assert.Equal(first.AdmissionEpoch, next.AdmissionEpoch);
        Assert.True(next.AdmissionOrder > first.AdmissionOrder);
        Assert.Equal(bytes, reconstructed.Read(first));
        Assert.Equal(2, reconstructed.Pending().Length);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("oversize")]
    [InlineData("exhausted")]
    [InlineData("different-epoch")]
    [InlineData("live-epoch")]
    [InlineData("ambiguous-epochs")]
    public void InvalidOrExhaustedCounterCannotAdmitOrDiscardOriginalEvidence(string boundary)
    {
        var directory = Path.Join(_tempDir, "invalid-counter-" + boundary);
        byte[] bytes = [1];
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, write);
        var first = journal.Admit(JournalTicket(bytes), bytes);
        var counter = Path.Join(directory, ".admission-order");
        if (boundary == "live-epoch") journal.Complete(first.Id);
        if (boundary == "ambiguous-epochs")
        {
            var other = first with { Id = Guid.NewGuid(), AdmissionEpoch = Guid.NewGuid() };
            write(Path.Join(directory, other.Id.ToString("N") + ".json"), System.Text.Json.JsonSerializer.Serialize(new { ticket = other, payload = bytes }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options));
            File.Delete(counter);
            journal = new DiscoveryFeedbackRecoveryJournal(directory, write);
        }
        else
        {
            var state = boundary switch
            {
                "null" => "null",
                "oversize" => new string(' ', 129),
                "exhausted" => System.Text.Json.JsonSerializer.Serialize(new { epoch = first.AdmissionEpoch, order = long.MaxValue }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options),
                _ => System.Text.Json.JsonSerializer.Serialize(new { epoch = Guid.NewGuid(), order = 1L }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options)
            };
            write(counter, state);
        }

        var pending = journal.Pending().Select(ticket => ticket.Id).Order().ToArray();
        Assert.Throws<IOException>(() => journal.Admit(JournalTicket(bytes), bytes));
        Assert.Equal(pending, journal.Pending().Select(ticket => ticket.Id).Order());
        Assert.Equal(pending.Length, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public void CounterLeaseContentionIsBoundedAndAReleasedLeaseAllowsTheNextOriginalAdmission()
    {
        var directory = Path.Join(_tempDir, "locked-counter");
        byte[] bytes = [1];
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents));
        var first = journal.Admit(JournalTicket(bytes), bytes);
        using (File.Open(Path.Join(directory, ".admission-lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => journal.Admit(JournalTicket(bytes), bytes));
            Assert.Single(journal.Pending());
        }

        var next = journal.Admit(JournalTicket(bytes), bytes);
        Assert.Equal(first.AdmissionEpoch, next.AdmissionEpoch);
        Assert.Equal(first.AdmissionOrder + 1, next.AdmissionOrder);
    }

    [Fact]
    public void ACompactLegacyIntentRewriteCannotExceedItsByteBudgetOrDiscardTheAcceptedOriginal()
    {
        var directory = Path.Join(_tempDir, "compact-intent-budget");
        byte[] bytes = [1];
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        var original = new DiscoveryFeedbackRecoveryJournal(directory, write);
        var ticket = original.Admit(JournalTicket(bytes) with { Kind = "RequestIntent", RequestSourceScope = "validated-source" }, bytes);
        var path = Path.Join(directory, ticket.Id.ToString("N") + ".json");
        ticket = ticket with { IntentSchemaVersion = 0 };
        write(path, System.Text.Json.JsonSerializer.Serialize(new { ticket, payload = bytes }, CompactJsonOptions));
        var budget = new FileInfo(path).Length + DiscoveryFeedbackRecoveryJournal.MetadataReservationBytes;
        var limited = new DiscoveryFeedbackRecoveryJournal(directory, write, byteBudget: budget);
        Assert.Throws<IOException>(() => limited.UpdateIntent(ticket.Id, accepted: true, uncertain: true));
        Assert.Equal("RequestIntent", limited.Get(ticket.Id).Kind);
        Assert.Equal(bytes, limited.Read(limited.Get(ticket.Id)));
        var recovered = new DiscoveryFeedbackRecoveryJournal(directory, write);
        var accepted = recovered.UpdateIntent(ticket.Id, accepted: true, uncertain: true);
        Assert.Equal("Request", accepted.Kind);
        Assert.Equal(accepted, recovered.UpdateIntent(ticket.Id, accepted: true, uncertain: true));
        Assert.Equal(bytes, recovered.Read(accepted));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscoveryFeedbackRecoveryJournal(directory, write, byteBudget: 127));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscoveryFeedbackRecoveryJournal(directory, write, byteBudget: 256L * 1024 * 1024 + 1));
    }

    [Fact]
    public void AnUnavailableEnvelopeReadIsAnIoFailureAndNonIntentKindsCannotBeConfirmedAsRequests()
    {
        var directory = Path.Join(_tempDir, "unavailable-envelope");
        byte[] bytes = [1];
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents))
        {
            OpenEnvelopeRead = _ => throw new UnauthorizedAccessException("Original envelope unavailable.")
        };
        var ticket = journal.Admit(JournalTicket(bytes) with { Kind = "Impression" }, bytes);
        Assert.Throws<IOException>(() => journal.Read(ticket));
        Assert.Throws<IOException>(() => journal.UpdateIntent(ticket.Id, accepted: true, uncertain: true));
        Assert.Single(journal.Pending());
    }

    [Fact]
    public void ARecoveryPathThatIsAFileCannotBePublishedAsAnEmptyFeedbackJournal()
    {
        var path = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        File.WriteAllText(path, "unavailable original evidence");
        using var store = CreateStore();
        Assert.Throws<IOException>(() => store.LoadAll());
        Assert.True(File.Exists(path));
        File.Delete(path);
        Assert.Empty(store.LoadAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyEqualTickEvidenceWithoutAdmissionOrderCannotDisplaceAlreadyCommittedInputs(bool impression)
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        var generated = clock.Now.AddMinutes(-1);
        Guid? committedEpoch;
        long? committedOrder;
        using (var first = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { ReceiptTimeProvider = clock })
        {
            var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } };
            if (impression) first.RecordImpressions(user, [card], generated);
            else first.RecordDismissed(user, card, generated);
            var entry = Assert.Single(first.LoadForUser(user)!.Entries);
            committedEpoch = entry.PredictionReceiptEpoch;
            committedOrder = entry.PredictionReceiptOrder;
        }

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, generatedAt = generated, knownPeople = (string[]?)null }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        var ticket = new DiscoveryFeedbackRecoveryJournal.Ticket(Guid.NewGuid(), user, 700, "tv", impression ? "Impression" : "Dismissal", clock.Now, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)));
        var path = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery", ticket.Id.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { ticket, payload }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options), TestContext.Current.CancellationToken);
        using var restored = CreateStore();
        try { restored.LoadAll(); } catch (IOException) { /* Legacy recovery remains managed, but its unknown order cannot supersede committed inputs. */ }
        await CurrentWriter(restored).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = Assert.Single(Assert.Single(restored.LoadAll()).Entries);
        Assert.Equal(.9, recovered.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(committedEpoch, recovered.PredictionReceiptEpoch);
        Assert.Equal(committedOrder, recovered.PredictionReceiptOrder);
        Assert.Equal(generated, recovered.ShownAtUtc);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PreparedOriginalRequestSurvivesDeliveryExpiryButCannotFitBeforeVerifiedAcceptance()
    {
        var user = Guid.NewGuid();
        var scope = "validated-source";
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-20) };
        var originalAt = clock.Now;
        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        DiscoveryFeedbackStore.PreparedRequest prepared;
        using (var first = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { ReceiptTimeProvider = clock })
        {
            prepared = first.PrepareRequestedCard(user, card, originalAt.AddMinutes(-1), scope);
            first.MarkPreparedSubmission(prepared);
            Assert.Empty(first.LoadAll());
            Assert.Empty(first.GetPendingRequestedItems(user));
            Assert.Contains((700, "tv"), first.GetPreparedRequestItems(user, scope));
        }

        using var reconstructed = CreateStore();
        Assert.Empty(reconstructed.LoadAll());
        Assert.Empty(reconstructed.GetPreparedRequestItems(user, "different-source"));
        Assert.True(await reconstructed.ConfirmPreparedForTitleAsync(user, 700, "tv", scope, TestContext.Current.CancellationToken));
        var entry = Assert.Single(Assert.Single(reconstructed.LoadAll()).Entries);
        Assert.Equal(.2, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(originalAt, entry.CardRequestedAtUtc);
        Assert.Equal(originalAt.AddMinutes(-1), entry.ShownAtUtc);
        Assert.True(await reconstructed.ConfirmPreparedRequestAsync(user, prepared, scope, TestContext.Current.CancellationToken));
        reconstructed.MarkPreparedSubmission(prepared);
        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ADefinitiveFailureCannotAbandonTheSharedOriginalWhileAnotherUpstreamAttemptStillOwnsIt(bool firstMarkedBeforeReuse, bool rejectFirst)
    {
        var user = Guid.NewGuid();
        using var store = CreateStore();
        var generated = DateTime.UtcNow.AddMinutes(-1);
        var first = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, generated, "source");
        if (firstMarkedBeforeReuse) store.MarkPreparedSubmission(first);
        var second = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, generated.AddSeconds(1), "source");
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Attempt, second.Attempt);
        Assert.False(second.PreviouslyUncertain);
        store.MarkPreparedSubmission(second);
        store.AbandonPreparedRequest(rejectFirst ? first : second);
        Assert.Contains((700, "tv"), store.GetPreparedRequestItems(user, "source"));
        Assert.Empty(store.LoadAll());
        Assert.True(await store.ConfirmPreparedRequestAsync(user, rejectFirst ? second : first, "source", TestContext.Current.CancellationToken));
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(.2, saved.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(generated, saved.ShownAtUtc);
        Assert.Empty(store.GetPreparedRequestItems(user, "source"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheLastDefinitivelyFailedAttemptRetiresTheOriginalAndAnUncertainSiblingRemainsRecoverable(bool uncertain)
    {
        var user = Guid.NewGuid();
        using var store = CreateStore();
        var first = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, DateTime.UtcNow, "source");
        var second = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv" }, DateTime.UtcNow, "source");
        store.MarkPreparedSubmission(first);
        store.MarkPreparedSubmission(second);
        store.FinishPreparedAttempt(first, uncertain);
        Assert.Contains((700, "tv"), store.GetPreparedRequestItems(user, "source"));
        store.AbandonPreparedRequest(second);
        Assert.Equal(uncertain, store.GetPreparedRequestItems(user, "source").Contains((700, "tv")));
        Assert.Empty(store.LoadAll());
        if (uncertain)
        {
            Assert.True(await store.ConfirmPreparedForTitleAsync(user, 700, "tv", "source", TestContext.Current.CancellationToken));
            Assert.Equal(.2, Assert.Single(Assert.Single(store.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedUncertainOriginalsCannotReenterTheRegistryWhenConfirmationOrALateSiblingFinishes(bool reconciled)
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-2);
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { PreparedAcknowledgmentLimit = 1 };
        var original = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, at, "source");
        store.MarkPreparedSubmission(original);
        store.FinishPreparedAttempt(original, submissionUncertain: true);
        var active = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, DateTime.UtcNow, "source");
        var sibling = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv" }, DateTime.UtcNow, "source");
        Assert.True(active.PreviouslyUncertain);
        Assert.True(sibling.PreviouslyUncertain);
        Assert.Single(UncertainPreparedIntents(store));

        Assert.True(reconciled
            ? await store.ConfirmPreparedForTitleAsync(user, 700, "tv", "source", TestContext.Current.CancellationToken)
            : await store.ConfirmPreparedRequestAsync(user, active, "source", TestContext.Current.CancellationToken));
        if (reconciled) store.FinishPreparedAttempt(active, submissionUncertain: false);
        Assert.Empty(UncertainPreparedIntents(store));
        Assert.Equal(1, PreparedAttemptCount(store));

        var next = store.PrepareRequestedCard(user, new() { TmdbId = 701, MediaType = "tv" }, DateTime.UtcNow, "source");
        Assert.True(await store.ConfirmPreparedRequestAsync(user, next, "source", TestContext.Current.CancellationToken));
        // The old acknowledgment was evicted, but its original is already durable
        // and absent from every journal. A still-live sibling must release its lease.
        store.FinishPreparedAttempt(sibling, submissionUncertain: true);
        Assert.Empty(UncertainPreparedIntents(store));
        Assert.Equal(0, PreparedAttemptCount(store));
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 700);
        Assert.Equal(.2, saved.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(at, saved.ShownAtUtc);
        Assert.Empty(store.GetPreparedRequestItems(user, "source"));
    }

    [Fact]
    public void FinishingAnUnknownOriginalCannotCreateUncertaintyOrInitializeRecoveryStorage()
    {
        using var store = CreateStore();
        store.FinishPreparedAttempt(new(Guid.NewGuid(), true), submissionUncertain: true);
        Assert.Empty(UncertainPreparedIntents(store));
        Assert.Equal(0, PreparedAttemptCount(store));
        Assert.Null(typeof(DiscoveryFeedbackStore).GetField("_recoveryJournal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
        Assert.Null(typeof(DiscoveryFeedbackStore).GetField("_preparedRecoveryJournal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
        Assert.Null(typeof(DiscoveryFeedbackStore).GetField("_deferredIntentJournal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
    }

    [Fact]
    public void PreparedAttemptCapacityFailsBeforeCapturingAnotherCardAndReturnsOnlyAfterARealLeaseFinishes()
    {
        var user = Guid.NewGuid();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { PreparedAttemptLimit = 1 };
        var first = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv" }, DateTime.UtcNow, "source");
        Assert.Throws<IOException>(() => store.PrepareRequestedCard(user, new() { TmdbId = 701, MediaType = "tv" }, DateTime.UtcNow, "source"));
        Assert.DoesNotContain((701, "tv"), store.GetPreparedRequestItems(user, "source"));
        store.AbandonPreparedRequest(first);
        var next = store.PrepareRequestedCard(user, new() { TmdbId = 701, MediaType = "tv" }, DateTime.UtcNow, "source");
        store.AbandonPreparedRequest(next);
        Assert.Empty(store.GetPreparedRequestItems(user, "source"));
    }

    [Fact]
    public async Task TwoSpacedCompleteNegativeSnapshotsMoveAnUnresolvedIntentWithoutLosingItsOriginalOrCreatingFittingEvidence()
    {
        var user = Guid.NewGuid();
        var at = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
        var clock = new ReceiptClock { Now = at };
        DiscoveryFeedbackStore.PreparedRequest prepared;
        using (var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { ReceiptTimeProvider = clock })
        {
            prepared = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, at.AddMinutes(-1), "source");
            store.MarkPreparedSubmission(prepared);
            store.FinishPreparedAttempt(prepared, submissionUncertain: true);
            clock.Now = at.AddMinutes(14);
            store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
            clock.Now = at.AddMinutes(15);
            store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "different-source");
            store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
            clock.Now = at.AddMinutes(19);
            store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
            Assert.Single(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-request-intents"), "*.json"));
            clock.Now = at.AddMinutes(20);
            store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
            Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-request-intents"), "*.json"));
            Assert.Single(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-deferred-request-intents"), "*.json"));
            Assert.Empty(store.LoadAll());
            Assert.Contains((700, "tv"), store.GetPreparedRequestItems(user, "source"));
        }

        using var restored = CreateStore();
        Assert.True(await restored.ConfirmPreparedForTitleAsync(user, 700, "tv", "source", TestContext.Current.CancellationToken));
        var original = Assert.Single(Assert.Single(restored.LoadAll()).Entries);
        Assert.Equal(.2, original.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(at.AddMinutes(-1), original.ShownAtUtc);
        Assert.Equal(at, original.RequestedAtUtc);
        Assert.Empty(restored.GetPreparedRequestItems(user, "source"));
    }

    [Fact]
    public async Task UnresolvedIntentCapacityCannotConsumeConfirmedFeedbackCapacityAndDormancyAllowsANewUpstreamAdmission()
    {
        var user = Guid.NewGuid();
        var at = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
        var clock = new ReceiptClock { Now = at };
        long bytes;
        using (var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { ReceiptTimeProvider = clock })
        {
            var pending = original.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, at, "source");
            original.MarkPreparedSubmission(pending);
            original.FinishPreparedAttempt(pending, true);
            var journal = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "jellyfin-helper-request-intents"), (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents));
            bytes = journal.Get(pending.Id).ReservedBytes + DiscoveryFeedbackRecoveryJournal.MetadataReservationBytes;
        }

        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { ReceiptTimeProvider = clock, PreparedRecoveryByteBudget = bytes };
        Assert.Throws<IOException>(() => store.PrepareRequestedCard(user, new() { TmdbId = 701, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, at, "source"));
        store.RecordImpressions(user, [new() { TmdbId = 900, MediaType = "movie", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .6 } }], at);
        Assert.Equal(900, Assert.Single(Assert.Single(store.LoadAll()).Entries).TmdbId);
        clock.Now = at.AddMinutes(30);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        clock.Now = at.AddMinutes(35);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        var next = store.PrepareRequestedCard(user, new() { TmdbId = 701, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, at, "source");
        store.AbandonPreparedRequest(next);
        Assert.True(await store.ConfirmPreparedForTitleAsync(user, 700, "tv", "source", TestContext.Current.CancellationToken));
        var recovered = Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 700);
        Assert.Equal(.2, recovered.ScoringFeatures!.GenreSimilarity, 10);
    }

    [Fact]
    public void AnInFlightAttemptOrFailedDormancyWriteCannotDiscardTheOriginalAndClockRollbackCannotCertifyASecondNegative()
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddHours(-1);
        var clock = new ReceiptClock { Now = at };
        var fail = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock,
            WriteRecovery = (path, contents) =>
            {
                if (fail && path.Contains("deferred-request-intents", StringComparison.Ordinal)) throw new IOException("archive unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var original = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv" }, at, "source");
        store.MarkPreparedSubmission(original);
        clock.Now = at.AddMinutes(30);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        store.FinishPreparedAttempt(original, true);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        clock.Now = at.AddMinutes(29);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        clock.Now = at.AddMinutes(35);
        fail = true;
        Assert.Throws<IOException>(() => store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source"));
        Assert.Single(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-request-intents"), "*.json"));
        Assert.Empty(store.LoadAll());
        fail = false;
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        Assert.Contains((700, "tv"), store.GetPreparedRequestItems(user, "source"));
    }

    [Fact]
    public async Task FailedDormancyDeletionReplicasRecoverOnlyOnceAfterConfirmationIsCancelledBeforeAdmission()
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddHours(-1);
        var clock = new ReceiptClock { Now = at };
        var preparedDirectory = Path.Join(_tempDir, "jellyfin-helper-request-intents");
        var archiveDirectory = Path.Join(_tempDir, "jellyfin-helper-deferred-request-intents");
        var failCompletion = false;
        var writes = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock,
            RetryTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            DeleteRecovery = path =>
            {
                if (failCompletion && Path.GetDirectoryName(path) == preparedDirectory) throw new IOException("source completion unavailable after import");
                File.Delete(path);
            },
            WriteFeedback = (path, contents) =>
            {
                Interlocked.Increment(ref writes);
                entered.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        Assert.Empty(store.LoadAll());
        var original = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", KnownPeople = ["original actor"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, at.AddMinutes(-1), "source");
        store.MarkPreparedSubmission(original);
        store.FinishPreparedAttempt(original, submissionUncertain: true);
        clock.Now = at.AddMinutes(15);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        clock.Now = at.AddMinutes(20);
        failCompletion = true;
        Assert.Throws<IOException>(() => store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source"));
        Assert.Single(Directory.GetFiles(preparedDirectory, "*.json"));
        Assert.Single(Directory.GetFiles(archiveDirectory, "*.json"));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfirmPreparedForTitleAsync(user, 700, "tv", "source", cancellation.Token));
        failCompletion = false;
        try
        {
            Assert.Contains((700, "tv"), store.GetPendingRequestedItems(user));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(1, (int)typeof(DiscoveryFeedbackStore).GetField("_pendingObservedWrites", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!);
            Assert.Throws<IOException>(() => store.LoadAll());
        }
        finally
        {
            release.Set();
            if (typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store) is Task<bool> writer)
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }

        Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(1, writes);
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(.2, saved.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(new[] { "original actor" }, saved.KnownPeople);
        Assert.Equal(at, saved.RequestedAtUtc);
        Assert.Equal(at.AddMinutes(-1), saved.ShownAtUtc);
        Assert.Empty(store.GetPendingRequestedItems(user));
        Assert.Empty(Directory.GetFiles(preparedDirectory, "*.json"));
        Assert.Empty(Directory.GetFiles(archiveDirectory, "*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentRecoveryReplicasPreferTheDurableRequestStatusWithoutRenewingOriginalEvidence(bool acceptedInArchive)
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var item = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item, generatedAt = at.AddMinutes(-1), knownPeople = (string[]?)null }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        var counterDirectory = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        var prepared = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "jellyfin-helper-request-intents"), write, counterDirectory: counterDirectory);
        var archive = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "jellyfin-helper-deferred-request-intents"), write, counterDirectory: counterDirectory);
        var original = prepared.Admit(JournalTicket(payload) with { User = user, Kind = "RequestIntent", Acquired = at, RequestSourceScope = "source" }, payload);
        archive.Import(original, payload);
        (acceptedInArchive ? archive : prepared).UpdateIntent(original.Id, accepted: true, uncertain: true);
        using var store = CreateStore();
        try { store.LoadAll(); } catch (IOException) { /* One durable accepted replica owns the managed recovery. */ }
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(.2, saved.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(at, saved.RequestedAtUtc);
        Assert.Equal(at.AddMinutes(-1), saved.ShownAtUtc);
        Assert.Equal(original.AdmissionEpoch, saved.PredictionReceiptEpoch);
        Assert.Equal(original.AdmissionOrder, saved.PredictionReceiptOrder);
        Assert.Empty(store.GetPendingRequestedItems(user));
    }

    [Fact]
    public async Task ConflictingRecoveryReplicasAreRejectedBeforeAnyOriginalIsQueuedOrDeleted()
    {
        var at = DateTime.UtcNow.AddMinutes(-20);
        Action<string, string> write = (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        var primaryDirectory = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        var primary = new DiscoveryFeedbackRecoveryJournal(primaryDirectory, write);
        var prepared = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "jellyfin-helper-request-intents"), write, counterDirectory: primaryDirectory);
        var archive = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "jellyfin-helper-deferred-request-intents"), write, counterDirectory: primaryDirectory);
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv" }, generatedAt = at }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        var unrelated = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item = new DiscoveryRecommendation { TmdbId = 701, MediaType = "tv" }, generatedAt = at }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        primary.Admit(JournalTicket(unrelated) with { Tmdb = 701, Kind = "Impression", Acquired = at.AddMinutes(-1) }, unrelated);
        var original = prepared.Admit(JournalTicket(payload) with { Kind = "RequestIntent", Acquired = at, RequestSourceScope = "source" }, payload);
        archive.Import(original with { Kind = "Request", User = Guid.NewGuid() }, payload);
        var files = await Task.WhenAll(Directory.GetFiles(_tempDir, "*.json", SearchOption.AllDirectories)
            .Select(async path => (Path: path, Contents: await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))));
        using var store = CreateStore();
        try
        {
            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.Null(typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
            Assert.False(File.Exists(Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json")));
            foreach (var file in files) Assert.Equal(file.Contents, await File.ReadAllTextAsync(file.Path, TestContext.Current.CancellationToken));
        }
        finally
        {
            store.Dispose();
            if (typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store) is Task<bool> writer)
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task GenericRecoveryAnAcceptedReplicaCannotBeAbandonedByAColdUnacceptedSibling()
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var counter = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        var preparedDirectory = Path.Join(_tempDir, "jellyfin-helper-request-intents");
        var archiveDirectory = Path.Join(_tempDir, "jellyfin-helper-deferred-request-intents");
        var original = SeedGenericRecoveryIntent(user, 700, preparedDirectory, counter, at, "accepted original");
        var archive = new DiscoveryFeedbackRecoveryJournal(archiveDirectory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents), counterDirectory: counter);
        archive.Import(original.Ticket with { Kind = "Request", SubmissionUncertain = true }, original.Payload);
        var files = Directory.GetFiles(_tempDir, "*.json", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
        using var store = CreateStore();
        var sibling = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", Title = "must not replace the original" }, DateTime.UtcNow, "source");
        Assert.Equal(original.Ticket.Id, sibling.Id);
        store.AbandonPreparedRequest(sibling);
        foreach (var file in files) Assert.Equal(file.Value, await File.ReadAllTextAsync(file.Key, TestContext.Current.CancellationToken));

        try { store.LoadAll(); } catch (IOException) { /* The accepted replica owns recovery even though this sibling was unaccepted. */ }
        Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        AssertGenericRecoveryPrediction(saved, original.Ticket, original.GeneratedAt, "accepted original");
        Assert.Empty(Directory.GetFiles(preparedDirectory, "*.json"));
        Assert.Empty(Directory.GetFiles(archiveDirectory, "*.json"));
        store.FinishPreparedAttempt(sibling, submissionUncertain: true);
        using var restarted = CreateStore();
        AssertGenericRecoveryPrediction(Assert.Single(Assert.Single(restarted.LoadAll()).Entries), original.Ticket, original.GeneratedAt, "accepted original");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericRecoveryConflictingPreparedReplicasCannotRetireOrCompleteAnOriginal(bool accepted)
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var counter = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        var preparedDirectory = Path.Join(_tempDir, "jellyfin-helper-request-intents");
        var archiveDirectory = Path.Join(_tempDir, "jellyfin-helper-deferred-request-intents");
        var original = SeedGenericRecoveryIntent(user, 700, preparedDirectory, counter, at, "original card");
        var conflicting = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Title = "different card", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, generatedAt = at, knownPeople = (string[]?)null }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        var archive = new DiscoveryFeedbackRecoveryJournal(archiveDirectory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents), counterDirectory: counter);
        archive.Import(original.Ticket with { Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(conflicting)) }, conflicting);
        var archivePath = Assert.Single(Directory.GetFiles(archiveDirectory, "*.json"));
        var archiveContents = await File.ReadAllTextAsync(archivePath, TestContext.Current.CancellationToken);
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider()
        };
        var prepared = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv" }, DateTime.UtcNow, "source");
        Assert.Equal(original.Ticket.Id, prepared.Id);
        if (accepted)
        {
            await Assert.ThrowsAsync<IOException>(() => store.ConfirmPreparedRequestAsync(user, prepared, "source", TestContext.Current.CancellationToken));
            Assert.False(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            AssertGenericRecoveryPrediction(Assert.Single(store.LoadForUser(user)!.Entries), original.Ticket, original.GeneratedAt, "original card");
            Assert.False(await store.ConfirmPreparedRequestAsync(user, prepared, "source", TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Throws<IOException>(() => store.AbandonPreparedRequest(prepared));
            Assert.Empty(store.GetRequestedItems(user));
        }

        Assert.Throws<IOException>(() => store.LoadAll());
        Assert.Equal(archiveContents, await File.ReadAllTextAsync(archivePath, TestContext.Current.CancellationToken));
        var retained = new DiscoveryFeedbackRecoveryJournal(preparedDirectory, (_, _) => { });
        var ticket = retained.Get(original.Ticket.Id);
        Assert.True(DiscoveryFeedbackRecoveryJournal.SameOriginal(original.Ticket, ticket));
        Assert.Equal(original.Payload, retained.Read(ticket));
        using var restarted = CreateStore();
        Assert.Throws<IOException>(() => restarted.LoadAll());
        Assert.Equal(archiveContents, await File.ReadAllTextAsync(archivePath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericRecoveryFailedLegacyMigrationKeepsActiveOriginalsAndResumesWithoutRenewingThem(bool failsDeletion)
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var primaryDirectory = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        var preparedDirectory = Path.Join(_tempDir, "jellyfin-helper-request-intents");
        var first = SeedGenericRecoveryIntent(user, 700, primaryDirectory, primaryDirectory, at, "first legacy card");
        var second = SeedGenericRecoveryIntent(user, 701, primaryDirectory, primaryDirectory, at.AddSeconds(1), "second legacy card");
        var files = Directory.GetFiles(primaryDirectory, "*.json").ToDictionary(path => path, File.ReadAllText);
        var unavailable = true;
        var writes = 0;
        var log = new Mock<IPluginLogService>();
        using var store = new DiscoveryFeedbackStore(log.Object, Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            WriteRecovery = (path, contents) =>
            {
                if (unavailable && !failsDeletion && Path.GetDirectoryName(path) == preparedDirectory) throw new IOException("migration copy unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            },
            DeleteRecovery = path =>
            {
                if (unavailable && failsDeletion && Path.GetDirectoryName(path) == primaryDirectory) throw new IOException("migration source completion unavailable");
                File.Delete(path);
            },
            WriteFeedback = (path, contents) =>
            {
                Interlocked.Increment(ref writes);
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var prepared = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, DateTime.UtcNow, "source");
        Assert.Equal(first.Ticket.Id, prepared.Id);
        Assert.Equal(failsDeletion ? 1 : 0, Directory.Exists(preparedDirectory) ? Directory.GetFiles(preparedDirectory, "*.json").Length : 0);
        Assert.Empty(store.LoadAll());
        foreach (var file in files) Assert.Equal(file.Value, await File.ReadAllTextAsync(file.Key, TestContext.Current.CancellationToken));
        log.Verify(value => value.LogWarning("DiscoveryFeedback", It.Is<string>(message => message.Contains("migration is deferred", StringComparison.Ordinal)), It.IsAny<Exception?>(), It.IsAny<ILogger?>()), Times.AtLeastOnce);
        unavailable = false;
        Assert.True(await store.ConfirmPreparedRequestAsync(user, prepared, "source", TestContext.Current.CancellationToken));
        AssertGenericRecoveryPrediction(Assert.Single(Assert.Single(store.LoadAll()).Entries), first.Ticket, first.GeneratedAt, "first legacy card");
        Assert.Empty(Directory.GetFiles(primaryDirectory, "*.json"));
        Assert.Equal(new HashSet<(int, string)> { (701, "tv") }, store.GetPreparedRequestItems(user, "source"));
        var retained = new DiscoveryFeedbackRecoveryJournal(preparedDirectory, (_, _) => { });
        Assert.True(DiscoveryFeedbackRecoveryJournal.SameOriginal(second.Ticket, retained.Get(second.Ticket.Id)));
        Assert.Equal(second.Payload, retained.Read(retained.Get(second.Ticket.Id)));
        Assert.True(await store.ConfirmPreparedRequestAsync(user, prepared, "source", TestContext.Current.CancellationToken));
        store.FinishPreparedAttempt(prepared, submissionUncertain: true);
        Assert.Equal(1, writes);
        using var restarted = CreateStore();
        AssertGenericRecoveryPrediction(Assert.Single(Assert.Single(restarted.LoadAll()).Entries), first.Ticket, first.GeneratedAt, "first legacy card");
        Assert.DoesNotContain((701, "tv"), restarted.GetRequestedItems(user));
    }

    [Fact]
    public async Task GenericRecoveryKnownAcceptanceWithCancelledAdmissionCannotBecomeANegativeSnapshot()
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var clock = new ReceiptClock { Now = at };
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry
        };
        var prepared = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", Title = "accepted before local cancellation", KnownPeople = ["original person"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, at.AddMinutes(-1), "source");
        store.MarkPreparedSubmission(prepared);
        var directory = Path.Join(_tempDir, "jellyfin-helper-request-intents");
        var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var contents = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfirmPreparedRequestAsync(user, prepared, "source", cancellation.Token));
        clock.Now = at.AddMinutes(30);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        clock.Now = at.AddMinutes(35);
        store.ObserveCompleteRequestSnapshot(user, new HashSet<(int, string)>(), "source");
        Assert.Equal(contents, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var original = new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }).Get(prepared.Id);
        Assert.Null(original.FirstNegativeAtUtc);
        Assert.False(Directory.Exists(Path.Join(_tempDir, "jellyfin-helper-deferred-request-intents")));
        await retry.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        retry.ExpireDeadlines();
        Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        AssertGenericRecoveryPrediction(Assert.Single(Assert.Single(store.LoadAll()).Entries), original, at.AddMinutes(-1), "accepted before local cancellation");
        Assert.Empty(store.GetPendingRequestedItems(user));
        Assert.True(await store.ConfirmPreparedRequestAsync(user, prepared, "source", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenericRecoveryAnUnavailablePreparedInventoryRetriesInitializationWithoutReplacingLegacyInputs()
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var primaryDirectory = Path.Join(_tempDir, "jellyfin-helper-feedback-recovery");
        var preparedDirectory = Path.Join(_tempDir, "jellyfin-helper-request-intents");
        var original = SeedGenericRecoveryIntent(user, 700, primaryDirectory, primaryDirectory, at, "legacy original");
        var path = Assert.Single(Directory.GetFiles(primaryDirectory, "*.json"));
        var contents = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(preparedDirectory, "unavailable inventory", TestContext.Current.CancellationToken);
        using var store = CreateStore();
        Assert.Throws<IOException>(() => store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", Title = "unadmitted replacement" }, DateTime.UtcNow, "source"));
        Assert.Throws<IOException>(() => store.LoadAll());
        Assert.Equal(contents, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        File.Delete(preparedDirectory);
        var prepared = store.PrepareRequestedCard(user, new() { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, DateTime.UtcNow, "source");
        Assert.Equal(original.Ticket.Id, prepared.Id);
        Assert.True(await store.ConfirmPreparedRequestAsync(user, prepared, "source", TestContext.Current.CancellationToken));
        AssertGenericRecoveryPrediction(Assert.Single(Assert.Single(store.LoadAll()).Entries), original.Ticket, original.GeneratedAt, "legacy original");
    }

    [Fact]
    public void GenericRecoveryLegacyDismissalIdentitiesMigrateWithoutReplacingWarmRequestedPredictions()
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var generated = at.AddMinutes(-1);
        var entry = new DiscoveryFeedbackEntry { TmdbId = 700, MediaType = "tv", Title = "warm request", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 }, ShownAtUtc = generated, PredictionEvidenceAtUtc = at, PredictionActionAtUtc = at, CardRequestedAtUtc = at, RequestedAtUtc = at };
        var data = new[] { new DiscoveryFeedbackResult { UserId = user, DismissedItems = [new(701, " TV "), new(701, "movie")], Entries = [entry] } };
        var path = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        var contents = System.Text.Json.JsonSerializer.Serialize(data, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        File.WriteAllText(path, contents);
        var fail = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            WriteFeedback = (file, json) =>
            {
                if (fail) throw new IOException("feedback file unavailable after identity migration");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(file, json);
            }
        };
        var warm = store.LoadForUser(user)!;
        store.RecordGenerated(user, "viewer", [new() { TmdbId = 900 }], DateTime.UtcNow);
        Assert.Equal(contents, File.ReadAllText(path));
        Assert.Contains((701, "tv"), store.GetDismissedItems(user));
        Assert.Contains((701, "movie"), store.GetDismissedItems(user));
        Assert.Equal(.2, Assert.Single(warm.Entries).ScoringFeatures!.GenreSimilarity);
        fail = false;
        store.RecordGenerated(user, "viewer", [new() { TmdbId = 900 }], DateTime.UtcNow);
        var durable = System.Text.Json.JsonSerializer.Deserialize<DiscoveryFeedbackResult[]>(File.ReadAllText(path), Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options)!;
        Assert.Null(Assert.Single(durable).DismissedItems);
        using var restarted = CreateStore();
        var requested = Assert.Single(restarted.LoadForUser(user)!.Entries, value => value.TmdbId == 700);
        Assert.Equal(.2, requested.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(generated, requested.ShownAtUtc);
        Assert.Equal(at, requested.RequestedAtUtc);
        Assert.Contains((701, "tv"), restarted.GetDismissedItems(user));
        Assert.Contains((701, "movie"), restarted.GetDismissedItems(user));
    }

    [Fact]
    public void GenericRecoveryColdIntentReservationsRejectAnUnderfundedInventoryWithoutDiscardingEvidence()
    {
        var directory = Path.Join(_tempDir, "intent-reservation-restart");
        byte[] payload = [1];
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents));
        var original = journal.Admit(JournalTicket(payload) with { Kind = "RequestIntent", RequestSourceScope = "source" }, payload);
        var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var contents = File.ReadAllText(path);
        var length = new FileInfo(path).Length;
        Assert.True(original.ReservedBytes > length);
        Assert.Throws<IOException>(() => new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { }, byteBudget: length + DiscoveryFeedbackRecoveryJournal.MetadataReservationBytes));
        Assert.Equal(contents, File.ReadAllText(path));
        var restored = new DiscoveryFeedbackRecoveryJournal(directory, (_, _) => { });
        Assert.True(DiscoveryFeedbackRecoveryJournal.SameOriginal(original, restored.Get(original.Id)));
        Assert.Equal(payload, restored.Read(restored.Get(original.Id)));
    }

    [Fact]
    public void GenericRecoveryColdPlaylistReservationsRejectAnUnderfundedInventoryWithoutReplacingTheAttempt()
    {
        var directory = Path.Join(_tempDir, "playlist-reservation-restart");
        var journal = new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist.PlaylistCreationJournal(directory);
        var original = journal.Begin(Guid.NewGuid(), new HashSet<Guid> { Guid.NewGuid() });
        var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var contents = File.ReadAllText(path);
        var length = new FileInfo(path).Length;
        Assert.True(original.ReservedBytes > length);
        Assert.Throws<IOException>(() => new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist.PlaylistCreationJournal(directory, byteBudget: length));
        Assert.Equal(contents, File.ReadAllText(path));
        var restored = new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist.PlaylistCreationJournal(directory);
        Assert.Equal(original.Id, restored.Get(original.Owner)!.Id);
        Assert.Throws<IOException>(() => restored.Begin(original.Owner, new HashSet<Guid>()));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    private static (DiscoveryFeedbackRecoveryJournal.Ticket Ticket, byte[] Payload, DateTime GeneratedAt) SeedGenericRecoveryIntent(Guid user, int tmdb, string directory, string counter, DateTime acquired, string title)
    {
        var generatedAt = acquired.AddMinutes(-1);
        var item = new DiscoveryRecommendation { TmdbId = tmdb, MediaType = "tv", Title = title, KnownPeople = ["original person"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item, generatedAt, knownPeople = item.KnownPeople }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents), counterDirectory: counter);
        return (journal.Admit(JournalTicket(payload) with { User = user, Tmdb = tmdb, Kind = "RequestIntent", Acquired = acquired, RequestSourceScope = "source" }, payload), payload, generatedAt);
    }

    private static void AssertGenericRecoveryPrediction(DiscoveryFeedbackEntry entry, DiscoveryFeedbackRecoveryJournal.Ticket ticket, DateTime generated, string title)
    {
        Assert.Equal(ticket.Tmdb, entry.TmdbId);
        Assert.Equal(title, entry.Title);
        Assert.Equal(.2, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(new[] { "original person" }, entry.KnownPeople);
        Assert.Equal(generated, entry.ShownAtUtc);
        Assert.Equal(ticket.Acquired, entry.RequestedAtUtc);
        Assert.Equal(ticket.AdmissionEpoch, entry.PredictionReceiptEpoch);
        Assert.Equal(ticket.AdmissionOrder, entry.PredictionReceiptOrder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPreparedOriginalsMoveOutOfSharedFeedbackCapacityBeforeAnyNegativeOutcomeIsInferred(bool activeIndexFull)
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var item = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item, generatedAt = at, knownPeople = (string[]?)null }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
        var journal = new DiscoveryFeedbackRecoveryJournal(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents));
        var ticket = journal.Admit(JournalTicket(payload) with { User = user, Tmdb = 700, Type = "tv", Kind = "RequestIntent", RequestSourceScope = "source" }, payload);
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { PreparedRecoveryByteBudget = activeIndexFull ? 128 : 64 * 1024 * 1024 };
        Assert.Empty(store.LoadAll());
        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
        Assert.Contains((700, "tv"), store.GetPreparedRequestItems(user, "source"));
        Assert.True(await store.ConfirmPreparedForTitleAsync(user, 700, "tv", "source", TestContext.Current.CancellationToken));
        Assert.Equal(.2, Assert.Single(Assert.Single(store.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
        Assert.True(ticket.AdmissionOrder > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedConfirmationCannotUseAnotherUserOrSourceAndDoesNotStrandTheCorrectOriginal(bool wrongUser)
    {
        var user = Guid.NewGuid();
        const string scope = "validated-source";
        using var store = CreateStore();
        var prepared = store.PrepareRequestedCard(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, DateTime.UtcNow.AddMinutes(-1), scope);
        await Assert.ThrowsAsync<IOException>(() => store.ConfirmPreparedRequestAsync(wrongUser ? Guid.NewGuid() : user, prepared, wrongUser ? scope : "different-source", TestContext.Current.CancellationToken));
        Assert.Empty(store.LoadAll());
        Assert.Empty(store.GetPendingRequestedItems(user));
        Assert.True(await store.ConfirmPreparedRequestAsync(user, prepared, scope, TestContext.Current.CancellationToken));
        Assert.Equal(.2, Assert.Single(Assert.Single(store.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public async Task ConfirmationAfterDisposalReleasesItsActiveLeaseAndKeepsTheOriginalForRestartRecovery()
    {
        var user = Guid.NewGuid();
        const string scope = "validated-source";
        var stopped = CreateStore();
        var prepared = stopped.PrepareRequestedCard(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, DateTime.UtcNow.AddMinutes(-1), scope);
        stopped.MarkPreparedSubmission(prepared);
        stopped.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stopped.ConfirmPreparedRequestAsync(user, prepared, scope, TestContext.Current.CancellationToken));
        using var recovered = CreateStore();
        Assert.Empty(recovered.LoadAll());
        Assert.True(await recovered.ConfirmPreparedForTitleAsync(user, 700, "tv", scope, TestContext.Current.CancellationToken));
        Assert.Equal(.2, Assert.Single(Assert.Single(recovered.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public async Task CompletionAcknowledgmentsEvictOnlyOldIdsWithoutDiscardingDurableRequestedPredictions()
    {
        var user = Guid.NewGuid();
        const string scope = "validated-source";
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { PreparedAcknowledgmentLimit = 2 };
        var prepared = new List<DiscoveryFeedbackStore.PreparedRequest>();
        foreach (var id in new[] { 700, 701, 702 })
        {
            var receipt = store.PrepareRequestedCard(user, new DiscoveryRecommendation { TmdbId = id, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, DateTime.UtcNow.AddMinutes(-1), scope);
            prepared.Add(receipt);
            Assert.True(await store.ConfirmPreparedRequestAsync(user, receipt, scope, TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<IOException>(() => store.ConfirmPreparedRequestAsync(user, prepared[0], scope, TestContext.Current.CancellationToken));
        Assert.True(await store.ConfirmPreparedRequestAsync(user, prepared[2], scope, TestContext.Current.CancellationToken));
        Assert.Equal(3, Assert.Single(store.LoadAll()).Entries.Count);
        Assert.Empty(store.GetPendingRequestedItems(user));
    }

    [Fact]
    public async Task FailedAcceptedIntentTransitionRetainsOriginalPayloadAndDefersFittingUntilManagedRecovery()
    {
        var user = Guid.NewGuid();
        var scope = "validated-source";
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var offline = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = retry,
            WriteRecovery = (path, contents) =>
            {
                if (offline) throw new IOException("confirmation unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var prepared = store.PrepareRequestedCard(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, DateTime.UtcNow.AddMinutes(-1), scope);
        store.MarkPreparedSubmission(prepared);
        offline = true;
        await Assert.ThrowsAsync<IOException>(() => store.ConfirmPreparedRequestAsync(user, prepared, scope, TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        store.AbandonPreparedRequest(prepared);
        Assert.Throws<IOException>(() => store.LoadAll());
        Assert.Contains((700, "tv"), store.GetPreparedRequestItems(user, scope));
        offline = false;
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(.2, Assert.Single(Assert.Single(store.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledPreparedConfirmationReleasesItsActiveTicketAndRecoversTheOriginalWithoutASecondSubmission(bool afterAdmission)
    {
        var user = Guid.NewGuid();
        var scope = "validated-source";
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) =>
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        Assert.Empty(store.LoadAll());
        var prepared = store.PrepareRequestedCard(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, clock.Now.AddMinutes(-1), scope);
        store.MarkPreparedSubmission(prepared);
        if (!afterAdmission) await cancellation.CancelAsync();
        var confirmation = store.ConfirmPreparedRequestAsync(user, prepared, scope, cancellation.Token);
        try
        {
            if (afterAdmission)
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                store.AbandonPreparedRequest(prepared);
                Assert.Single(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-request-intents"), "*.json"));
                Assert.False(await store.ConfirmPreparedRequestAsync(user, prepared, scope, TestContext.Current.CancellationToken));
                await cancellation.CancelAsync();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation);
            Assert.Contains((700, "tv"), store.GetPendingRequestedItems(user));
            Assert.Throws<IOException>(() => store.LoadAll());
        }
        finally
        {
            release.Set();
            retry.ExpireDeadlines();
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(.2, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(clock.Now, entry.CardRequestedAtUtc);
        Assert.True(await store.ConfirmPreparedRequestAsync(user, prepared, scope, TestContext.Current.CancellationToken));
        Assert.Empty(store.GetPendingRequestedItems(user));
        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADefinitivelyRejectedPreparedRequestCanRetireButAnEarlierUncertainSubmissionKeepsItsOriginal(bool uncertain)
    {
        var user = Guid.NewGuid();
        const string scope = "validated-source";
        using var store = CreateStore();
        var original = store.PrepareRequestedCard(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, DateTime.UtcNow.AddMinutes(-20), scope);
        store.MarkPreparedSubmission(original);
        if (uncertain) store.FinishPreparedAttempt(original, submissionUncertain: true);
        var rejected = uncertain
            ? store.PrepareRequestedCard(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, DateTime.UtcNow, scope)
            : original;
        Assert.Equal(uncertain, rejected.PreviouslyUncertain);
        store.AbandonPreparedRequest(rejected);
        Assert.Empty(store.LoadAll());
        Assert.Equal(uncertain, store.GetPreparedRequestItems(user, scope).Contains((700, "tv")));
        if (uncertain)
        {
            Assert.True(await store.ConfirmPreparedForTitleAsync(user, 700, "tv", scope, TestContext.Current.CancellationToken));
            Assert.Equal(.2, Assert.Single(Assert.Single(store.LoadAll()).Entries).ScoringFeatures!.GenreSimilarity);
        }

        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
    }

    [Fact]
    public async Task OverflowOriginalSurvivesDeliveryExpiryAndAReconstructedStoreWithoutResubmittingAnyToken()
    {
        var user = Guid.NewGuid();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-20) };
        var acquired = clock.Now;
        var generated = acquired.AddMinutes(-1);
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        using var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry, ReplayPayloadBudget = 1,
            WriteFeedback = (_, _) => throw new IOException("feedback storage unavailable")
        };
        foreach (var id in Enumerable.Range(1000, 64))
        {
            Assert.False(await original.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(original).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", KnownPeople = ["original credits"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } };
        Assert.Throws<IOException>(() => original.RecordImpressions(user, [card], generated));
        Assert.Equal(0, RetainedReplayBytes(original));
        original.Dispose();
        clock.Now = acquired.AddMinutes(15);
        using var restarted = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { ReceiptTimeProvider = clock, RetryTimeProvider = retry, ReplayPayloadBudget = 1 };
        try { restarted.LoadAll(); } catch (IOException) { /* Pending originals deliberately withhold fitting until the managed recovery completes. */ }
        await CurrentWriter(restarted).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = Assert.Single(Assert.Single(restarted.LoadAll()).Entries);
        Assert.Equal(acquired, recovered.ImpressionAtUtc);
        Assert.Equal(generated, recovered.ShownAtUtc);
        Assert.Equal(card.KnownPeople, recovered.KnownPeople);
        Assert.Equal(.2, recovered.ScoringFeatures!.GenreSimilarity);
        Assert.Empty(Directory.GetFiles(Path.Join(_tempDir, "jellyfin-helper-feedback-recovery"), "*.json"));
    }

    [Fact]
    public void FailedRecoveryAdmissionDoesNotCreateAnUnrecoverableGlobalFittingReceipt()
    {
        var attempts = 0;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            WriteRecovery = (_, _) => throw new IOException("recovery storage unavailable"),
            WriteFeedback = (path, contents) => { attempts++; Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        Assert.Throws<IOException>(() => store.RecordImpressions(Guid.NewGuid(), [new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures() }], DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(0, attempts);
        Assert.Empty(store.LoadAll());
        Assert.Equal(0, RetainedReplayBytes(store));
    }

    [Fact]
    public async Task RepairingAnOversizedFeedbackFileUnblocksTheSameStoresRetainedDismissal()
    {
        var user = Guid.NewGuid();
        var file = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        using (var initial = CreateStore()) initial.RecordDismissed(user, 777, "tv");
        var backup = File.ReadAllBytes(file);
        using (var stream = File.OpenWrite(file)) stream.SetLength(121L * 1024 * 1024);
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir) { RetryTimeProvider = retry };
        Assert.Throws<IOException>(() => store.RecordDismissed(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Throws<IOException>(() => store.LoadAll());
        File.WriteAllBytes(file, backup);
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var rows = Assert.Single(store.LoadAll()).Entries;
        Assert.Contains(rows, row => row.TmdbId == 777 && row.DismissedAtUtc.HasValue);
        Assert.Equal(.2, Assert.Single(rows, row => row.TmdbId == 700).ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public async Task StalledRecoveryAdmissionWithholdsFittingWithoutOwningTheReadinessGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            WriteRecovery = (path, contents) => { entered.TrySetResult(); release.Wait(TestContext.Current.CancellationToken); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        Assert.Empty(store.LoadAll());
        var user = Guid.NewGuid();
        var write = Task.Run(() => store.RecordImpressions(user, [new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv" }], DateTime.UtcNow.AddMinutes(-1)), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Task.Run(() => Assert.Throws<IOException>(() => store.LoadAll()), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
            await write.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Single(Assert.Single(store.LoadAll()).Entries);
    }

    [Fact]
    public void DurableRecoveryRejectsTamperedOriginalPayloadInsteadOfAcceptingReplacementInputs()
    {
        var directory = Path.Join(_tempDir, "integrity-journal");
        var journal = new DiscoveryFeedbackRecoveryJournal(directory, (path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents));
        var bytes = System.Text.Encoding.UTF8.GetBytes("{}");
        var ticket = journal.Admit(new DiscoveryFeedbackRecoveryJournal.Ticket(Guid.NewGuid(), Guid.NewGuid(), 700, "tv", "Impression", DateTime.UtcNow, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), bytes);
        Assert.Equal(bytes, journal.Read(ticket));
        var file = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        json["payload"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("[]"));
        File.WriteAllText(file, json.ToJsonString());
        Assert.Throws<IOException>(() => journal.Read(ticket));
        Assert.True(journal.HasPending);
        Assert.Single(journal.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADelayedCardRequestOwnsTheOutcomeTimeDespiteALaterIdentityConfirmation(bool newerCard)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var firstAt = clock.Now;
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retry,
            WriteFeedback = (path, contents) => { if (fail) throw new IOException("offline"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var user = Guid.NewGuid();
        store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", KnownPeople = ["original actor"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .2 } }, firstAt.AddMinutes(-1));
        clock.Now = firstAt.AddMinutes(1);
        fail = false;
        store.RecordRequested(user, 700, "tv");
        if (newerCard) store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, clock.Now.AddSeconds(-1));
        retry.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(newerCard ? clock.Now : firstAt, entry.RequestedAtUtc);
        Assert.Equal(entry.CardRequestedAtUtc, entry.RequestedAtUtc);
        Assert.Equal(newerCard ? .9 : .2, entry.ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public async Task ARequestFinishingAfterDisposeCannotPromoteRetainedDependentWatches()
    {
        var retry = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = true;
        var stall = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = retry,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                if (stall) { entered.TrySetResult(); release.Wait(TestContext.Current.CancellationToken); }
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        Assert.False(await store.RecordRequestedAsync(user, 700, "tv", TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (700, "tv") }, TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        fail = false;
        stall = true;
        var request = Task.Run(() => store.RecordRequested(user, 700, "tv"), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            store.Dispose();
            release.Set();
            await request.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(Assert.Single(store.LoadForUser(user)!.Entries).WasWatched);
            retry.ExpireDeadlines();
            Assert.True(CurrentWriter(store).IsCompleted);
            Assert.False(Assert.Single(CreateStore().LoadForUser(user)!.Entries).WasWatched);
        }
        finally { release.Set(); await request.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task ADisposedOldRetryCallbackCannotTakeOwnershipOfANewRetryTimer()
    {
        var clock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = clock,
            WriteFeedback = (path, contents) => { if (fail) throw new IOException("offline"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var user = Guid.NewGuid();
        Assert.Throws<IOException>(() => store.RecordDismissed(user, 1, "movie"));
        var dispatchedOldCallback = clock.CaptureLatestCallback();
        fail = false;
        Assert.True(await store.RecordGeneratedAsync(user, "viewer", [new() { TmdbId = 500 }], DateTime.UtcNow, TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        fail = true;
        Assert.Throws<IOException>(() => store.RecordDismissed(user, 2, "tv"));
        var timerField = typeof(DiscoveryFeedbackStore).GetField("_retryTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var newTimer = timerField.GetValue(store);
        Assert.NotNull(newTimer);
        dispatchedOldCallback();
        Assert.Same(newTimer, timerField.GetValue(store));
        Assert.Throws<IOException>(() => store.LoadAll());
        fail = false;
        clock.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains((2, "tv"), store.GetDismissedItems(user));
        Assert.NotEmpty(store.LoadAll());
    }

    [Fact]
    public async Task UnknownInitialReadIsRetriedBeforeAWriteAndCannotEraseOtherUsersDurableDismissals()
    {
        var originalUser = Guid.NewGuid();
        using (var original = CreateStore()) original.RecordDismissed(originalUser, 700, "tv");
        var path = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        var before = File.ReadAllText(path);
        var clock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var unavailableRead = true;
        var unavailableWrite = true;
        var reads = 0;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = clock,
            OpenFeedbackRead = file => { reads++; if (unavailableRead) throw new IOException("unavailable read"); return File.OpenRead(file); },
            WriteFeedback = (file, contents) => { if (unavailableWrite) throw new IOException("unavailable write"); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(file, contents); }
        };
        var other = Guid.NewGuid();
        Assert.Throws<IOException>(() => store.RecordDismissed(other, 800, "movie"));
        Assert.Equal(before, File.ReadAllText(path));
        await clock.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        unavailableRead = false;
        clock.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, reads);
        Assert.Contains((700, "tv"), store.GetDismissedItems(originalUser));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Throws<IOException>(() => store.LoadAll());
        await clock.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        unavailableWrite = false;
        clock.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var restarted = CreateStore();
        Assert.Contains((700, "tv"), restarted.GetDismissedItems(originalUser));
        Assert.Contains((800, "movie"), restarted.GetDismissedItems(other));
        Assert.Equal(2, store.LoadAll().Count);
    }

    public void Dispose()
    {
        foreach (var store in _ownedStores) store.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Best effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup
        }
        catch (IOException)
        {
            // Best effort cleanup
        }
    }

    private DiscoveryFeedbackStore CreateStore()
    {
        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryFeedbackStore>>();
        var store = new DiscoveryFeedbackStore(pluginLog.Object, logger.Object, _tempDir);
        _ownedStores.Add(store);
        return store;
    }

    [Theory]
    [InlineData(400)]
    [InlineData(1)]
    public void ExplicitDiscoveryDismissalSurvivesTrainingAgeEvictionCountPressureAndReload(int daysOld)
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddDays(-daysOld);
        File.WriteAllText(Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json"), System.Text.Json.JsonSerializer.Serialize(new[] { new DiscoveryFeedbackResult
        {
            UserId = user, Entries = [new DiscoveryFeedbackEntry { TmdbId = 700, MediaType = "tv", ShownAtUtc = at, DismissedAtUtc = at }]
        } }));
        var store = CreateStore();
        Assert.Contains((700, "tv"), store.GetDismissedItems(user));
        store.RecordGenerated(user, "viewer", Enumerable.Range(1000, 250).Select(id => new DiscoveryRecommendation { TmdbId = id, MediaType = "movie" }).ToArray());
        var retained = store.LoadForUser(user)!;
        Assert.Equal(200, retained.Entries.Count);
        Assert.DoesNotContain(retained.Entries, entry => entry.TmdbId == 700);
        Assert.Equal(new DiscoveryDismissedItem(700, "tv"), Assert.Single(retained.DismissedItems!));
        var reloaded = CreateStore();
        Assert.Contains((700, "tv"), reloaded.GetDismissedItems(user));
        Assert.DoesNotContain((700, "movie"), reloaded.GetDismissedItems(user));
        Assert.Empty(reloaded.GetDismissedItems(Guid.NewGuid()));
        Assert.Empty(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([retained], new Dictionary<Guid, UserWatchProfile>(), null, null, TestContext.Current.CancellationToken).Examples);
        var detached = Assert.IsType<DiscoveryDismissedItem[]>(retained.DismissedItems);
        detached[0] = new DiscoveryDismissedItem(999, "movie");
        Assert.Contains((700, "tv"), store.GetDismissedItems(user));
        Assert.DoesNotContain((999, "movie"), store.GetDismissedItems(user));
    }

    [Fact]
    public void AUserWithOnlyPermanentDismissalIdentityIsNotRemovedAsAnEmptyTrainingHistory()
    {
        var user = Guid.NewGuid();
        var at = DateTime.UtcNow.AddDays(-400);
        File.WriteAllText(Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json"), System.Text.Json.JsonSerializer.Serialize(new[] { new DiscoveryFeedbackResult
        {
            UserId = user, Entries = [new DiscoveryFeedbackEntry { TmdbId = 700, MediaType = "movie", ShownAtUtc = at, DismissedAtUtc = at }]
        } }));
        var store = CreateStore();
        store.RecordGenerated(Guid.NewGuid(), "other viewer", [new DiscoveryRecommendation { TmdbId = 900 }]);
        var retained = store.LoadForUser(user)!;
        Assert.Empty(retained.Entries);
        Assert.Equal(new DiscoveryDismissedItem(700, "movie"), Assert.Single(retained.DismissedItems!));
        Assert.Contains((700, "movie"), CreateStore().GetDismissedItems(user));
    }

    [Fact]
    public async Task AConfirmedDismissalIsExcludedDuringPersistenceWithoutPublishingIncompleteTrainingFeedback()
    {
        var acquired = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stall = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = acquired,
            WriteFeedback = (path, contents) =>
            {
                if (stall) { entered.TrySetResult(); release.Wait(TestContext.Current.CancellationToken); }
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var predicted = acquired.Now.AddMinutes(-1);
        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } };
        store.RecordGenerated(user, "viewer", [card], predicted);
        stall = true;
        var dismissal = Task.Run(() => store.RecordDismissed(user, card, predicted), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Task.Run(() =>
            {
                Assert.Contains((700, "tv"), store.GetDismissedItems(user));
                Assert.DoesNotContain((700, "movie"), store.GetDismissedItems(user));
                Assert.Empty(store.GetDismissedItems(Guid.NewGuid()));
                Assert.Null(Assert.Single(store.LoadForUser(user)!.Entries).DismissedAtUtc);
                Assert.Throws<IOException>(() => store.LoadAll());
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            release.Set();
            await dismissal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
            Assert.Equal(acquired.Now, saved.DismissedAtUtc);
            Assert.Equal(predicted, saved.ShownAtUtc);
            Assert.Equal(.7, saved.ScoringFeatures!.GenreSimilarity);
            Assert.Contains((700, "tv"), store.GetDismissedItems(user));
        }
        finally { release.Set(); await dismissal; }
    }

    [Fact]
    public async Task AFailedConfirmedDismissalStaysExcludedThroughRetryAndRestartWithoutANewUserAction()
    {
        var clock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var acquired = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        var fail = false;
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = clock, ReceiptTimeProvider = acquired,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("temporarily unwritable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
                if (contents.Contains("dismissedAtUtc", StringComparison.OrdinalIgnoreCase)) persisted.TrySetResult();
            }
        };
        var user = Guid.NewGuid();
        var predicted = acquired.Now.AddMinutes(-1);
        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } };
        store.RecordGenerated(user, "viewer", [card], predicted);
        fail = true;
        Assert.Throws<IOException>(() => store.RecordDismissed(user, card, predicted));
        await clock.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains((700, "tv"), store.GetDismissedItems(user));
        Assert.DoesNotContain((700, "movie"), store.GetDismissedItems(user));
        Assert.Empty(store.GetDismissedItems(Guid.NewGuid()));
        Assert.Throws<IOException>(() => store.LoadAll());
        fail = false;
        clock.ExpireDeadlines();
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
        Assert.Equal(acquired.Now, saved.DismissedAtUtc);
        Assert.Equal(predicted, saved.ShownAtUtc);
        Assert.Equal(.7, saved.ScoringFeatures!.GenreSimilarity);
        var restarted = CreateStore();
        Assert.Contains((700, "tv"), restarted.GetDismissedItems(user));
        Assert.DoesNotContain((700, "movie"), restarted.GetDismissedItems(user));
        Assert.Empty(restarted.GetDismissedItems(Guid.NewGuid()));
    }

    [Fact]
    public async Task ConfirmedFailedRequestRetriesAfterDiskRecoveryWithoutANewSubmission()
    {
        var clock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var receipt = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        var fail = true;
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            RetryTimeProvider = clock, ReceiptTimeProvider = receipt,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("temporarily unwritable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
                persisted.TrySetResult();
            }
        };
        var user = Guid.NewGuid();
        Assert.False(await store.RecordRequestedAsync(user, 600, "movie", TestContext.Current.CancellationToken));
        await clock.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => store.LoadAll());
        fail = false;
        clock.ExpireDeadlines();
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(receipt.Now, Assert.Single(Assert.Single(store.LoadAll()).Entries).RequestedAtUtc);
        Assert.Empty(store.GetPendingRequestedItems(user));
    }

    [Fact]
    public async Task PendingImpressionPersistenceWithholdsTrainingButNotThePublishedServingSnapshot()
    {
        var acquired = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stall = false;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            ReceiptTimeProvider = acquired,
            WriteFeedback = (path, contents) =>
            {
                if (stall) { started.TrySetResult(); release.Wait(TestContext.Current.CancellationToken); }
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var card = new DiscoveryRecommendation { TmdbId = 600, ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } };
        store.RecordGenerated(user, "viewer", [card]);
        stall = true;
        var impression = Task.Run(() => store.RecordImpressions(user, [card], acquired.Now.AddMinutes(-1)), TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Task.Run(() =>
            {
                Assert.Null(Assert.Single(store.LoadForUser(user)!.Entries).ImpressionAtUtc);
                Assert.Throws<IOException>(() => store.LoadAll());
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            release.Set();
            await impression.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
            Assert.Equal(acquired.Now, entry.ImpressionAtUtc);
            Assert.Equal(.7, entry.ScoringFeatures!.GenreSimilarity);
        }
        finally { release.Set(); await impression.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task OversizeRecoveryNeverPublishesUncompactedPredictionInputsWhileTheWriteIsStalled()
    {
        var user = Guid.NewGuid();
        File.WriteAllText(Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json"), System.Text.Json.JsonSerializer.Serialize(new[] { new DiscoveryFeedbackResult
        {
            UserId = user, Entries = [new DiscoveryFeedbackEntry { TmdbId = 600, ShownAtUtc = DateTime.UtcNow.AddYears(-2), ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } }]
        } }));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir, sizeLimit: 100)
        {
            BeforeTrainingSnapshotSelection = () => selecting.TrySetResult(),
            WriteFeedback = (path, contents) => { entered.TrySetResult(); release.Wait(TestContext.Current.CancellationToken); Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents); }
        };
        var load = Task.Run(() => store.LoadForUser(user), TestContext.Current.CancellationToken);
        Task<IReadOnlyList<DiscoveryFeedbackResult>>? training = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            training = Task.Run(() => store.LoadAll(), TestContext.Current.CancellationToken);
            await selecting.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(training.IsCompleted);
            release.Set();
            Assert.Null(await load.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Empty(await training.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            await load.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (training is not null) await training.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FullRetryQueueRestartsWithoutAdmittingOrAcknowledgingTheRejectedReceiptAndRecoversOnlyThatIdentity()
    {
        var fail = true;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WaitTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var gate = (System.Threading.Lock)typeof(DiscoveryFeedbackStore).GetField("_fileLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
        }, CancellationToken.None);
        var user = Guid.NewGuid();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var writes = Enumerable.Range(1, 64).Select(id => store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken)).ToArray();
            release.Set();
            Assert.All(await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), saved => Assert.False(saved));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            fail = false;
            Assert.False(await store.RecordRequestedAsync(user, 9999, "movie", TestContext.Current.CancellationToken));
            Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal(64, store.GetRequestedItems(user).Count);
            Assert.DoesNotContain((9999, "movie"), store.GetRequestedItems(user));
            Assert.Throws<IOException>(() => store.LoadAll());
            store.RecordDismissed(user, 9999, "movie");
            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.True(await store.RecordRequestedAsync(user, 9999, "tv", TestContext.Current.CancellationToken));
            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.True(await store.RecordRequestedAsync(Guid.NewGuid(), 9999, "movie", TestContext.Current.CancellationToken));
            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.False(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (9999, "movie") }, TestContext.Current.CancellationToken));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.True(await store.RecordRequestedAsync(user, 9999, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.NotEmpty(store.LoadAll());
            Assert.True(Assert.Single(store.LoadForUser(user)!.Entries, entry => entry.TmdbId == 9999 && entry.MediaType == "movie").WasWatched);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WatchedReceiptWaitsForItsFailedRequestAndReplaysWithTheDurableRequestWithoutInventingMissingKeys()
    {
        var fail = true;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        Assert.False(await store.RecordRequestedAsync(user, 600, "movie", TestContext.Current.CancellationToken));
        await CurrentWriter(store);
        Assert.False(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (600, "movie"), (99, "tv") }, TestContext.Current.CancellationToken));
        await CurrentWriter(store);
        Assert.Throws<IOException>(() => store.LoadAll());
        fail = false;
        Assert.True(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 500 }], DateTime.UtcNow, TestContext.Current.CancellationToken));
        await CurrentWriter(store);
        var entries = Assert.Single(store.LoadAll()).Entries;
        Assert.True(Assert.Single(entries, entry => entry.TmdbId == 600).WasWatched);
        Assert.DoesNotContain(entries, entry => entry.TmdbId == 99);
        Assert.True(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (98, "tv") }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(store.LoadForUser(user)!.Entries, entry => entry.TmdbId == 98);
    }

    [Fact]
    public async Task AnOlderDismissalRetryCannotReplaceTheTimestampOfANewerSuccessfulDismissal()
    {
        var fail = true;
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            ReceiptTimeProvider = clock,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        Assert.Throws<IOException>(() => store.RecordDismissed(user, 600, "movie"));
        clock.Now = clock.Now.AddMinutes(1);
        fail = false;
        store.RecordDismissed(user, 600, "movie");
        var newer = Assert.Single(store.LoadForUser(user)!.Entries).DismissedAtUtc;
        Assert.Equal(clock.Now, newer);
        Assert.True(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 500 }], DateTime.UtcNow, TestContext.Current.CancellationToken));
        Assert.Equal(newer, Assert.Single(store.LoadForUser(user)!.Entries, entry => entry.TmdbId == 600).DismissedAtUtc);
        Assert.Equal(newer, Assert.Single(CreateStore().LoadForUser(user)!.Entries, entry => entry.TmdbId == 600).DismissedAtUtc);
    }

    [Fact]
    public async Task TrainingSnapshotSelectionCannotReturnANewerPartialSnapshotAfterItsEarlierReadinessCheck()
    {
        var pause = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            BeforeTrainingSnapshotSelection = () =>
            {
                if (!pause) return;
                entered.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            },
            WriteFeedback = (path, contents) =>
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<List<DiscoveryFeedbackResult>>(contents, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options)!;
                if (data.SelectMany(user => user.Entries).Any(entry => entry.TmdbId == 600)) throw new IOException("request A unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.Parse("60000000-0000-0000-0000-000000000001");
        store.RecordGenerated(user, "user", [new DiscoveryRecommendation { TmdbId = 1 }]);
        pause = true;
        var read = Task.Run(() => store.LoadAll(), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(await store.RecordRequestedAsync(user, 600, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(store);
            Assert.True(await store.RecordRequestedAsync(user, 601, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(store);
            release.Set();
            await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    private static Task<bool> CurrentWriter(DiscoveryFeedbackStore store)
        => (Task<bool>)typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;

    private static HashSet<Guid> UncertainPreparedIntents(DiscoveryFeedbackStore store)
        => (HashSet<Guid>)typeof(DiscoveryFeedbackStore).GetField("_uncertainPreparedIntents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;

    private static int PreparedAttemptCount(DiscoveryFeedbackStore store)
        => (int)typeof(DiscoveryFeedbackStore).GetField("_preparedAttemptCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedExactImpressionPayloadOrDurableEarlierDuplicateRestoresReadiness(bool earlierImpression)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var firstAt = clock.Now;
        var fail = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("temporarily unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } };
        store.RecordGenerated(user, "viewer", [card], firstAt.AddMinutes(-1));
        fail = true;
        if (earlierImpression) Assert.Throws<IOException>(() => store.RecordImpressions(user, [card], firstAt.AddMinutes(-1)));
        foreach (var id in Enumerable.Range(1000, earlierImpression ? 63 : 64))
        {
            Assert.False(await store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        clock.Now = firstAt.AddMinutes(1);
        Assert.Throws<IOException>(() => store.RecordImpressions(user, [card], firstAt.AddMinutes(-1)));
        Assert.Throws<IOException>(() => store.LoadAll());
        fail = false;
        Assert.False(await store.RecordGeneratedAsync(user, "viewer", [new DiscoveryRecommendation { TmdbId = 900 }], clock.Now, TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries, row => row.TmdbId == 700);
        Assert.Equal(earlierImpression ? firstAt : clock.Now, entry.ImpressionAtUtc);
        Assert.Equal(.7, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(firstAt.AddMinutes(-1), entry.ShownAtUtc);
    }

    [Fact]
    public async Task DismissalHandoffCannotRetirePendingIdentityBetweenPublishedAndPendingServingReads()
    {
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWrite = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var stall = false;
        var pause = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            BeforeDismissalSnapshotSelection = () => { if (pause) { selecting.TrySetResult(); releaseRead.Wait(TestContext.Current.CancellationToken); } },
            WriteFeedback = (path, contents) =>
            {
                if (stall) { writing.TrySetResult(); releaseWrite.Wait(TestContext.Current.CancellationToken); }
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        store.RecordGenerated(user, "viewer", [new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv" }]);
        stall = true;
        var dismissal = Task.Run(() => store.RecordDismissed(user, 700, "tv"), CancellationToken.None);
        Task<IReadOnlySet<(int TmdbId, string MediaType)>>? read = null;
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            pause = true;
            read = Task.Run(() => store.GetDismissedItems(user), CancellationToken.None);
            await selecting.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            releaseWrite.Set();
            await dismissal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            releaseRead.Set();
            Assert.Contains((700, "tv"), await read.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            releaseWrite.Set();
            releaseRead.Set();
            await dismissal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (read is not null) await read.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FailedAtomicPersistenceKeepsKnownDurableExclusionsAndRetryRestoresThemWithThePendingReceipt()
    {
        var clock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var fail = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            RetryTimeProvider = clock,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("storage unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        store.RecordDismissed(user, 700, "tv");
        fail = true;
        Assert.Throws<IOException>(() => store.RecordDismissed(user, 800, "movie"));
        File.Delete(Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json"));
        Assert.Contains((700, "tv"), store.GetDismissedItems(user));
        Assert.Contains((800, "movie"), store.GetDismissedItems(user));
        Assert.Throws<IOException>(() => store.LoadAll());
        await clock.WaitForDeadlineAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        fail = false;
        clock.ExpireDeadlines();
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var restarted = CreateStore();
        Assert.Contains((700, "tv"), restarted.GetDismissedItems(user));
        Assert.Contains((800, "movie"), restarted.GetDismissedItems(user));
    }

    [Fact]
    public async Task OlderCardRequestRetryKeepsTheNewerRequestTimeAndItsAlreadyConfirmedPrediction()
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var fail = true;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            ReceiptTimeProvider = clock,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, Title = "old", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .1 } }, clock.Now.AddDays(-2));
        clock.Now = clock.Now.AddMinutes(1);
        var requested = clock.Now;
        var predictionAt = clock.Now.AddDays(-1);
        fail = false;
        store.RecordRequested(user, new DiscoveryRecommendation { TmdbId = 700, Title = "new", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .9 } }, predictionAt);
        Assert.True(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 701 }], clock.Now, TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(await store.RecordRequestedAsync(user, 700, "movie", TestContext.Current.CancellationToken));
        var saved = Assert.Single(CreateStore().LoadForUser(user)!.Entries, entry => entry.TmdbId == 700);
        Assert.Equal(requested, saved.RequestedAtUtc);
        Assert.Equal(predictionAt, saved.ShownAtUtc);
        Assert.Equal("new", saved.Title);
        Assert.Equal(.9, saved.ScoringFeatures!.GenreSimilarity);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task SynchronousRequestStartsTheWorkerForPromotedRetainedWatchesEvenWhenAllSlotsBecomeRunnable(int watchCount)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WaitTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var first = store.RecordRequestedAsync(user, 1, "movie", TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var queued = Enumerable.Range(2, 64).Select(id => store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken)).ToArray();
            Assert.False(await store.RecordRequestedAsync(user, 9000, "movie", TestContext.Current.CancellationToken));
            release.Set();
            Assert.All(await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), saved => Assert.True(saved));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            for (var index = 0; index < watchCount; index++)
            {
                Assert.False(await store.MarkWatchedAsync(user, new HashSet<(int, string)> { (9000, "movie") }, TestContext.Current.CancellationToken));
                await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }

            Assert.Throws<IOException>(() => store.LoadAll());
            store.RecordRequested(user, 9000, "movie");
            Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 9000).WasWatched);
            Assert.Empty(store.GetPendingRequestedItems(user));
        }
        finally
        {
            release.Set();
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AReplayWithTheRetainedLaterDismissalTimeCanRecoverBothIdentityOnlyActions()
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-3) };
        var original = clock.Now;
        var fail = true;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            ReceiptTimeProvider = clock,
            WaitTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        Assert.Throws<IOException>(() => store.RecordDismissed(user, 1, "movie"));
        var gate = (System.Threading.Lock)typeof(DiscoveryFeedbackStore).GetField("_fileLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
        }, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var pending = Enumerable.Range(2, 63).Select(id => store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken)).ToArray();
            release.Set();
            Assert.All(await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), saved => Assert.False(saved));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            clock.Now = original.AddMinutes(1);
            Assert.Throws<IOException>(() => store.RecordDismissed(user, 1, "movie"));
            fail = false;
            Assert.False(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 999 }], clock.Now, TestContext.Current.CancellationToken));
            Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var recovered = Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 1);
            Assert.Equal(clock.Now, recovered.DismissedAtUtc);
            Assert.Equal(original, recovered.ShownAtUtc);
            Assert.Null(recovered.ScoringFeatures);
            clock.Now = original.AddMinutes(2);
            store.RecordDismissed(user, 1, "movie");
            Assert.Equal(clock.Now, Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 1).DismissedAtUtc);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARejectedDuplicateIsRecoveredWhenTheOlderExactRequestOrWatchBecomesDurable(bool watched)
    {
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var original = clock.Now;
        var fail = false;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            ReceiptTimeProvider = clock,
            WaitTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        if (watched) store.RecordRequested(user, 1, "movie");
        fail = true;
        var gate = (System.Threading.Lock)typeof(DiscoveryFeedbackStore).GetField("_fileLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
        }, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var first = watched
                ? store.MarkWatchedAsync(user, new HashSet<(int, string)> { (1, "movie") }, TestContext.Current.CancellationToken)
                : store.RecordRequestedAsync(user, 1, "movie", TestContext.Current.CancellationToken);
            var pending = Enumerable.Range(2, 63).Select(id => store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken)).Prepend(first).ToArray();
            release.Set();
            Assert.All(await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), saved => Assert.False(saved));
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            clock.Now = original.AddMinutes(1);
            fail = false;
            var duplicate = watched
                ? store.MarkWatchedAsync(user, new HashSet<(int, string)> { (1, "movie") }, TestContext.Current.CancellationToken)
                : store.RecordRequestedAsync(user, 1, "movie", TestContext.Current.CancellationToken);
            Assert.False(await duplicate.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 1);
            Assert.Equal(original, entry.RequestedAtUtc);
            Assert.Equal(watched, entry.WasWatched);
            Assert.Equal(watched ? original : (DateTime?)null, entry.WatchedAtUtc);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ControlledWaitDeadlineCanExpireWithoutCancelingOrAcknowledgingTheStillPendingReceipt()
    {
        var clock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WaitTimeProvider = clock,
            WriteFeedback = (path, contents) =>
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var receipt = store.RecordRequestedAsync(user, 600, "movie", TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            clock.ExpireDeadlines();
            Assert.False(await receipt.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(CurrentWriter(store).IsCompleted);
            Assert.Throws<IOException>(() => store.LoadAll());
        }
        finally
        {
            release.Set();
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Contains((600, "movie"), store.GetRequestedItems(user));
        Assert.NotEmpty(store.LoadAll());
    }

    [Fact]
    public async Task SaturatedRejectionLedgerStaysBoundedAndCannotClaimRecoveryAfterLosingAnIdentity()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WaitTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = new List<Task<bool>> { store.RecordRequestedAsync(user, 1, "movie", cancellation.Token) };
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            for (var id = 2; id <= 65; id++) pending.Add(store.RecordRequestedAsync(user, id, "movie", cancellation.Token));
            for (var id = 10000; id <= 14096; id++) Assert.False(await store.RecordRequestedAsync(user, id, "movie", cancellation.Token));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var ledger = (System.Collections.IDictionary)typeof(DiscoveryFeedbackStore).GetField("_rejectedReceipts", flags)!.GetValue(store)!;
            Assert.Equal(4096, ledger.Count);
            Assert.True((bool)typeof(DiscoveryFeedbackStore).GetField("_untrackedRejection", flags)!.GetValue(store)!);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            release.Set();
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.True(await store.RecordRequestedAsync(user, 14096, "movie", TestContext.Current.CancellationToken));
            Assert.Throws<IOException>(() => store.LoadAll());
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    private sealed class ReceiptClock : TimeProvider
    {
        internal DateTime Now { get; set; }

        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("dismissal")]
    [InlineData("impression")]
    public async Task DelayedFirstReceiptRestoresOriginalPredictionInputsWithoutRenewingOutcomeOrVisibility(string action)
    {
        var retryClock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddDays(-10) };
        var acquired = clock.Now;
        var predicted = acquired.AddMinutes(-2);
        var laterPrediction = acquired.AddMinutes(-1);
        var fail = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), _tempDir)
        {
            ReceiptTimeProvider = clock, RetryTimeProvider = retryClock,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("test first receipt not yet durable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var first = new DiscoveryRecommendation
        {
            TmdbId = 700, MediaType = "tv", Score = .7, ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 },
            TitleMetadata = new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.TitleMetadataSnapshot { TmdbId = 700, MediaType = "tv", FetchedAtUtc = predicted, Overview = "first captured metadata" }
        };
        var later = first.Clone();
        later.Score = .1;
        later.ScoringFeatures!.GenreSimilarity = .1;
        later.TitleMetadata = first.TitleMetadata with { FetchedAtUtc = laterPrediction, Overview = "later captured metadata" };
        store.RecordGenerated(user, "test viewer", [first], predicted);
        fail = true;
        if (action == "request") store.RecordRequested(user, first, predicted);
        else if (action == "dismissal") Assert.Throws<IOException>(() => store.RecordDismissed(user, first, predicted));
        else Assert.Throws<IOException>(() => store.RecordImpressions(user, [first], predicted));
        Assert.Throws<IOException>(() => store.LoadAll());

        fail = false;
        clock.Now = acquired.AddDays(1);
        store.RecordImpressions(user, [later], laterPrediction);
        Assert.Equal(.1, Assert.Single(store.LoadForUser(user)!.Entries).ScoringFeatures!.GenreSimilarity);
        Assert.Throws<IOException>(() => store.LoadAll());
        Assert.True(await store.RecordGeneratedAsync(user, "test viewer", [new DiscoveryRecommendation { TmdbId = 999, MediaType = "movie" }], clock.Now, TestContext.Current.CancellationToken));
        await CurrentWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var result = CreateStore().LoadForUser(user)!;
        var saved = Assert.Single(result.Entries, entry => entry.TmdbId == 700 && entry.MediaType == "tv");
        Assert.Equal(.7, saved.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(.7, saved.Score);
        Assert.Equal(predicted, saved.ShownAtUtc);
        Assert.Equal(acquired, saved.PredictionEvidenceAtUtc);
        Assert.Equal(action == "impression" ? acquired : clock.Now, saved.ImpressionAtUtc);
        Assert.Equal(action == "request" ? acquired : null, saved.RequestedAtUtc);
        Assert.Equal(action == "dismissal" ? acquired : null, saved.DismissedAtUtc);
        Assert.Equal("first captured metadata", saved.TitleMetadata!.Overview);
        Assert.Equal(predicted, saved.TitleMetadata.FetchedAtUtc);
        var training = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([result], new Dictionary<Guid, UserWatchProfile>(), null, null, TestContext.Current.CancellationToken).Examples);
        Assert.Equal(acquired, training.GeneratedAtUtc);
        Assert.Equal(.7, training.Features.GenreSimilarity);
        Assert.Equal(action != "impression", training.IsObserved);
        Assert.Equal(action == "impression", training.IsPassiveSample);
        Assert.Equal(.7, first.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(.1, later.ScoringFeatures.GenreSimilarity);
    }

    [Fact]
    public async Task FailedCardActionRetryPreservesOriginalPredictionEvidenceAcquisitionAndSavedInputs()
    {
        var fail = true;
        var clock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-2) };
        var acquired = clock.Now;
        var generated = acquired.AddDays(-1);
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            ReceiptTimeProvider = clock,
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("offline");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var item = new DiscoveryRecommendation { TmdbId = 600, ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } };
        store.RecordRequested(user, item, generated);
        item.ScoringFeatures.GenreSimilarity = .1;
        clock.Now = acquired.AddMinutes(1);
        fail = false;
        Assert.True(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 500 }], clock.Now, TestContext.Current.CancellationToken));
        var saved = Assert.Single(CreateStore().LoadForUser(user)!.Entries, entry => entry.TmdbId == 600);
        Assert.Equal(generated, saved.ShownAtUtc);
        Assert.Equal(acquired, saved.RequestedAtUtc);
        Assert.Equal(acquired, saved.PredictionEvidenceAtUtc);
        Assert.Equal(.7, saved.ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public async Task ConsecutiveGenerationAcknowledgementsCannotStrandWorkDuringWriterIdleHandoff()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        for (var index = 1; index <= 100; index++)
        {
            Assert.True(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = index }], DateTime.UtcNow, TestContext.Current.CancellationToken));
        }

        Assert.Equal(100, store.LoadForUser(user)!.Entries.Count);
    }

    [Fact]
    public async Task ConfirmedFeedbackBackpressureBoundsTheQueueAndWithholdsIncompleteTrainingEvidence()
    {
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WaitTimeProvider = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider()
        };
        var gate = (System.Threading.Lock)typeof(DiscoveryFeedbackStore).GetField("_fileLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
        }, CancellationToken.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var user = Guid.NewGuid();
        var pending = new List<Task<bool>>();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            pending.Add(store.RecordRequestedAsync(user, 1, "movie", cancellation.Token));
            // One operation may already have left the bounded queue to acquire the lock.
            for (var index = 2; index <= 65; index++) pending.Add(store.RecordRequestedAsync(user, index, "movie", cancellation.Token));
            Assert.False(await store.RecordRequestedAsync(user, 66, "movie", cancellation.Token));
            await Assert.ThrowsAsync<IOException>(() => Task.Run(() => store.LoadAll(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var writer = (Task<bool>)typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.InRange(store.GetRequestedItems(user).Count, 64, 65);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedRequestsAndWatchUpdatesShareOneWriterWithoutBlockingCancellationOrUsingObsoleteTrainingOutcomes(bool cancel)
    {
        var user = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stall = false;
        var writes = 0;
        var receiptClock = new ReceiptClock { Now = DateTime.UtcNow.AddMinutes(-1) };
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            ReceiptTimeProvider = receiptClock,
            WaitTimeProvider = cancel ? new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider() : TimeProvider.System,
            WriteFeedback = (path, contents) =>
            {
                Interlocked.Increment(ref writes);
                if (stall)
                {
                    started.TrySetResult();
                    release.Wait(TestContext.Current.CancellationToken);
                }

                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        store.RecordRequested(user, 1, "movie");
        stall = true;
        var generation = store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 500 }], DateTime.UtcNow, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<bool>? writer = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            writer = (Task<bool>)typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var requestedAt = receiptClock.Now;
            var request = store.RecordRequestedAsync(user, 600, "tv", cancellation.Token);
            var watched = store.MarkWatchedAsync(user, new HashSet<(int, string)> { (1, "movie") }, cancellation.Token);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watched.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.All(await Task.WhenAll(request, watched).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), result => Assert.False(result));
            }

            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.Contains((1, "movie"), store.GetRequestedItems(user));
            Assert.Equal(2, writes);
            Assert.Same(writer, typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store));
            release.Set();
            Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var entries = Assert.Single(store.LoadAll()).Entries;
            Assert.True(Assert.Single(entries, entry => entry.TmdbId == 1).WasWatched);
            var requested = Assert.Single(entries, entry => entry.TmdbId == 600);
            Assert.Equal(requestedAt, requested.RequestedAtUtc);
            Assert.Equal("tv", requested.MediaType);
            Assert.Null(requested.ScoringFeatures);
        }
        finally
        {
            release.Set();
            if (writer is not null) await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await generation;
        }
    }

    [Fact]
    public async Task FailedConfirmedWritesRetainTheirReceiptForABoundedLaterRetryAndWithholdObsoleteOutcomeViews()
    {
        var user = Guid.NewGuid();
        var fail = true;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("storage unavailable");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var acquired = DateTime.UtcNow;
        Assert.False(await store.RecordRequestedAsync(user, 600, "movie", TestContext.Current.CancellationToken));
        var writer = (Task<bool>)typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        Assert.False(await writer);
        Assert.Throws<IOException>(() => store.LoadAll());
        fail = false;
        Assert.True(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 500 }], DateTime.UtcNow, TestContext.Current.CancellationToken));
        var request = Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 600);
        Assert.True(request.RequestedAtUtc >= acquired);
        Assert.Null(request.ScoringFeatures);
        var recorded = request.RequestedAtUtc;
        Assert.True(await store.RecordRequestedAsync(user, 600, "movie", TestContext.Current.CancellationToken));
        Assert.Equal(recorded, Assert.Single(store.LoadForUser(user)!.Entries, entry => entry.TmdbId == 600).RequestedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledGeneratedFeedbackDoesNotBlockCancellationOrConfirmedReadsAndCannotAccumulateWriters(bool cancel)
    {
        var user = Guid.NewGuid();
        var generated = DateTime.UtcNow.AddDays(-3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stall = false;
        var writes = 0;
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WaitTimeProvider = cancel ? new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider() : TimeProvider.System,
            WriteFeedback = (path, contents) =>
            {
                Interlocked.Increment(ref writes);
                if (stall)
                {
                    started.TrySetResult();
                    release.Wait(TestContext.Current.CancellationToken);
                }

                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        store.RecordRequested(user, 1, "movie");
        store.RecordDismissed(user, 2, "tv");
        stall = true;
        var original = new DiscoveryRecommendation { TmdbId = 500, Title = "original", Genres = ["Animation"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 } };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var recording = store.RecordGeneratedAsync(user, "user", [original], generated, cancellation.Token);
        var writer = (Task<bool>)typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            original.Title = "changed after dispatch";
            original.ScoringFeatures.GenreSimilarity = .1;
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.False(await recording.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }

            Assert.False(await store.RecordGeneratedAsync(user, "later", [new DiscoveryRecommendation { TmdbId = 600 }], DateTime.UtcNow, TestContext.Current.CancellationToken));
            Assert.Equal(3, writes);
            var reads = Task.Run(() =>
            {
                Assert.Contains((1, "movie"), store.GetRequestedItems(user));
                Assert.Contains((2, "tv"), store.GetDismissedItems(user));
                Assert.Equal(2, store.LoadForUser(user)!.Entries.Count);
                Assert.Equal(2, Assert.Single(store.LoadAll()).Entries.Count);
            }, TestContext.Current.CancellationToken);
            await reads.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(writer.IsCompleted);
        }
        finally
        {
            release.Set();
            Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }

        var entry = Assert.Single(CreateStore().LoadForUser(user)!.Entries, row => row.TmdbId == 500);
        Assert.Equal("original", entry.Title);
        Assert.Equal(generated, entry.ShownAtUtc);
        Assert.Null(entry.ScoringFeatures);
        Assert.False(entry.HasConfirmedPrediction);
        Assert.True(await store.RecordGeneratedAsync(user, "later", [new DiscoveryRecommendation { TmdbId = 600 }], generated, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AsyncGeneratedFeedbackValidatesInputAndDoesNotClaimFailedPersistence()
    {
        var user = Guid.NewGuid();
        using var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _tempDir)
        {
            WriteFeedback = (_, _) => throw new IOException("read only")
        };
        Assert.False(await store.RecordGeneratedAsync(user, "user", [new DiscoveryRecommendation { TmdbId = 500 }], DateTime.UtcNow, TestContext.Current.CancellationToken));
        Assert.Null(store.LoadForUser(user));
        Assert.True(await store.RecordGeneratedAsync(user, "user", [], null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RecordGeneratedAsync(user, null!, [], null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RecordGeneratedAsync(user, "user", null!, null, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RecordGeneratedAsync(user, "user", [], null, cancellation.Token));
    }

    [Fact]
    public void ConfirmedRequestRetainsUnderlyingMetadataAndTheExactPredictionThroughRefreshReloadAndTraining()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        var generated = DateTime.UtcNow.AddDays(-2);
        var metadata = new TitleMetadataSnapshot { TmdbId = 700, MediaType = "tv", Keywords = ["anime", "pirate", "friendship"], KeywordReferences = [new(42, "pirate"), new(43, "friendship")], OriginalLanguage = "ja", FetchedAtUtc = generated.AddMinutes(-1) };
        var features = new CandidateFeatures { IsExternalCandidate = true, HasGenreEvidence = true, GenreSimilarity = .7, ContentFitSupport = .78, ContentTasteKey = Guid.NewGuid() };
        var item = new DiscoveryRecommendation { TmdbId = 700, MediaType = "tv", Genres = ["Animation"], Popularity = 50, TitleMetadata = metadata, ScoringFeatures = features };
        store.RecordGenerated(user, "synthetic user", [item], generated);
        Assert.Null(Assert.Single(store.LoadForUser(user)!.Entries).TitleMetadata);
        store.RecordRequested(user, item, generated);
        item.TitleMetadata = metadata with { Keywords = ["later data"] };
        item.ScoringFeatures!.GenreSimilarity = .1;
        store.RecordGenerated(user, "synthetic user", [item], DateTime.UtcNow);
        var feedback = CreateStore().LoadAll();
        var entry = Assert.Single(Assert.Single(feedback).Entries);
        Assert.Equal("ja", entry.TitleMetadata!.OriginalLanguage);
        Assert.Equal(["anime", "pirate", "friendship"], entry.TitleMetadata.Keywords!.Value);
        Assert.Equal(2, entry.TitleMetadata.KeywordReferences!.Value.Length);
        Assert.Equal(.7, entry.ScoringFeatures!.GenreSimilarity);
        var example = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(feedback, new Dictionary<Guid, UserWatchProfile>(), null, null, TestContext.Current.CancellationToken).Examples);
        Assert.Equal(EngineConstants.DiscoveryRequestedLabel, example.Label);
        Assert.True(example.IsObserved);
        Assert.Equal(entry.ScoringFeatures.ToVector(), example.Features.ToVector());
        Assert.Equal(entry.ScoringFeatures.ContentTasteKey, example.Features.ContentTasteKey);
    }

    [Fact]
    public void Backfill_RecordsObservedEmptyCreditsAndDoesNotReplaceThemWithLaterPeople()
    {
        var store = CreateStore();
        var user = Guid.NewGuid();
        var item = new DiscoveryRecommendation { TmdbId = 700, Title = "Title", KnownPeople = null };
        store.RecordShown(user, "user", [item]);
        Assert.Null(Assert.Single(store.LoadForUser(user)!.Entries).KnownPeople);
        item.KnownPeople = [];
        store.RecordShown(user, "user", [item]);
        Assert.Empty(Assert.IsType<IReadOnlyList<string>>(Assert.Single(CreateStore().LoadForUser(user)!.Entries).KnownPeople, exactMatch: false));
        item.KnownPeople = ["Later actor"];
        store.RecordShown(user, "user", [item]);
        Assert.Empty(Assert.IsType<IReadOnlyList<string>>(Assert.Single(CreateStore().LoadForUser(user)!.Entries).KnownPeople, exactMatch: false));
    }

    [Theory]
    [InlineData("missing", 0.6)]
    [InlineData("empty", 0.0)]
    [InlineData("nonmatch", 0.0)]
    [InlineData("match", 1.0)]
    public void PersistedCredits_RetainUnknownVersusObservedEvidenceThroughWritesClonesReloadsAndTraining(string state, double expected)
    {
        var user = Guid.NewGuid();
        var shown = DateTime.UtcNow.AddDays(-2);
        IReadOnlyList<string>? people = state switch { "missing" => null, "empty" => [], "match" => ["Keanu Reeves"], _ => ["Other actor"] };
        var store = CreateStore();
        var item = new DiscoveryRecommendation { TmdbId = 700, MediaType = "movie", Title = "Title", Genres = ["Action"], KnownPeople = people, Popularity = 50 };
        store.RecordGenerated(user, "user", [item], shown);
        Assert.Equal(people, Assert.Single(store.LoadForUser(user)!.Entries).KnownPeople);
        store.RecordRequested(user, item, shown); // Confirm metadata without a stored numerical snapshot.
        var profile = new UserWatchProfile { UserId = user, WatchedItems = new System.Collections.ObjectModel.Collection<WatchedItemInfo>(Enumerable.Range(0, 3).Select(_ => new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = shown.AddDays(-1), Genres = ["Action"], PeopleNames = ["Keanu Reeves"]
        }).ToList()) };
        foreach (var watched in profile.WatchedItems)
        {
            watched.Credits = new CreditMetadataSnapshot
            {
                SourceItemId = watched.ItemId,
                Cast = new CreditField { Complete = true, AcquiredAtUtc = watched.LastPlayedDate, People = [new() { Name = "Keanu Reeves", Kind = Jellyfin.Data.Enums.PersonKind.Actor }] },
                Crew = new CreditField { Complete = true, AcquiredAtUtc = watched.LastPlayedDate, People = [] }
            };
        }
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var means = Enumerable.Repeat(0.6, CandidateFeatures.FeatureCount).ToArray();
        var candidate = new TmdbDiscoverItem { Id = 700, MediaType = "movie", GenreIds = [28], KnownPeople = people?.ToList() };
        var inference = ExternalCandidateFeatureBuilder.Build(candidate, preferences, ["Keanu Reeves"], 0,
            PreferenceBuilder.BuildGenreExposureAnalysis(preferences, profile), profile, means);
        foreach (var feedback in new[] { store, CreateStore() }.Select(reader => reader.LoadAll()))
        {
            var entry = Assert.Single(Assert.Single(feedback).Entries);
            Assert.Equal(people, entry.KnownPeople);
            Assert.True(entry.HasConfirmedPrediction);
            Assert.Null(entry.ScoringFeatures);
            var features = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(feedback,
                new Dictionary<Guid, UserWatchProfile> { [user] = profile }, null, means, TestContext.Current.CancellationToken).Examples).Features;
            Assert.Equal(state != "missing", features.HasPeopleMetadata);
            Assert.Equal(state != "missing", features.IsExternalFeatureKnown(FeatureIndex.PeopleSimilarity));
            Assert.Equal(expected, features.PeopleSimilarity);
            Assert.Equal(inference.PeopleSimilarity, features.PeopleSimilarity);
            Assert.Equal(inference.ToModelVector(means)[(int)FeatureIndex.PeopleGenreInteraction], features.ToModelVector(means)[(int)FeatureIndex.PeopleGenreInteraction]);
        }
        if (people is not null)
        {
            var exposed = Assert.IsType<IList<string>>(Assert.Single(store.LoadForUser(user)!.Entries).KnownPeople, exactMatch: false);
            exposed.Add("Client-only actor");
            Assert.Equal(people, Assert.Single(store.LoadForUser(user)!.Entries).KnownPeople);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyFeedback_MissingCreditsPropertyIsUnknownButExplicitEmptyCreditsRemainObserved(bool explicitEmpty)
    {
        var user = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var credits = explicitEmpty ? ",\"KnownPeople\":[]" : string.Empty;
        File.WriteAllText(Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json"),
            $$"""[{"UserId":"{{user}}","Entries":[{"TmdbId":700,"MediaType":"movie","ShownAtUtc":"{{now:O}}","RequestedAtUtc":"{{now:O}}","Genres":["Action"]{{credits}}}]}]""");
        var feedback = CreateStore().LoadAll();
        var entry = Assert.Single(Assert.Single(feedback).Entries);
        if (explicitEmpty) Assert.Empty(Assert.IsType<IReadOnlyList<string>>(entry.KnownPeople, exactMatch: false));
        else Assert.Null(entry.KnownPeople);
        var means = Enumerable.Repeat(0.6, CandidateFeatures.FeatureCount).ToArray();
        var features = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(feedback,
            new Dictionary<Guid, UserWatchProfile>(), null, means, TestContext.Current.CancellationToken).Examples).Features;
        Assert.Equal(explicitEmpty, features.HasPeopleMetadata);
        Assert.Equal(explicitEmpty, features.IsExternalFeatureKnown(FeatureIndex.PeopleSimilarity));
        Assert.Equal(explicitEmpty ? 0.0 : 0.6, features.PeopleSimilarity);
    }

    [Fact]
    public void MissingFeedbackCredits_DoNotRewriteAConfirmedLegacyPredictionSnapshot()
    {
        var user = Guid.NewGuid();
        var stored = new CandidateFeatures { IsExternalCandidate = true, GenreSimilarity = 0.7, PeopleSimilarity = 0.23 };
        var item = new DiscoveryRecommendation { TmdbId = 700, KnownPeople = null, ScoringFeatures = stored };
        var store = CreateStore();
        store.RecordRequested(user, item, DateTime.UtcNow.AddDays(-2));
        var feedback = CreateStore().LoadAll();
        Assert.Null(Assert.Single(Assert.Single(feedback).Entries).KnownPeople);
        var example = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(feedback,
            new Dictionary<Guid, UserWatchProfile>(), null, Enumerable.Repeat(0.6, CandidateFeatures.FeatureCount).ToArray(), TestContext.Current.CancellationToken).Examples);
        Assert.Null(example.Features.HasPeopleMetadata);
        Assert.Equal(stored.ToVector(), example.Features.ToVector());
        Assert.NotSame(stored, example.Features);
    }

    [Fact]
    public void RecordShown_SameTmdbId_DifferentMediaType_CreatesSeparateEntries()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        var items = new List<DiscoveryRecommendation>
        {
            new()
            {
                TmdbId = 550,
                MediaType = "movie",
                Title = "Fight Club",
                Year = 1999,
                Genres = ["Drama"],
                TmdbRating = 8.4,
                Score = 0.9
            },
            new()
            {
                TmdbId = 550,
                MediaType = "tv",
                Title = "The Fixer",
                Year = 2008,
                Genres = ["Crime"],
                TmdbRating = 6.5,
                Score = 0.7
            }
        };

        store.RecordShown(userId, "TestUser", items);

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Entries.Count);

        var movieEntry = result.Entries.First(e => e.MediaType == "movie");
        var tvEntry = result.Entries.First(e => e.MediaType == "tv");

        Assert.Equal("Fight Club", movieEntry.Title);
        Assert.Equal("The Fixer", tvEntry.Title);
        Assert.Equal(550, movieEntry.TmdbId);
        Assert.Equal(550, tvEntry.TmdbId);
    }

    [Fact]
    public void RecordDismissed_OnlyAffectsMatchingMediaType()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        // Show both movie and TV with same TMDb ID
        var items = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 550, MediaType = "movie", Title = "Fight Club" },
            new() { TmdbId = 550, MediaType = "tv", Title = "The Fixer" }
        };
        store.RecordShown(userId, "TestUser", items);

        // Dismiss only the movie
        store.RecordDismissed(userId, 550, "movie");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);

        var movieEntry = result!.Entries.First(e => e.MediaType == "movie");
        var tvEntry = result.Entries.First(e => e.MediaType == "tv");

        Assert.NotNull(movieEntry.DismissedAtUtc);
        Assert.Null(tvEntry.DismissedAtUtc);
    }

    [Fact]
    public void RecordRequested_OnlyAffectsMatchingMediaType()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        var items = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 550, MediaType = "movie", Title = "Fight Club" },
            new() { TmdbId = 550, MediaType = "tv", Title = "The Fixer" }
        };
        store.RecordShown(userId, "TestUser", items);

        // Request only the TV show
        store.RecordRequested(userId, 550, "tv");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);

        var movieEntry = result!.Entries.First(e => e.MediaType == "movie");
        var tvEntry = result.Entries.First(e => e.MediaType == "tv");

        Assert.Null(movieEntry.RequestedAtUtc);
        Assert.NotNull(tvEntry.RequestedAtUtc);
    }

    [Fact]
    public void MarkWatched_OnlyAffectsMatchingCompositeKey()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        var items = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 550, MediaType = "movie", Title = "Fight Club" },
            new() { TmdbId = 550, MediaType = "tv", Title = "The Fixer" }
        };
        store.RecordShown(userId, "TestUser", items);

        // Request both
        store.RecordRequested(userId, 550, "movie");
        store.RecordRequested(userId, 550, "tv");

        // Mark only the movie as watched
        var watchedItems = new HashSet<(int TmdbId, string MediaType)> { (550, "movie") };
        store.MarkWatched(userId, watchedItems);

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);

        var movieEntry = result!.Entries.First(e => e.MediaType == "movie");
        var tvEntry = result.Entries.First(e => e.MediaType == "tv");

        Assert.True(movieEntry.WasWatched);
        Assert.False(tvEntry.WasWatched);
    }

    [Fact]
    public void RecordShown_DuplicateSameTmdbAndMediaType_DoesNotCreateDuplicate()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        var items = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 123, MediaType = "movie", Title = "Original" }
        };
        store.RecordShown(userId, "TestUser", items);

        // Show again with same key
        var items2 = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 123, MediaType = "movie", Title = "Duplicate" }
        };
        store.RecordShown(userId, "TestUser", items2);

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal("Original", result.Entries[0].Title);
    }

    [Fact]
    public void RecordDismissed_MixedCaseMediaType_NormalizesToLowercase()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        // Show an item with lowercase media type
        var items = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 999, MediaType = "movie", Title = "Test Movie" }
        };
        store.RecordShown(userId, "TestUser", items);

        // Dismiss with mixed-case media type - should still match
        store.RecordDismissed(userId, 999, "Movie");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.NotNull(result.Entries[0].DismissedAtUtc);
    }

    [Fact]
    public void RecordRequested_MixedCaseMediaType_NormalizesToLowercase()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        // Show an item with lowercase media type
        var items = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 888, MediaType = "tv", Title = "Test TV" }
        };
        store.RecordShown(userId, "TestUser", items);

        // Request with uppercase media type - should still match
        store.RecordRequested(userId, 888, "TV");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.NotNull(result.Entries[0].RequestedAtUtc);
    }

    [Fact]
    public void RecordDismissed_WhitespaceMediaType_NormalizesCorrectly()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        // Show an item
        var items = new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 777, MediaType = "movie", Title = "Whitespace Test" }
        };
        store.RecordShown(userId, "TestUser", items);

        // Dismiss with whitespace-padded media type - should still match after normalization
        store.RecordDismissed(userId, 777, " movie ");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.NotNull(result.Entries[0].DismissedAtUtc);
    }

    [Fact]
    public void RecordShown_PersistsToDisk_RoundTripThroughFreshStoreInstance()
    {
        // Write data through one store instance
        var store1 = CreateStore();
        var userId = Guid.NewGuid();

        var items = new List<DiscoveryRecommendation>
        {
            new()
            {
                TmdbId = 42,
                MediaType = "movie",
                Title = "Persistence Test",
                Year = 2024,
                Genres = ["Sci-Fi", "Action"],
                TmdbRating = 7.8,
                Score = 0.85,
                KnownPeople = ["Actor A", "Director B"]
            },
            new()
            {
                TmdbId = 42,
                MediaType = "tv",
                Title = "TV Persistence Test",
                Year = 2023,
                Genres = ["Drama"],
                TmdbRating = 8.1,
                Score = 0.72
            }
        };
        store1.RecordShown(userId, "PersistUser", items);

        // Create a completely new store instance pointing to the same directory.
        // This forces a full JSON deserialization from disk (fresh _memoryCache = null).
        var store2 = CreateStore();

        var result = store2.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        // UserName is [JsonIgnore] (PII not persisted to disk) - expected to be empty after round-trip.
        Assert.Equal(string.Empty, result.UserName);
        Assert.Equal(2, result.Entries.Count);

        var movieEntry = result.Entries.First(e => e.MediaType == "movie");
        Assert.Equal(42, movieEntry.TmdbId);
        Assert.Equal("Persistence Test", movieEntry.Title);
        Assert.Equal(2024, movieEntry.Year);
        Assert.Equal(7.8, movieEntry.TmdbRating);
        Assert.Equal(0.85, movieEntry.Score);
        Assert.NotNull(movieEntry.KnownPeople);
        Assert.Contains("Actor A", movieEntry.KnownPeople);
        Assert.Contains("Director B", movieEntry.KnownPeople);

        var tvEntry = result.Entries.First(e => e.MediaType == "tv");
        Assert.Equal(42, tvEntry.TmdbId);
        Assert.Equal("TV Persistence Test", tvEntry.Title);
        Assert.Equal(2023, tvEntry.Year);
    }

    // Guard-clause tests: empty inputs / invalid IDs must not persist

    [Fact]
    public void RecordShown_EmptyItemsList_DoesNotCreateUserEntry()
    {
        // Empty items list must not create a phantom user entry that later
        // pollutes LoadAll() results or serializes an empty user to disk.
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordShown(userId, "TestUser", new List<DiscoveryRecommendation>());

        Assert.Null(store.LoadForUser(userId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RecordDismissed_InvalidTmdbId_IsSilentlyIgnored(int tmdbId)
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, tmdbId, "movie");

        Assert.Null(store.LoadForUser(userId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RecordRequested_InvalidTmdbId_IsSilentlyIgnored(int tmdbId)
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordRequested(userId, tmdbId, "movie");

        Assert.Null(store.LoadForUser(userId));
    }

    [Fact]
    public void MarkWatched_EmptySet_IsNoOp()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie" }
        });
        store.RecordRequested(userId, 1, "movie");

        // Passing an empty set must not throw and must not mutate the entry.
        store.MarkWatched(userId, new HashSet<(int TmdbId, string MediaType)>());

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.False(result!.Entries[0].WasWatched);
    }

    [Fact]
    public void MarkWatched_UnknownUser_IsNoOpAndDoesNotCreateUser()
    {
        var store = CreateStore();
        var unknownUser = Guid.NewGuid();

        store.MarkWatched(
            unknownUser,
            new HashSet<(int TmdbId, string MediaType)> { (1, "movie") });

        Assert.Null(store.LoadForUser(unknownUser));
    }

    [Fact]
    public void MarkWatched_OnlyMarksItemsThatWereRequested()
    {
        // MarkWatched must only mark entries that have RequestedAtUtc.
        // A "shown but not requested" item must NOT be tagged as watched.
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 100, MediaType = "movie" }
        });

        // Note: no RecordRequested call. Ergo the entry cannot be "watched" in this model.
        store.MarkWatched(
            userId,
            new HashSet<(int TmdbId, string MediaType)> { (100, "movie") });

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.False(result!.Entries[0].WasWatched);
        Assert.Null(result.Entries[0].WatchedAtUtc);
    }

    [Fact]
    public void MarkWatched_IsIdempotent_DoesNotReRunUpdate()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie" }
        });
        store.RecordRequested(userId, 1, "movie");

        var watched = new HashSet<(int TmdbId, string MediaType)> { (1, "movie") };
        store.MarkWatched(userId, watched);
        var persisted = store.LoadForUser(userId)!;
        Assert.True(persisted.Entries[0].WasWatched);
        Assert.NotNull(persisted.Entries[0].WatchedAtUtc);

        // Seed a distinct, recent timestamp through the existing persistence seam.
        // Reload because LoadForUser returns a defensive copy, not the cached entry.
        var firstTimestamp = DateTime.UtcNow.AddDays(-1);
        persisted.Entries[0].WatchedAtUtc = firstTimestamp;
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        File.WriteAllText(filePath, System.Text.Json.JsonSerializer.Serialize(new[] { persisted }));
        store = CreateStore();
        Assert.Equal(firstTimestamp, store.LoadForUser(userId)!.Entries[0].WatchedAtUtc);

        store.MarkWatched(userId, watched);
        store.MarkWatched(userId, watched);
        var secondTimestamp = store.LoadForUser(userId)!.Entries[0].WatchedAtUtc;

        // The second call must not update WatchedAtUtc, proving the WasWatched short-circuit.
        Assert.Equal(firstTimestamp, secondTimestamp);
    }

    // RecordShown backfill: existing placeholder entries are enriched, not replaced

    [Fact]
    public void RecordShown_BackfillsAllMetadataOnPlaceholderEntry()
    {
        // A placeholder entry created by RecordDismissed carries only TmdbId/MediaType. A later RecordShown must backfill Title, Year, Genres, TmdbRating, Popularity, Score, and KnownPeople onto that same entry (no duplicate row).
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, 200, "movie"); // Creates placeholder

        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new()
            {
                TmdbId = 200,
                MediaType = "movie",
                Title = "Enriched",
                Year = 2024,
                Genres = ["Drama", "Comedy"],
                TmdbRating = 7.5,
                Popularity = 42.0,
                Score = 0.88,
                KnownPeople = ["Actor X"]
            }
        });

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        var entry = result.Entries[0];
        Assert.Equal("Enriched", entry.Title);
        Assert.Equal(2024, entry.Year);
        Assert.Equal(2, entry.Genres.Count);
        Assert.Equal(7.5, entry.TmdbRating);
        Assert.Equal(42.0, entry.Popularity);
        Assert.Equal(0.88, entry.Score);
        Assert.NotNull(entry.KnownPeople);
        Assert.Contains("Actor X", entry.KnownPeople);
        // Placeholder DismissedAtUtc must remain intact (not overwritten by RecordShown).
        Assert.NotNull(entry.DismissedAtUtc);
    }

    [Theory]
    [InlineData(0.0, true, false)]
    [InlineData(0.0, true, true)]
    [InlineData(1e-12, false, false)]
    [InlineData(double.Epsilon, false, false)]
    public void RecordShown_OnlyExactZeroNumericSentinelsAreBackfilled(double existingValue, bool shouldBackfill, bool negate)
    {
        existingValue = negate ? -existingValue : existingValue;
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "User", [new DiscoveryRecommendation
        {
            TmdbId = 300,
            MediaType = "movie",
            TmdbRating = existingValue,
            Score = existingValue,
            Popularity = existingValue
        }]);

        store.RecordShown(userId, "User", [new DiscoveryRecommendation
        {
            TmdbId = 300,
            MediaType = "movie",
            TmdbRating = 7.5,
            Score = 0.8,
            Popularity = 42.0
        }]);

        var entry = Assert.Single(store.LoadForUser(userId)!.Entries);
        Assert.Equal(shouldBackfill ? 7.5 : existingValue, entry.TmdbRating);
        Assert.Equal(shouldBackfill ? 0.8 : existingValue, entry.Score);
        Assert.Equal(shouldBackfill ? 42.0 : existingValue, entry.Popularity);
    }

    [Fact]
    public void RecordShown_DoesNotOverwriteAlreadyPopulatedFields()
    {
        // Backfill must only fill EMPTY fields. If Title is already
        // "Original", a second RecordShown with Title="Updated" must NOT overwrite it.
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new()
            {
                TmdbId = 300,
                MediaType = "movie",
                Title = "Original",
                Year = 2020,
                TmdbRating = 8.0
            }
        });

        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new()
            {
                TmdbId = 300,
                MediaType = "movie",
                Title = "Updated",
                Year = 2025,
                TmdbRating = 9.0
            }
        });

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal("Original", result.Entries[0].Title);
        Assert.Equal(2020, result.Entries[0].Year);
        Assert.Equal(8.0, result.Entries[0].TmdbRating);
    }

    // Lookup helpers: GetDismissedItems / GetRequestedItems / LoadAll

    [Fact]
    public void GetDismissedItems_ReturnsOnlyDismissedComposites()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 10, MediaType = "movie" },
            new() { TmdbId = 11, MediaType = "movie" },
            new() { TmdbId = 12, MediaType = "tv" }
        });
        store.RecordDismissed(userId, 10, "movie");
        store.RecordDismissed(userId, 12, "tv");

        var dismissed = store.GetDismissedItems(userId);

        Assert.Equal(2, dismissed.Count);
        Assert.Contains((10, "movie"), dismissed);
        Assert.Contains((12, "tv"), dismissed);
        Assert.DoesNotContain((11, "movie"), dismissed);
    }

    [Fact]
    public void GetDismissedItems_UnknownUser_ReturnsEmpty()
    {
        var store = CreateStore();
        Assert.Empty(store.GetDismissedItems(Guid.NewGuid()));
    }

    [Fact]
    public void GetRequestedItems_ReturnsOnlyRequestedComposites()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 20, MediaType = "movie" },
            new() { TmdbId = 21, MediaType = "movie" }
        });
        store.RecordRequested(userId, 21, "movie");

        var requested = store.GetRequestedItems(userId);

        Assert.Single(requested);
        Assert.Contains((21, "movie"), requested);
        Assert.DoesNotContain((20, "movie"), requested);
    }

    [Fact]
    public void GetRequestedItems_UnknownUser_ReturnsEmpty()
    {
        var store = CreateStore();
        Assert.Empty(store.GetRequestedItems(Guid.NewGuid()));
    }

    [Fact]
    public void LoadAll_ReturnsAllUsersDefensiveCopy()
    {
        // LoadAll must return a defensive copy so external mutation cannot poison the store.
        var store = CreateStore();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        store.RecordShown(u1, "User1", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie" }
        });
        store.RecordShown(u2, "User2", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 2, MediaType = "tv" }
        });

        var all = store.LoadAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.UserId == u1);
        Assert.Contains(all, r => r.UserId == u2);

        // Prove defensive copy: mutating the returned list must not affect the store.
        var returnedU1 = all.First(r => r.UserId == u1);
        returnedU1.Entries.Clear();
        returnedU1.Entries.Add(
            new DiscoveryFeedbackEntry { TmdbId = 9999, MediaType = "movie" });

        var fresh = store.LoadAll();
        // Original entries must still be present for u1 (the mutation target).
        var freshU1 = fresh.First(r => r.UserId == u1);
        Assert.Contains(freshU1.Entries, e => e.TmdbId == 1);
        Assert.DoesNotContain(freshU1.Entries, e => e.TmdbId == 9999);

        // And u2 is untouched too - a defensive-copy regression that only affected
        // its own record would sneak past a u1-only assertion.
        var freshU2 = fresh.First(r => r.UserId == u2);
        Assert.Contains(freshU2.Entries, e => e.TmdbId == 2);
    }

    [Fact]
    public void LoadAll_EmptyStore_ReturnsEmptyList()
    {
        var store = CreateStore();
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void LoadForUser_DefensiveCopy_MutationDoesNotAffectStore()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new()
            {
                TmdbId = 500,
                MediaType = "movie",
                Title = "Original",
                Genres = ["Drama"],
                KnownPeople = ["Actor"]
            }
        });

        var snapshot = store.LoadForUser(userId);
        Assert.NotNull(snapshot);
        // Mutate the returned copy
        snapshot!.Entries[0].Title = "Mutated";
        snapshot.Entries.Clear();

        // The store must still have the untouched original.
        var fresh = store.LoadForUser(userId);
        Assert.NotNull(fresh);
        Assert.Single(fresh!.Entries);
        Assert.Equal("Original", fresh.Entries[0].Title);
    }

    // File-corruption / oversize handling

    [Fact]
    public void LoadForUser_CorruptedJsonFile_ReturnsNullAndDeletesFile()
    {
        // Reveals: an unparseable JSON file must be treated as absent (return empty)
        // and the corrupted file must be deleted so subsequent writes start clean.
        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryFeedbackStore>>();

        // Seed a corrupted file BEFORE creating the store (so the memory cache doesn't
        // hide the disk read path).
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        File.WriteAllText(filePath, "{ not valid json ]]]");

        using var store = new DiscoveryFeedbackStore(pluginLog.Object, logger.Object, _tempDir);

        var result = store.LoadForUser(Guid.NewGuid());

        Assert.Null(result);
        // Corrupted file must have been deleted.
        Assert.False(File.Exists(filePath));
        // Warning must have been logged.
        pluginLog.Verify(
            p => p.LogWarning("DiscoveryFeedback", It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<ILogger>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void LoadForUser_CorruptedJsonFile_DeleteFails_DegradesGracefullyWithoutThrowing()
    {
        // The corrupt-file recovery deletes the poisoned file, but the delete itself is best-effort: if the OS refuses (a handle is open without FileShare.Delete), the store must swallow the IOException rather than surface it to the caller.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        File.WriteAllText(filePath, "{ not valid json ]]]");

        // Forcing the best-effort File.Delete to fail relies on a mandatory file lock: an open handle without FileShare.Delete makes File.Delete throw IOException on Windows.
        var store = CreateStore();

        if (!OperatingSystem.IsWindows())
        {
            var linuxEx = Record.Exception(() => store.LoadForUser(Guid.NewGuid()));
            Assert.Null(linuxEx);
            Assert.Null(store.LoadForUser(Guid.NewGuid()));
            return;
        }

        // Grant the read the store needs (ReadAllText) but withhold FileShare.Delete so
        // the subsequent File.Delete inside TryDeleteFile fails.
        using var handle = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Record.Exception(() => store.LoadForUser(Guid.NewGuid()));

        // Delete failure is swallowed, never propagated.
        Assert.Null(ex);
        // Store degraded to empty.
        Assert.Null(store.LoadForUser(Guid.NewGuid()));
        // The file is still present, proving the delete genuinely failed and the catch body ran.
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void RecordShown_AfterCorruptedFile_StartsClean()
    {
        // After the corrupted-file recovery path runs, subsequent writes must work.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        File.WriteAllText(filePath, "corrupted-not-json");

        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie", Title = "PostRecovery" }
        });

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal("PostRecovery", result.Entries[0].Title);
    }

    // Isolation invariant: repeated construction with same directory does not
    // clobber previously-written data.

    [Fact]
    public void MultipleStoreInstances_ShareTheSameFile_ConsistentReads()
    {
        // Confirms disk-backed isolation: two stores over the same folder see the same
        // state after a save + re-read cycle, which is what the plugin process relies
        // on across restarts.
        var store1 = CreateStore();
        var userId = Guid.NewGuid();
        store1.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 42, MediaType = "movie", Title = "Persisted" }
        });

        var store2 = CreateStore();
        var seenByStore2 = store2.LoadForUser(userId);
        Assert.NotNull(seenByStore2);
        Assert.Equal("Persisted", seenByStore2!.Entries[0].Title);

        // Mutate through the second instance...
        store2.RecordRequested(userId, 42, "movie");

        // ...and prove the change is visible through a *third* freshly-constructed store.
        var store3 = CreateStore();
        var seenByStore3 = store3.LoadForUser(userId);
        Assert.NotNull(seenByStore3);
        Assert.NotNull(seenByStore3!.Entries[0].RequestedAtUtc);
    }

    // GetOrCreateUserResult: UserName update

    [Fact]
    public void RecordShown_UpdatesUserNameOnSubsequentCall()
    {
        // If the user renames themselves in Jellyfin, the next RecordShown call must
        // update the UserName rather than sticking to the stale name.
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.RecordShown(userId, "Alice", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie" }
        });

        store.RecordShown(userId, "AliceRenamed", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 2, MediaType = "movie" }
        });

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Equal("AliceRenamed", result!.UserName);
    }

    // Bounds enforcement: MaxFileSizeBytes / MaxEntriesPerUser / MaxEntryAgeDays These branches guard against three separate DoS-style corruption modes: 1.

    [Fact]
    public void LoadForUser_OversizedValidEmptyFile_IsCompactedRatherThanDeleted()
    {
        // Any file larger than MaxFileSizeBytes (30 MB) must be treated as poisoned - deserializing a 30 MB+ JSON payload can OOM small deployments (e.g.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        // Write just over the 30 MB threshold. We use a valid JSON array header followed by filler so the code hits the size check BEFORE the deserializer.
        var padSize = (30 * 1024 * 1024) + 1024;
        using (var stream = File.Create(filePath))
        {
            // Prefix with a valid empty-array so a code path that skipped the size guard would still deserialize cleanly - makes the test specifically prove the size-guard fires FIRST, not the JSON parser bailing on garbage input.
            stream.Write("[]"u8);
            var padding = new byte[8192];
            Array.Fill(padding, (byte)' ');
            var written = 2;
            while (written < padSize)
            {
                var chunk = Math.Min(padding.Length, padSize - written);
                stream.Write(padding, 0, chunk);
                written += chunk;
            }
        }

        var pluginLog = new Mock<IPluginLogService>();
        var warningCount = 0;
        pluginLog.Setup(p => p.LogWarning(
                "DiscoveryFeedback",
                It.Is<string>(m => m.Contains("exceeds", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()))
            .Callback(() => warningCount++);

        using var store = new DiscoveryFeedbackStore(
            pluginLog.Object,
            new Mock<ILogger<DiscoveryFeedbackStore>>().Object,
            _tempDir);

        var result = store.LoadForUser(Guid.NewGuid());

        Assert.Null(result);
        Assert.True(File.Exists(filePath), "valid oversized feedback must be recovered, not deleted");
        Assert.True(new FileInfo(filePath).Length <= 30 * 1024 * 1024);
        Assert.Equal(1, warningCount);
    }

    [Fact]
    public void SaveInternal_UserWithMoreThanMaxEntries_KeepsOnlyMostRecent200()
    {
        // Cap at MaxEntriesPerUser=200. A user who accumulates 250 shown items must have the oldest-by-activity 50 evicted, keeping the 200 most recently active.
        var userId = Guid.NewGuid();

        // Seed older entries explicitly so ordering does not depend on clock resolution or sleeps.
        // One day old is safely inside the independent 365-day age-eviction limit.
        var oldTimestamp = DateTime.UtcNow.AddDays(-1);
        var oldResult = new DiscoveryFeedbackResult { UserId = userId };
        for (var i = 1; i <= 50; i++)
        {
            oldResult.Entries.Add(new DiscoveryFeedbackEntry
            {
                TmdbId = i,
                MediaType = "movie",
                Title = $"Old-{i}",
                ShownAtUtc = oldTimestamp
            });
        }

        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        File.WriteAllText(filePath, System.Text.Json.JsonSerializer.Serialize(new[] { oldResult }));
        var store = CreateStore();
        Assert.Equal(50, store.LoadForUser(userId)!.Entries.Count);

        var newBatch = new List<DiscoveryRecommendation>();
        for (var i = 51; i <= 250; i++)
        {
            newBatch.Add(new DiscoveryRecommendation
            {
                TmdbId = i,
                MediaType = "movie",
                Title = $"New-{i}"
            });
        }

        store.RecordShown(userId, "User", newBatch);

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        // Cap invariant.
        Assert.Equal(200, result!.Entries.Count);
        // The 50 oldest entries (TmdbId 1..50) must have been evicted; the 200 newest
        // (TmdbId 51..250) must remain, not just a sample of the retained entries.
        Assert.Equal(Enumerable.Range(51, 200), result.Entries.Select(e => e.TmdbId).Order());
    }

    [Fact]
    public void SaveInternal_EntriesOlderThanMaxAge_AreEvicted()
    {
        // Entries with ALL activity timestamps older than MaxEntryAgeDays=365 must be evicted.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        var userId = Guid.NewGuid();
        var ancientUtc = DateTime.UtcNow.AddDays(-400); // beyond the 365-day cutoff
        var recentUtc = DateTime.UtcNow.AddDays(-30);    // safely inside the cutoff

        var seedJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                UserId = userId,
                Entries = new[]
                {
                    new { TmdbId = 1, MediaType = "movie", ShownAtUtc = ancientUtc },
                    new { TmdbId = 2, MediaType = "movie", ShownAtUtc = recentUtc }
                }
            }
        });
        File.WriteAllText(filePath, seedJson);

        var store = CreateStore();

        // Trigger a save (RecordShown of an unrelated third item) so eviction runs.
        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 3, MediaType = "movie", Title = "Fresh" }
        });

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);

        // TmdbId=1 is the 400-day-old entry -> must be gone.
        Assert.DoesNotContain(result!.Entries, e => e.TmdbId == 1);
        // TmdbId=2 is 30 days old -> must survive.
        Assert.Contains(result.Entries, e => e.TmdbId == 2);
        // TmdbId=3 is fresh -> must be present.
        Assert.Contains(result.Entries, e => e.TmdbId == 3);
    }

    [Fact]
    public void SaveInternal_UsersWithZeroEntriesAfterEviction_AreRemoved()
    {
        // After eviction removes all of a user's entries (because they were all ancient), the user record itself must be removed from the top-level list.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        var deadUser = Guid.NewGuid();
        var liveUser = Guid.NewGuid();
        var ancientUtc = DateTime.UtcNow.AddDays(-500);
        var recentUtc = DateTime.UtcNow.AddDays(-10);

        var seedJson = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new
            {
                UserId = deadUser,
                Entries = new[] { new { TmdbId = 1, MediaType = "movie", ShownAtUtc = ancientUtc } }
            },
            new
            {
                UserId = liveUser,
                Entries = new[] { new { TmdbId = 2, MediaType = "movie", ShownAtUtc = recentUtc } }
            }
        });
        File.WriteAllText(filePath, seedJson);

        var store = CreateStore();

        // Trigger a save by touching the live user - eviction runs during the save.
        store.RecordShown(liveUser, "Live", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 3, MediaType = "movie" }
        });

        // Dead user's entries were all ancient -> their record must be removed entirely.
        Assert.Null(store.LoadForUser(deadUser));
        // Live user survives with the recent + fresh entries.
        var liveResult = store.LoadForUser(liveUser);
        Assert.NotNull(liveResult);
        Assert.Contains(liveResult!.Entries, e => e.TmdbId == 2);
        Assert.Contains(liveResult.Entries, e => e.TmdbId == 3);
    }

    [Fact]
    public void RecordDismissed_NewUser_CreatesEntryWithDismissedTimestamp()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, 101, "movie");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        var entry = Assert.Single(result!.Entries);
        Assert.Equal(101, entry.TmdbId);
        Assert.Equal("movie", entry.MediaType);
        Assert.NotNull(entry.DismissedAtUtc);
    }

    [Fact]
    public void RecordDismissed_ExistingEntry_UpdatesDismissedTimestamp()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, 101, "movie");
        var firstDismiss = store.LoadForUser(userId)!.Entries[0].DismissedAtUtc;

        store.RecordDismissed(userId, 101, "movie");
        var secondDismiss = store.LoadForUser(userId)!.Entries[0].DismissedAtUtc;

        // Entry is reused, not duplicated
        Assert.Single(store.LoadForUser(userId)!.Entries);
        Assert.True(secondDismiss >= firstDismiss);
    }

    [Fact]
    public void RecordDismissed_SameTmdbId_DifferentMediaType_CreatesDistinctEntries()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, 202, "movie");
        store.RecordDismissed(userId, 202, "tv");

        var entries = store.LoadForUser(userId)!.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.MediaType == "movie");
        Assert.Contains(entries, e => e.MediaType == "tv");
    }

    [Fact]
    public void RecordRequested_NewUser_CreatesEntryWithRequestedTimestamp()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordRequested(userId, 303, "tv");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        var entry = Assert.Single(result!.Entries);
        Assert.Equal(303, entry.TmdbId);
        Assert.Equal("tv", entry.MediaType);
        Assert.NotNull(entry.RequestedAtUtc);
    }

    [Fact]
    public void RecordRequested_ExistingEntry_DoesNotDuplicateEntry()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordRequested(userId, 404, "movie");
        store.RecordRequested(userId, 404, "movie");

        Assert.Single(store.LoadForUser(userId)!.Entries);
    }

    [Fact]
    public void RecordDismissed_InvalidTmdbId_Ignored()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, 0, "movie");
        store.RecordDismissed(userId, -1, "movie");

        Assert.Null(store.LoadForUser(userId));
    }

    [Fact]
    public void RecordRequested_InvalidTmdbId_Ignored()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordRequested(userId, 0, "tv");
        store.RecordRequested(userId, -5, "tv");

        Assert.Null(store.LoadForUser(userId));
    }

    [Fact]
    public void RecordShown_AfterDiskLoad_DoesNotMutatePersistedCache()
    {
        var userId = Guid.NewGuid();
        var store1 = CreateStore();
        store1.RecordShown(userId, "TestUser", [new DiscoveryRecommendation { TmdbId = 1, MediaType = "movie", Title = "Film" }]);

        var store2 = CreateStore();

        var first = store2.LoadForUser(userId);
        Assert.NotNull(first);
        var originalShownAt = first!.Entries[0].ShownAtUtc;

        // Mutate the returned object - simulates what RecordShown/RecordDismissed do.
        first.Entries[0].ShownAtUtc = DateTime.UtcNow.AddYears(10);

        // A second load must return the original unmodified value from the cache.
        var second = store2.LoadForUser(userId);
        Assert.NotNull(second);
        Assert.Equal(originalShownAt, second!.Entries[0].ShownAtUtc);
    }

    // NormalizeMediaType: defensive defaulting to "movie"

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordDismissed_NullOrWhitespaceMediaType_DefaultsToMovie(string? mediaType)
    {
        // Blank media type is the ambiguous case: the store documents "movie" as the safe
        // default so a null/blank value never yields an empty MediaType that breaks the
        // composite-key lookup.
        var store = CreateStore();
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, 700, mediaType!);

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        var entry = Assert.Single(result!.Entries);
        Assert.Equal("movie", entry.MediaType);
    }

    [Fact]
    public void RecordDismissed_UnrecognizedMediaType_NormalizesToMovieAndLogsDebug()
    {
        // An unexpected media type (neither "movie" nor "tv") must still normalize to
        // "movie" and emit a debug diagnostic so operators can spot bad upstream data.
        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryFeedbackStore>>();
        logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
        using var store = new DiscoveryFeedbackStore(pluginLog.Object, logger.Object, _tempDir);
        var userId = Guid.NewGuid();

        store.RecordDismissed(userId, 800, "podcast");

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Equal("movie", Assert.Single(result!.Entries).MediaType);

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString()!.Contains("Unexpected mediaType")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // SaveInternal: directory creation + graceful degradation on unwritable path

    [Fact]
    public void RecordShown_DataFolderDoesNotYetExist_CreatesDirectoryAndPersists()
    {
        // A brand-new plugin install has no data subfolder yet. The save path must
        // create the missing directory before the atomic write rather than throwing.
        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryFeedbackStore>>();
        var nestedDir = Path.Join(_tempDir, "not", "yet", "created");
        using var store = new DiscoveryFeedbackStore(pluginLog.Object, logger.Object, nestedDir);
        var userId = Guid.NewGuid();

        store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie", Title = "FirstRun" }
        });

        Assert.True(File.Exists(Path.Join(nestedDir, "jellyfin-helper-discovery-feedback.json")));
        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Equal("FirstRun", Assert.Single(result!.Entries).Title);
    }

    [Fact]
    public void RecordShown_UnwritableFilePath_DegradesGracefullyAndLogsWarning()
    {
        // A directory occupying the exact feedback file name makes the atomic move fail. The store must swallow the IO failure (best-effort persistence) and log a warning instead of propagating the exception to the caller's request/task.
        var dataDir = Path.Join(_tempDir, "blocked");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Join(dataDir, "jellyfin-helper-discovery-feedback.json"));

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryFeedbackStore>>();
        using var store = new DiscoveryFeedbackStore(pluginLog.Object, logger.Object, dataDir);
        var userId = Guid.NewGuid();

        var ex = Record.Exception(() => store.RecordShown(userId, "User", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie", Title = "Unwritable" }
        }));

        Assert.Null(ex);
        pluginLog.Verify(
            p => p.LogWarning(
                "DiscoveryFeedback",
                It.Is<string>(m => m.Contains("Could not save", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.AtLeastOnce);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RecordDismissed_NonPositiveTmdbId_Ignored(int tmdbId)
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        var ex = Record.Exception(() => store.RecordDismissed(userId, tmdbId, "movie"));

        Assert.Null(ex);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("MOVIE")]
    [InlineData("Movie")]
    [InlineData("tv")]
    [InlineData("TV")]
    [InlineData("invalid-type")]
    [InlineData("")]
    public void RecordDismissed_MediaTypeInjection_CollapsesSafely(string mediaType)
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        var ex = Record.Exception(() => store.RecordDismissed(userId, 123, mediaType));

        Assert.Null(ex);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0)]
    [InlineData(-5)]
    public void RecordRequested_NonPositiveTmdbId_Ignored(int tmdbId)
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        var ex = Record.Exception(() => store.RecordRequested(userId, tmdbId, "movie"));

        Assert.Null(ex);
    }
}
