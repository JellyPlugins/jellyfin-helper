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
}
