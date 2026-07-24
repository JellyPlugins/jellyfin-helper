using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for <see cref="DiscoveryFeedbackStore"/> composite key (TmdbId, MediaType) dedup logic.
///     Verifies that Movie and TV items with the same TMDb ID are tracked independently.
///     Each test gets an isolated temp directory to prevent cross-test file contamination.
/// </summary>
public class DiscoveryFeedbackStoreTests : IDisposable
{
    private readonly string _tempDir;

    public DiscoveryFeedbackStoreTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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
        return new DiscoveryFeedbackStore(pluginLog.Object, logger.Object, _tempDir);
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

        // Dismiss with mixed-case media type — should still match
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

        // Request with uppercase media type — should still match
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

        // Dismiss with whitespace-padded media type — should still match after normalization
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
        // UserName is [JsonIgnore] (PII not persisted to disk) — expected to be empty after round-trip.
        Assert.Equal(string.Empty, result.UserName);
        Assert.Equal(2, result.Entries.Count);

        var movieEntry = result.Entries.First(e => e.MediaType == "movie");
        Assert.Equal(42, movieEntry.TmdbId);
        Assert.Equal("Persistence Test", movieEntry.Title);
        Assert.Equal(2024, movieEntry.Year);
        Assert.Equal(7.8, movieEntry.TmdbRating);
        Assert.Equal(0.85, movieEntry.Score);
        Assert.Contains("Actor A", movieEntry.KnownPeople);
        Assert.Contains("Director B", movieEntry.KnownPeople);

        var tvEntry = result.Entries.First(e => e.MediaType == "tv");
        Assert.Equal(42, tvEntry.TmdbId);
        Assert.Equal("TV Persistence Test", tvEntry.Title);
        Assert.Equal(2023, tvEntry.Year);
    }

    // -----------------------------------------------------------------------
    // Guard-clause tests: empty inputs / invalid IDs must not persist
    // -----------------------------------------------------------------------

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
        var firstTimestamp = store.LoadForUser(userId)!.Entries[0].WatchedAtUtc;

        // Wait a moment so a re-mark would produce a different timestamp.
        Thread.Sleep(20);
        store.MarkWatched(userId, watched);
        var secondTimestamp = store.LoadForUser(userId)!.Entries[0].WatchedAtUtc;

        // The second call must not update WatchedAtUtc, proving the WasWatched short-circuit.
        Assert.Equal(firstTimestamp, secondTimestamp);
    }

    // -----------------------------------------------------------------------
    // RecordShown backfill: existing placeholder entries are enriched, not replaced
    // -----------------------------------------------------------------------

    [Fact]
    public void RecordShown_BackfillsAllMetadataOnPlaceholderEntry()
    {
        // A placeholder entry created by RecordDismissed carries only TmdbId/MediaType.
        // A later RecordShown must backfill Title, Year, Genres, TmdbRating, Popularity,
        // Score, and KnownPeople onto that same entry (no duplicate row).
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
        Assert.Contains("Actor X", entry.KnownPeople);
        // Placeholder DismissedAtUtc must remain intact (not overwritten by RecordShown).
        Assert.NotNull(entry.DismissedAtUtc);
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

    // -----------------------------------------------------------------------
    // Lookup helpers: GetDismissedItems / GetRequestedItems / LoadAll
    // -----------------------------------------------------------------------

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
        // Look up u1's returned record explicitly rather than relying on `all[0]` — the
        // dictionary-backed store makes no ordering guarantee, and picking the wrong
        // record would let a shallow-copy regression pass by mutating u2 while asserting
        // only against u1's later state.
        var returnedU1 = all.First(r => r.UserId == u1);
        returnedU1.Entries.Clear();
        returnedU1.Entries.Add(
            new DiscoveryFeedbackEntry { TmdbId = 9999, MediaType = "movie" });

        var fresh = store.LoadAll();
        // Original entries must still be present for u1 (the mutation target).
        var freshU1 = fresh.First(r => r.UserId == u1);
        Assert.Contains(freshU1.Entries, e => e.TmdbId == 1);
        Assert.DoesNotContain(freshU1.Entries, e => e.TmdbId == 9999);

        // And u2 is untouched too — a defensive-copy regression that only affected
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

    // -----------------------------------------------------------------------
    // File-corruption / oversize handling
    // -----------------------------------------------------------------------

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

        var store = new DiscoveryFeedbackStore(pluginLog.Object, logger.Object, _tempDir);

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

    // -----------------------------------------------------------------------
    // Isolation invariant: repeated construction with same directory does not
    // clobber previously-written data.
    // -----------------------------------------------------------------------

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

        // Mutate through the second instance…
        store2.RecordRequested(userId, 42, "movie");

        // …and prove the change is visible through a *third* freshly-constructed store.
        var store3 = CreateStore();
        var seenByStore3 = store3.LoadForUser(userId);
        Assert.NotNull(seenByStore3);
        Assert.NotNull(seenByStore3!.Entries[0].RequestedAtUtc);
    }

    // -----------------------------------------------------------------------
    // GetOrCreateUserResult: UserName update
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // Bounds enforcement: MaxFileSizeBytes / MaxEntriesPerUser / MaxEntryAgeDays
    //
    // These branches guard against three separate DoS-style corruption modes:
    //   1. A rogue writer inflates the feedback file past 30 MB — the store must
    //      delete it and start over rather than OOM'ing on the next read.
    //   2. A user's entry list grows unbounded because show/dismiss/request cycles
    //      accumulate over years — the store must cap at MaxEntriesPerUser=200 and
    //      keep the most-recently-active entries, so training data reflects recent
    //      user preferences instead of ancient noise.
    //   3. Very old entries (>365 days) survive indefinitely — the store must evict
    //      them so the JSON file doesn't grow by ~50 bytes per shown item forever.
    //
    // The current tests exercise none of these paths (all use tiny in-memory sets
    // with fresh timestamps). Coverage report flags them explicitly.
    // -----------------------------------------------------------------------

    [Fact]
    public void LoadForUser_OversizeFile_DeletesFileAndReturnsNull()
    {
        // Any file larger than MaxFileSizeBytes (30 MB) must be treated
        // as poisoned — deserializing a 30 MB+ JSON payload can OOM small deployments
        // (e.g. Raspberry Pi hosts) and even valid content that grows this large is
        // a sign the eviction logic silently broke. The store must delete the file
        // and log a warning so the operator sees the recovery.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-discovery-feedback.json");
        // Write just over the 30 MB threshold. We use a valid JSON array header
        // followed by filler so the code hits the size check BEFORE the deserializer.
        // 30 MB = 30 * 1024 * 1024 = 31,457,280 bytes. Add a few extra to guarantee overflow.
        var padSize = (30 * 1024 * 1024) + 1024;
        using (var stream = File.Create(filePath))
        {
            // Prefix with a valid empty-array so a code path that skipped the size guard
            // would still deserialize cleanly — makes the test specifically prove the
            // size-guard fires FIRST, not the JSON parser bailing on garbage input.
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

        var store = new DiscoveryFeedbackStore(
            pluginLog.Object,
            new Mock<ILogger<DiscoveryFeedbackStore>>().Object,
            _tempDir);

        var result = store.LoadForUser(Guid.NewGuid());

        Assert.Null(result);
        Assert.False(File.Exists(filePath), "oversize file must be deleted");
        Assert.Equal(1, warningCount);
    }

    [Fact]
    public void SaveInternal_UserWithMoreThanMaxEntries_KeepsOnlyMostRecent200()
    {
        // Cap at MaxEntriesPerUser=200. A user who accumulates 250 shown
        // items must have the oldest-by-activity 50 evicted, keeping the 200 most
        // recently active. This prevents unbounded per-user growth.
        //
        // We force a mixture of "shown-only" entries with staggered ShownAtUtc to
        // avoid entries getting the same timestamp (which would make the eviction
        // ordering ambiguous — LINQ OrderByDescending is stable but the input order
        // has to be non-degenerate for the test to be meaningful).
        var store = CreateStore();
        var userId = Guid.NewGuid();

        // Show 250 items in one batch — RecordShown assigns DateTime.UtcNow to all
        // of them, but the eviction code uses GetLatestActivityUtc which will tie.
        // To make eviction deterministic we do two batches with a small wait between.
        var oldBatch = new List<DiscoveryRecommendation>();
        for (var i = 1; i <= 50; i++)
        {
            oldBatch.Add(new DiscoveryRecommendation
            {
                TmdbId = i,
                MediaType = "movie",
                Title = $"Old-{i}"
            });
        }

        store.RecordShown(userId, "User", oldBatch);

        Thread.Sleep(50); // ensure the second batch has strictly later ShownAtUtc

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
        // (TmdbId 51..250) must remain. Sample a handful to confirm.
        Assert.DoesNotContain(result.Entries, e => e.TmdbId == 1);
        Assert.DoesNotContain(result.Entries, e => e.TmdbId == 25);
        Assert.DoesNotContain(result.Entries, e => e.TmdbId == 50);
        Assert.Contains(result.Entries, e => e.TmdbId == 51);
        Assert.Contains(result.Entries, e => e.TmdbId == 250);
    }

    [Fact]
    public void SaveInternal_EntriesOlderThanMaxAge_AreEvicted()
    {
        // Entries with ALL activity timestamps older than MaxEntryAgeDays=365
        // must be evicted. We seed the file directly (bypassing RecordShown, which
        // stamps DateTime.UtcNow) so the ancient timestamps are real, then trigger a
        // save via a fresh RecordShown call to exercise the eviction path.
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

        // TmdbId=1 is the 400-day-old entry → must be gone.
        Assert.DoesNotContain(result!.Entries, e => e.TmdbId == 1);
        // TmdbId=2 is 30 days old → must survive.
        Assert.Contains(result.Entries, e => e.TmdbId == 2);
        // TmdbId=3 is fresh → must be present.
        Assert.Contains(result.Entries, e => e.TmdbId == 3);
    }

    [Fact]
    public void SaveInternal_UsersWithZeroEntriesAfterEviction_AreRemoved()
    {
        // After eviction removes all of a user's entries (because they
        // were all ancient), the user record itself must be removed from the top-level
        // list. Otherwise the file accumulates empty user shells forever.
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

        // Trigger a save by touching the live user — eviction runs during the save.
        store.RecordShown(liveUser, "Live", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 3, MediaType = "movie" }
        });

        // Dead user's entries were all ancient → their record must be removed entirely.
        Assert.Null(store.LoadForUser(deadUser));
        // Live user survives with the recent + fresh entries.
        var liveResult = store.LoadForUser(liveUser);
        Assert.NotNull(liveResult);
        Assert.Contains(liveResult!.Entries, e => e.TmdbId == 2);
        Assert.Contains(liveResult.Entries, e => e.TmdbId == 3);
    }

    // ===== RecordDismissed / RecordRequested O(1) path via GetOrCreateEntry =====

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
}
