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
    private readonly string _tempDir;
    private readonly List<DiscoveryFeedbackStore> _ownedStores = [];

    public DiscoveryFeedbackStoreTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
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
            Assert.Contains((700, "tv"), store.GetDismissedItems(user));
            Assert.DoesNotContain((700, "movie"), store.GetDismissedItems(user));
            Assert.Empty(store.GetDismissedItems(Guid.NewGuid()));
            Assert.Null(Assert.Single(store.LoadForUser(user)!.Entries).DismissedAtUtc);
            Assert.Throws<IOException>(() => store.LoadAll());
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
        await clock.WaitForDeadlineAsync(TestContext.Current.CancellationToken);
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
        await clock.WaitForDeadlineAsync(TestContext.Current.CancellationToken);
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
            Assert.Null(Assert.Single(store.LoadForUser(user)!.Entries).ImpressionAtUtc);
            Assert.Throws<IOException>(() => store.LoadAll());
            release.Set();
            await impression.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries);
            Assert.Equal(acquired.Now, entry.ImpressionAtUtc);
            Assert.Equal(.7, entry.ScoringFeatures!.GenreSimilarity);
        }
        finally { release.Set(); await impression; }
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
            Assert.Null(await load);
            Assert.Empty(await training);
        }
        finally { release.Set(); await load; if (training is not null) await training; }
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
    public async Task AnOlderDurableDismissalCannotRecoverANewerRejectedDismissal()
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
            Assert.Equal(original, Assert.Single(store.LoadForUser(user)!.Entries, entry => entry.TmdbId == 1).DismissedAtUtc);
            Assert.Throws<IOException>(() => store.LoadAll());
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
