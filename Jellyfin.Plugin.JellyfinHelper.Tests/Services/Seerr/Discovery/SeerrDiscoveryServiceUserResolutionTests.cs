using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the user-resolution and quality-profile-list helpers on SeerrDiscoveryService: FindSeerrUserByJellyfinId and BuildAllowedProfileList.
/// </summary>
public sealed class SeerrDiscoveryServiceUserResolutionTests
{
    // FindSeerrUserByJellyfinId • Match uses Guid.ToString("N") normalisation (32-char lowercase, no hyphens).

    [Fact]
    public void FindSeerrUserByJellyfinId_EmptyGuid_ReturnsNull()
    {
        // Empty Guid is a sentinel for "no user identified" and must never match a real Seerr user, otherwise a request could be silently attributed to whichever user happens to sit at position 0 of the Seerr roster.
        var users = new List<SeerrUser>
        {
            new() { Id = 1, JellyfinUserId = "00000000000000000000000000000000" }
        };
        var result = Invoke(users, Guid.Empty);
        Assert.Null(result);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_EmptyUserList_ReturnsNull()
    {
        var result = Invoke([], Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_UserWithEmptyJellyfinId_IsSkipped()
    {
        // A Seerr user that has no linked Jellyfin ID must be skipped rather than incorrectly
        // matched against a normalized value that happens to be empty on both sides.
        var guid = Guid.NewGuid();
        var users = new List<SeerrUser>
        {
            new() { Id = 1, JellyfinUserId = "" },
            new() { Id = 2, JellyfinUserId = null }
        };
        var result = Invoke(users, guid);
        Assert.Null(result);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_32CharMatch_LowerCase_ReturnsUser()
    {
        // 32-char Seerr ID (no hyphens, lowercase) - the fast path.
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var users = new List<SeerrUser>
        {
            new() { Id = 42, JellyfinUserId = "11111111222233334444555555555555" }
        };
        var result = Invoke(users, guid);
        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_32CharMatch_UpperCase_ReturnsUser()
    {
        // BUG GUARD: case-insensitive matching. Seerr occasionally stores IDs uppercased - the fast path (32 chars) must not require lowercase, otherwise every uppercase-storing Seerr instance would report ALL users as "not linked to Seerr".
        var guid = Guid.Parse("aabbccdd-1122-3344-5566-778899aabbcc");
        var users = new List<SeerrUser>
        {
            new() { Id = 7, JellyfinUserId = "AABBCCDD112233445566778899AABBCC" }
        };
        var result = Invoke(users, guid);
        Assert.NotNull(result);
        Assert.Equal(7, result!.Id);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_36CharHyphenatedMatch_ReturnsUser()
    {
        // Slow path: 36-char hyphenated Seerr ID must be normalized before comparison.
        var guid = Guid.Parse("aabbccdd-1122-3344-5566-778899aabbcc");
        var users = new List<SeerrUser>
        {
            new() { Id = 99, JellyfinUserId = "aabbccdd-1122-3344-5566-778899aabbcc" }
        };
        var result = Invoke(users, guid);
        Assert.NotNull(result);
        Assert.Equal(99, result!.Id);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_36CharUpperCaseHyphenated_ReturnsUser()
    {
        // Combines both edge cases: 36-char AND uppercase.
        var guid = Guid.Parse("aabbccdd-1122-3344-5566-778899aabbcc");
        var users = new List<SeerrUser>
        {
            new() { Id = 5, JellyfinUserId = "AABBCCDD-1122-3344-5566-778899AABBCC" }
        };
        var result = Invoke(users, guid);
        Assert.NotNull(result);
        Assert.Equal(5, result!.Id);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_WrongLengthId_IsSkipped()
    {
        // BUG GUARD: an ID that is neither 32 nor 36 chars long is not a valid Guid representation and must be silently skipped, not passed to the comparison path (which could produce false matches on partial substrings if the normalisation ever regressed).
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var users = new List<SeerrUser>
        {
            new() { Id = 1, JellyfinUserId = "not-a-guid" },
            new() { Id = 2, JellyfinUserId = "1111111122223333" }, // 16 chars
            new() { Id = 3, JellyfinUserId = "1111111122223333444455555555555511111111" } // 40 chars
        };
        var result = Invoke(users, guid);
        Assert.Null(result);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_NoMatchAmongMultiple_ReturnsNull()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var users = new List<SeerrUser>
        {
            new() { Id = 1, JellyfinUserId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" },
            new() { Id = 2, JellyfinUserId = "22222222-3333-4444-5555-666666666666" }
        };
        var result = Invoke(users, guid);
        Assert.Null(result);
    }

    [Fact]
    public void FindSeerrUserByJellyfinId_ReturnsFirstMatch_WhenDuplicated()
    {
        // Duplicate JellyfinUserIds across Seerr users are a data-corruption in Seerr itself,
        // but the helper must still return the FIRST match deterministically rather than throw.
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var users = new List<SeerrUser>
        {
            new() { Id = 1, JellyfinUserId = "11111111222233334444555555555555" },
            new() { Id = 2, JellyfinUserId = "11111111222233334444555555555555" }
        };
        var result = Invoke(users, guid);
        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
    }

    // BuildAllowedProfileList • filterToDefault=true: emit only the server's ActiveProfileId per server. • filterToDefault=false: emit every profile × every distinct root folder per server.

    [Fact]
    public void BuildAllowedProfileList_NoServers_ReturnsEmpty()
    {
        var result = InvokeBuildAllowedProfileList([], filterToDefault: true);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildAllowedProfileList_FilterToDefault_EmitsOnlyDefaultProfile()
    {
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD"), MakeProfile(200, "4K")]);
        var result = InvokeBuildAllowedProfileList([server], filterToDefault: true);

        Assert.Single(result);
        Assert.Equal(100, result[0].ProfileId);
        Assert.Equal("HD", result[0].ProfileName);
        Assert.True(result[0].IsDefault);
    }

    [Fact]
    public void BuildAllowedProfileList_FilterToDefault_ActiveProfileMissing_SkipsServer()
    {
        // BUG GUARD: when Seerr reports an ActiveProfileId that doesn't exist in Profiles, we MUST NOT synthesize one from Profiles[0].
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 999,
            profiles: [MakeProfile(100, "HD")]);
        var result = InvokeBuildAllowedProfileList([server], filterToDefault: true);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildAllowedProfileList_FullList_EmitsProfileTimesRootFolders()
    {
        // Advanced-user path: each profile × each distinct root folder.
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD"), MakeProfile(200, "4K")],
            rootFolders: ["/movies/hd", "/movies/4k"],
            activeDirectory: "/movies/hd");

        var result = InvokeBuildAllowedProfileList([server], filterToDefault: false);

        // 2 profiles × 2 root folders = 4 entries
        Assert.Equal(4, result.Count);
        Assert.Contains(result, r => r.ProfileId == 100 && r.RootFolder == "/movies/hd" && r.IsDefault);
        Assert.Contains(result, r => r.ProfileId == 100 && r.RootFolder == "/movies/4k" && !r.IsDefault);
        Assert.Contains(result, r => r.ProfileId == 200 && r.RootFolder == "/movies/hd" && !r.IsDefault);
        Assert.Contains(result, r => r.ProfileId == 200 && r.RootFolder == "/movies/4k" && !r.IsDefault);
    }

    [Fact]
    public void BuildAllowedProfileList_FullList_DeduplicatesRootFolderPaths()
    {
        // BUG GUARD: root folders with duplicate paths (mis-configured Seerr) must not
        // produce duplicate emitted entries - the frontend would show the exact same choice twice.
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD")],
            rootFolders: ["/movies", "/movies", "/movies"],
            activeDirectory: "/movies");

        var result = InvokeBuildAllowedProfileList([server], filterToDefault: false);

        Assert.Single(result);
        Assert.Equal("/movies", result[0].RootFolder);
    }

    [Fact]
    public void BuildAllowedProfileList_FullList_EmptyPathsRootFolder_IsSkipped()
    {
        // Root folder entries with empty paths must be filtered out before the Cartesian product. Emitting `RootFolder=""` in the advanced-user path would defeat the "exact-match triple" validation in SubmitMyRequest for that entry.
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD")],
            rootFolders: ["", "/movies", null!],
            activeDirectory: "/movies");

        var result = InvokeBuildAllowedProfileList([server], filterToDefault: false);
        Assert.Single(result);
        Assert.Equal("/movies", result[0].RootFolder);
    }

    [Fact]
    public void BuildAllowedProfileList_FullList_NoRootFolders_FallsBackToActiveDirectory()
    {
        // When RootFolders is empty but ActiveDirectory is set, we use the ActiveDirectory
        // as fallback (backward compat).
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD")],
            rootFolders: [],
            activeDirectory: "/fallback");

        var result = InvokeBuildAllowedProfileList([server], filterToDefault: false);
        Assert.Single(result);
        Assert.Equal("/fallback", result[0].RootFolder);
        Assert.True(result[0].IsDefault);
    }

    [Fact]
    public void BuildAllowedProfileList_FullList_NoRootFoldersAndNoActiveDirectory_EmitsEmptyRootFolder()
    {
        // Complete absence of both fields - we still emit the profile with RootFolder="".
        // The controller then rejects any client-specified rootFolder and falls back to Seerr's server default.
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD")],
            rootFolders: [],
            activeDirectory: "");

        var result = InvokeBuildAllowedProfileList([server], filterToDefault: false);
        Assert.Single(result);
        Assert.Equal(string.Empty, result[0].RootFolder);
    }

    [Fact]
    public void BuildAllowedProfileList_FullList_MultipleServers_MergedIntoOneList()
    {
        // Multi-instance deployments: Radarr-1 with 1 profile+1 root, Radarr-2 with 2 profiles+1 root.
        // Result is a single flat list containing 3 entries in total.
        var s1 = MakeServer(id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD")],
            rootFolders: ["/r1"],
            activeDirectory: "/r1");
        var s2 = MakeServer(id: 2, name: "Radarr-2", activeProfileId: 200,
            profiles: [MakeProfile(200, "HD"), MakeProfile(300, "4K")],
            rootFolders: ["/r2"],
            activeDirectory: "/r2");

        var result = InvokeBuildAllowedProfileList([s1, s2], filterToDefault: false);
        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.ServerId == 1 && r.ProfileId == 100);
        Assert.Contains(result, r => r.ServerId == 2 && r.ProfileId == 200);
        Assert.Contains(result, r => r.ServerId == 2 && r.ProfileId == 300);
    }

    [Fact]
    public void BuildAllowedProfileList_FullList_IsDefault_SetOnlyForActiveProfileAndDirectory()
    {
        // IsDefault must be TRUE only when BOTH profile matches ActiveProfileId AND rootFolder matches ActiveDirectory.
        // A profile with ActiveProfileId but a non-active root folder gets IsDefault=false.
        var server = MakeServer(
            id: 1, name: "Radarr-1", activeProfileId: 100,
            profiles: [MakeProfile(100, "HD")],
            rootFolders: ["/active", "/secondary"],
            activeDirectory: "/active");

        var result = InvokeBuildAllowedProfileList([server], filterToDefault: false);

        Assert.Equal(2, result.Count);
        var activeEntry = Assert.Single(result, r => r.RootFolder == "/active");
        Assert.True(activeEntry.IsDefault);
        var secondaryEntry = Assert.Single(result, r => r.RootFolder == "/secondary");
        Assert.False(secondaryEntry.IsDefault);
    }

    // Test helpers - small factory functions to keep test bodies focused on assertions.

    private static SeerrServiceInfo MakeServer(
        int id,
        string name,
        int activeProfileId,
        IEnumerable<SeerrQualityProfile>? profiles = null,
        IEnumerable<string>? rootFolders = null,
        string activeDirectory = "")
    {
        var s = new SeerrServiceInfo
        {
            Id = id,
            Name = name,
            ActiveProfileId = activeProfileId,
            ActiveDirectory = activeDirectory
        };
        if (profiles is not null)
        {
            foreach (var p in profiles)
            {
                s.Profiles.Add(p);
            }
        }
        if (rootFolders is not null)
        {
            var index = 1;
            foreach (var rf in rootFolders)
            {
                s.RootFolders.Add(new SeerrRootFolder { Id = index++, Path = rf });
            }
        }
        return s;
    }

    private static SeerrQualityProfile MakeProfile(int id, string name)
        => new() { Id = id, Name = name };

    // Reflection glue

    private static SeerrUser? Invoke(IReadOnlyList<SeerrUser> seerrUsers, Guid jellyfinUserId)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "FindSeerrUserByJellyfinId",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (SeerrUser?)method!.Invoke(null, [seerrUsers, jellyfinUserId]);
    }

    private static List<AllowedQualityProfile> InvokeBuildAllowedProfileList(
        IReadOnlyList<SeerrServiceInfo> services,
        bool filterToDefault)
    {
        var method = typeof(SeerrDiscoveryService).GetMethod(
            "BuildAllowedProfileList",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (List<AllowedQualityProfile>)method!.Invoke(null, [services, filterToDefault])!;
    }
}
