using System.Collections;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the family of Seerr / TMDb JSON DTOs used by the discovery pipeline.
///     Each test asserts the wire contract: property names (lower-camel), default values,
///     null-handling of collection properties, and round-trip fidelity.
///     Several of these types are <c>internal sealed</c>, so we reach them via reflection
///     to avoid making them public just to test them.
/// </summary>
public class SeerrDiscoveryDtoTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static Type Resolve(string typeName)
        => PluginAssembly.GetType($"Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery.{typeName}", throwOnError: true)!;

    private static object Deserialize(Type t, string json)
    {
        var result = JsonSerializer.Deserialize(json, t);
        Assert.NotNull(result);
        return result!;
    }

    private static object? GetProp(object obj, string name)
        => obj.GetType().GetProperty(name)!.GetValue(obj);

    // -----------------------------------------------------------------------
    // SeerrCastMember
    // -----------------------------------------------------------------------

    [Fact]
    public void SeerrCastMember_DeserializesLowerCamelWireContract()
    {
        var t = Resolve("SeerrCastMember");
        var json = "{\"id\":42,\"name\":\"Actor A\",\"character\":\"Hero\",\"order\":3}";
        var obj = Deserialize(t, json);
        Assert.Equal(42, GetProp(obj, "Id"));
        Assert.Equal("Actor A", GetProp(obj, "Name"));
        Assert.Equal("Hero", GetProp(obj, "Character"));
        Assert.Equal(3, GetProp(obj, "Order"));
    }

    [Fact]
    public void SeerrCastMember_DefaultsAreSensible()
    {
        var t = Resolve("SeerrCastMember");
        var obj = Activator.CreateInstance(t)!;
        Assert.Equal(0, GetProp(obj, "Id"));
        Assert.Equal(string.Empty, GetProp(obj, "Name"));
        Assert.Null(GetProp(obj, "Character"));
        Assert.Equal(0, GetProp(obj, "Order"));
    }

    [Fact]
    public void SeerrCastMember_MissingCharacter_StaysNull()
    {
        var t = Resolve("SeerrCastMember");
        var obj = Deserialize(t, "{\"id\":1,\"name\":\"A\"}");
        Assert.Null(GetProp(obj, "Character"));
    }

    // -----------------------------------------------------------------------
    // SeerrCrewMember
    // -----------------------------------------------------------------------

    [Fact]
    public void SeerrCrewMember_DeserializesLowerCamelWireContract()
    {
        var t = Resolve("SeerrCrewMember");
        var json = "{\"id\":7,\"name\":\"Director X\",\"job\":\"Director\",\"department\":\"Directing\"}";
        var obj = Deserialize(t, json);
        Assert.Equal(7, GetProp(obj, "Id"));
        Assert.Equal("Director X", GetProp(obj, "Name"));
        Assert.Equal("Director", GetProp(obj, "Job"));
        Assert.Equal("Directing", GetProp(obj, "Department"));
    }

    [Fact]
    public void SeerrCrewMember_JobAndDepartment_AreNullableAndDefaultNull()
    {
        var t = Resolve("SeerrCrewMember");
        var obj = Deserialize(t, "{\"id\":1,\"name\":\"X\"}");
        Assert.Null(GetProp(obj, "Job"));
        Assert.Null(GetProp(obj, "Department"));
    }

    // -----------------------------------------------------------------------
    // SeerrCredits
    // -----------------------------------------------------------------------

    [Fact]
    public void SeerrCredits_DeserializesCastAndCrewIntoCollections()
    {
        var t = Resolve("SeerrCredits");
        var json = "{\"cast\":[{\"id\":1,\"name\":\"A\",\"order\":0},{\"id\":2,\"name\":\"B\",\"order\":1}]," +
                   "\"crew\":[{\"id\":3,\"name\":\"C\",\"job\":\"Director\"}]}";
        var obj = Deserialize(t, json);

        var cast = (IList)GetProp(obj, "Cast")!;
        var crew = (IList)GetProp(obj, "Crew")!;
        Assert.Equal(2, cast.Count);
        Assert.Single(crew);
    }

    [Fact]
    public void SeerrCredits_DefaultsAreEmptyLists_NotNull()
    {
        var t = Resolve("SeerrCredits");
        var obj = Activator.CreateInstance(t)!;
        Assert.NotNull(GetProp(obj, "Cast"));
        Assert.NotNull(GetProp(obj, "Crew"));
        Assert.Empty((IEnumerable)GetProp(obj, "Cast")!);
        Assert.Empty((IEnumerable)GetProp(obj, "Crew")!);
    }

    // -----------------------------------------------------------------------
    // SeerrMediaDetailResponse
    // -----------------------------------------------------------------------

    [Fact]
    public void SeerrMediaDetailResponse_DeserializesIdAndOptionalCredits()
    {
        var t = Resolve("SeerrMediaDetailResponse");
        var json = "{\"id\":550,\"credits\":{\"cast\":[{\"id\":1,\"name\":\"A\"}],\"crew\":[]}}";
        var obj = Deserialize(t, json);
        Assert.Equal(550, GetProp(obj, "Id"));
        Assert.NotNull(GetProp(obj, "Credits"));
    }

    [Fact]
    public void SeerrMediaDetailResponse_MissingCredits_StaysNull()
    {
        var t = Resolve("SeerrMediaDetailResponse");
        var obj = Deserialize(t, "{\"id\":550}");
        Assert.Null(GetProp(obj, "Credits"));
    }

    // -----------------------------------------------------------------------
    // SeerrUser (public - no reflection needed)
    // -----------------------------------------------------------------------

    [Fact]
    public void SeerrUser_DeserializesFullWireContract()
    {
        var json = "{\"id\":9,\"displayName\":\"Alice\",\"email\":\"a@b.c\",\"avatar\":\"http://x/a.png\"," +
                   "\"jellyfinUserId\":\"abc-123\",\"permissions\":2}";
        var user = JsonSerializer.Deserialize<SeerrUser>(json);
        Assert.NotNull(user);
        Assert.Equal(9, user!.Id);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal("a@b.c", user.Email);
        Assert.Equal("http://x/a.png", user.Avatar);
        Assert.Equal("abc-123", user.JellyfinUserId);
        Assert.Equal(2L, user.Permissions);
    }

    [Fact]
    public void SeerrUser_MissingOptionalFields_StayNull()
    {
        var user = JsonSerializer.Deserialize<SeerrUser>("{\"id\":1,\"displayName\":\"Bob\",\"permissions\":0}");
        Assert.NotNull(user);
        Assert.Null(user!.Email);
        Assert.Null(user.Avatar);
        Assert.Null(user.JellyfinUserId);
    }

    [Fact]
    public void SeerrUser_PermissionsIsLong_HandlesLargeBitmask()
    {
        // The permission bitmask can exceed int.MaxValue in Overseerr. If Permissions were
        // declared as int, deserialization of a real payload would throw or silently overflow.
        var largeMask = 1L << 40;
        var json = $"{{\"id\":1,\"displayName\":\"X\",\"permissions\":{largeMask}}}";
        var user = JsonSerializer.Deserialize<SeerrUser>(json);
        Assert.NotNull(user);
        Assert.Equal(largeMask, user!.Permissions);
    }

    [Fact]
    public void SeerrUser_DisplayNameDefault_IsEmptyStringNotNull()
    {
        // Consumers pass DisplayName directly to string.Compare / string.IsNullOrEmpty;
        // a null default would surprise them.
        var user = new SeerrUser();
        Assert.Equal(string.Empty, user.DisplayName);
    }

    // -----------------------------------------------------------------------
    // SeerrUserPage / SeerrUserPageInfo
    // -----------------------------------------------------------------------

    [Fact]
    public void SeerrUserPage_DeserializesPageInfoAndResults()
    {
        var t = Resolve("SeerrUserPage");
        var json = "{\"pageInfo\":{\"results\":10,\"pages\":2}," +
                   "\"results\":[{\"id\":1,\"displayName\":\"A\",\"permissions\":0}]}";
        var obj = Deserialize(t, json);
        Assert.NotNull(GetProp(obj, "PageInfo"));
        var results = (IList)GetProp(obj, "Results")!;
        Assert.Single(results);
    }

    [Fact]
    public void SeerrUserPage_DefaultResults_IsEmptyList()
    {
        var t = Resolve("SeerrUserPage");
        var obj = Activator.CreateInstance(t)!;
        Assert.NotNull(GetProp(obj, "Results"));
        Assert.Empty((IEnumerable)GetProp(obj, "Results")!);
        // PageInfo may be null when not populated - accepted.
    }

    [Fact]
    public void SeerrUserPageInfo_DeserializesResultsAndPages()
    {
        var t = Resolve("SeerrUserPageInfo");
        var obj = Deserialize(t, "{\"results\":25,\"pages\":3}");
        Assert.Equal(25, GetProp(obj, "Results"));
        Assert.Equal(3, GetProp(obj, "Pages"));
    }

    [Fact]
    public void SeerrUserPageInfo_MissingFields_DefaultsToZero()
    {
        var t = Resolve("SeerrUserPageInfo");
        var obj = Deserialize(t, "{}");
        Assert.Equal(0, GetProp(obj, "Results"));
        Assert.Equal(0, GetProp(obj, "Pages"));
    }

    // -----------------------------------------------------------------------
    // TmdbDiscoverResponse - has a non-trivial Results setter that coalesces null → []
    // -----------------------------------------------------------------------

    [Fact]
    public void TmdbDiscoverResponse_DeserializesPagingFields()
    {
        var t = Resolve("TmdbDiscoverResponse");
        var json = "{\"page\":2,\"totalPages\":10,\"totalResults\":195,\"results\":[]}";
        var obj = Deserialize(t, json);
        Assert.Equal(2, GetProp(obj, "Page"));
        Assert.Equal(10, GetProp(obj, "TotalPages"));
        Assert.Equal(195, GetProp(obj, "TotalResults"));
    }

    [Fact]
    public void TmdbDiscoverResponse_NullResults_CoalescesToEmptyList()
    {
        // Seerr's /api/v1/discover returns "results": null on empty pages.
        // Without the null-coalescing setter, downstream `.Count` / `.Any()` would NRE.
        var t = Resolve("TmdbDiscoverResponse");
        var obj = Deserialize(t, "{\"page\":1,\"totalPages\":0,\"totalResults\":0,\"results\":null}");
        var results = GetProp(obj, "Results");
        Assert.NotNull(results);
        Assert.Empty((IEnumerable)results!);
    }

    [Fact]
    public void TmdbDiscoverResponse_ResultsSetter_AssigningNull_YieldsEmptyList()
    {
        // Direct property assignment must exercise the same guard.
        var t = Resolve("TmdbDiscoverResponse");
        var obj = Activator.CreateInstance(t)!;
        obj.GetType().GetProperty("Results")!.SetValue(obj, null);
        var results = GetProp(obj, "Results");
        Assert.NotNull(results);
        Assert.Empty((IEnumerable)results!);
    }

    [Fact]
    public void TmdbDiscoverResponse_MissingResults_DefaultsToEmptyList()
    {
        var t = Resolve("TmdbDiscoverResponse");
        var obj = Deserialize(t, "{\"page\":1,\"totalPages\":0,\"totalResults\":0}");
        Assert.NotNull(GetProp(obj, "Results"));
        Assert.Empty((IEnumerable)GetProp(obj, "Results")!);
    }

    // -----------------------------------------------------------------------
    // DiscoveryResult (public class)
    // -----------------------------------------------------------------------

    [Fact]
    public void DiscoveryResult_UserName_IsIgnoredDuringJsonSerialization()
    {
        // Contract: UserName is marked [JsonIgnore] to keep PII out of persisted cache files.
        // If someone accidentally removes the attribute, this test fires.
        var dr = new DiscoveryResult
        {
            UserId = Guid.NewGuid(),
            UserName = "Alice",
            Recommendations = [new DiscoveryRecommendation { TmdbId = 1, MediaType = "movie" }]
        };
        var json = JsonSerializer.Serialize(dr);
        Assert.DoesNotContain("Alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("UserName", json, StringComparison.Ordinal);
        // But the persistent fields must survive round-trip:
        Assert.Contains(dr.UserId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoveryResult_JsonRoundTrip_LosesUserNameByDesign()
    {
        var dr = new DiscoveryResult
        {
            UserId = Guid.NewGuid(),
            UserName = "will-be-dropped",
            GeneratedAt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
            Recommendations = []
        };
        var json = JsonSerializer.Serialize(dr);
        var back = JsonSerializer.Deserialize<DiscoveryResult>(json);
        Assert.NotNull(back);
        Assert.Equal(dr.UserId, back!.UserId);
        Assert.Equal(string.Empty, back.UserName); // dropped by [JsonIgnore]
        Assert.NotNull(back.Recommendations);
    }

    [Fact]
    public void DiscoveryResult_GeneratedAt_DefaultsToUtcNow_WithinReasonableWindow()
    {
        // The default-value expression `DateTime.UtcNow` executes at construction time,
        // so a newly-created object must have a UTC-kind timestamp near "now".
        var before = DateTime.UtcNow.AddSeconds(-1);
        var dr = new DiscoveryResult();
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.Equal(DateTimeKind.Utc, dr.GeneratedAt.Kind);
        Assert.InRange(dr.GeneratedAt, before, after);
    }

    [Fact]
    public void DiscoveryResult_RecommendationsDefault_IsEmptyMutableList()
    {
        // System.Text.Json requires a settable list; consumers Add() to it directly.
        var dr = new DiscoveryResult();
        Assert.NotNull(dr.Recommendations);
        Assert.Empty(dr.Recommendations);
        dr.Recommendations.Add(new DiscoveryRecommendation());
        Assert.Single(dr.Recommendations);
    }

    // -----------------------------------------------------------------------
    // UserRequestPermissionResult (public - used by the DiscoveryController response contract)
    // -----------------------------------------------------------------------

    [Fact]
    public void UserRequestPermissionResult_DefaultInstance_IsSafeShape()
    {
        var r = new UserRequestPermissionResult();
        Assert.False(r.CanRequest);
        Assert.Null(r.DeniedReason);
        Assert.False(r.IsTransient);
        Assert.NotNull(r.Profiles);
        Assert.Empty(r.Profiles);
    }

    [Fact]
    public void UserRequestPermissionResult_JsonRoundTrip_PreservesAllFields()
    {
        var r = new UserRequestPermissionResult
        {
            CanRequest = false,
            DeniedReason = "Seerr unreachable",
            IsTransient = true,
            Profiles =
            [
                new AllowedQualityProfile
                {
                    ServerId = 1,
                    ServerName = "Radarr",
                    ProfileId = 4,
                    ProfileName = "Any",
                    IsDefault = true,
                    RootFolder = "/movies"
                }
            ]
        };
        var json = JsonSerializer.Serialize(r);
        var back = JsonSerializer.Deserialize<UserRequestPermissionResult>(json);
        Assert.NotNull(back);
        Assert.False(back!.CanRequest);
        Assert.Equal("Seerr unreachable", back.DeniedReason);
        Assert.True(back.IsTransient);
        Assert.Single(back.Profiles);
    }

    [Fact]
    public void UserRequestPermissionResult_TransientDenial_IsDistinctFromPermanentDenial()
    {
        // IsTransient must be independently settable from DeniedReason so the
        // controller can pick 503 vs 403 correctly.
        var transient = new UserRequestPermissionResult
        {
            CanRequest = false,
            DeniedReason = "temporary",
            IsTransient = true
        };
        var permanent = new UserRequestPermissionResult
        {
            CanRequest = false,
            DeniedReason = "no permission",
            IsTransient = false
        };
        Assert.True(transient.IsTransient);
        Assert.False(permanent.IsTransient);
    }

    // -----------------------------------------------------------------------
    // DiscoveryFeedbackEntry - MediaType normalisation + GetStatus() state machine
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("MOVIE", "movie")]
    [InlineData("Movie", "movie")]
    [InlineData("  tv  ", "tv")]
    [InlineData("TV", "tv")]
    public void DiscoveryFeedbackEntry_MediaTypeSetter_NormalisesToLowercaseTrimmed(string input, string expected)
    {
        var e = new DiscoveryFeedbackEntry { MediaType = input };
        Assert.Equal(expected, e.MediaType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DiscoveryFeedbackEntry_MediaTypeSetter_EmptyOrNull_DefaultsToMovie(string? input)
    {
        var e = new DiscoveryFeedbackEntry { MediaType = input! };
        Assert.Equal("movie", e.MediaType);
    }

    [Fact]
    public void DiscoveryFeedbackEntry_GetStatus_ShownOnly_ReturnsShown()
    {
        var e = new DiscoveryFeedbackEntry { ShownAtUtc = DateTime.UtcNow };
        Assert.Equal(DiscoveryInteractionStatus.Shown, e.GetStatus());
    }

    [Fact]
    public void DiscoveryFeedbackEntry_GetStatus_Dismissed_ReturnsDismissed()
    {
        var e = new DiscoveryFeedbackEntry
        {
            ShownAtUtc = DateTime.UtcNow,
            DismissedAtUtc = DateTime.UtcNow
        };
        Assert.Equal(DiscoveryInteractionStatus.Dismissed, e.GetStatus());
    }

    [Fact]
    public void DiscoveryFeedbackEntry_GetStatus_RequestedOnly_ReturnsRequested()
    {
        var e = new DiscoveryFeedbackEntry
        {
            ShownAtUtc = DateTime.UtcNow,
            RequestedAtUtc = DateTime.UtcNow
        };
        Assert.Equal(DiscoveryInteractionStatus.Requested, e.GetStatus());
    }

    [Fact]
    public void DiscoveryFeedbackEntry_GetStatus_RequestedTakesPrecedenceOverDismissed()
    {
        // If a user first dismisses an item, then requests it later, the
        // "Requested" status must win. Otherwise the training pipeline would treat a
        // converted request as a negative signal.
        var e = new DiscoveryFeedbackEntry
        {
            ShownAtUtc = DateTime.UtcNow.AddDays(-2),
            DismissedAtUtc = DateTime.UtcNow.AddDays(-1),
            RequestedAtUtc = DateTime.UtcNow
        };
        Assert.Equal(DiscoveryInteractionStatus.Requested, e.GetStatus());
    }

    [Fact]
    public void DiscoveryFeedbackEntry_GetStatus_RequestedAndWatched_WinsOverEverything()
    {
        var e = new DiscoveryFeedbackEntry
        {
            ShownAtUtc = DateTime.UtcNow.AddDays(-3),
            DismissedAtUtc = DateTime.UtcNow.AddDays(-2),
            RequestedAtUtc = DateTime.UtcNow.AddDays(-1),
            WasWatched = true,
            WatchedAtUtc = DateTime.UtcNow
        };
        Assert.Equal(DiscoveryInteractionStatus.RequestedAndWatched, e.GetStatus());
    }

    [Fact]
    public void DiscoveryFeedbackEntry_GetStatus_WasWatchedButNotRequested_DoesNotBecomeRequestedAndWatched()
    {
        // Defensive invariant: WasWatched alone does not qualify as RequestedAndWatched.
        // Only the combination Requested + WasWatched does. Otherwise the "shown → watched
        // externally" edge case (user watched the movie somewhere else) would falsely count
        // as a conversion.
        var e = new DiscoveryFeedbackEntry
        {
            ShownAtUtc = DateTime.UtcNow,
            WasWatched = true // no RequestedAtUtc
        };
        Assert.Equal(DiscoveryInteractionStatus.Shown, e.GetStatus());
    }

    [Fact]
    public void DiscoveryFeedbackEntry_Defaults_AllCollectionsAreEmptyLists()
    {
        var e = new DiscoveryFeedbackEntry();
        Assert.NotNull(e.Genres);
        Assert.Empty(e.Genres);
        Assert.NotNull(e.KnownPeople);
        Assert.Empty(e.KnownPeople);
        Assert.Equal("movie", e.MediaType);
        Assert.Equal(string.Empty, e.Title);
    }
}
