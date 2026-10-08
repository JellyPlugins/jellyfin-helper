using System;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt.External;

/// <summary>
///     Verifies <see cref="OfficialTraktPluginReader"/>: presence probing never throws, and token reads of the
///     official Trakt plugin's config file are strictly read-only, match on the Jellyfin user id, skip expired or
///     unusable tokens, and fail closed on a missing, malformed, locked, or hostile config.
/// </summary>
public sealed class OfficialTraktPluginReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public OfficialTraktPluginReaderTests()
    {
        _dir = Path.Join(Path.GetTempPath(), "jfh-trakt-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _configPath = Path.Join(_dir, OfficialTraktPluginGuids.ConfigFileName);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private OfficialTraktPluginReader Create(Func<bool> isPresent, string? configPath)
        => new(
            isPresent,
            configPath,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<OfficialTraktPluginReader>().Object);

    private void WriteConfig(string xml) => File.WriteAllText(_configPath, xml);

    private static string OneUser(Guid userId, string access, string refresh, string expiration) =>
        $"""
         <?xml version="1.0"?>
         <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
           <TraktUsers>
             <TraktUser>
               <AccessToken>{access}</AccessToken>
               <RefreshToken>{refresh}</RefreshToken>
               <LinkedMbUserId>{userId}</LinkedMbUserId>
               <AccessTokenExpiration>{expiration}</AccessTokenExpiration>
             </TraktUser>
           </TraktUsers>
         </PluginConfiguration>
         """;

    private static string Iso(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    [Fact]
    public void IsPresent_DelegateTrue_ReturnsTrue()
    {
        var reader = Create(() => true, _configPath);
        Assert.True(reader.IsPresent());
    }

    [Fact]
    public void IsPresent_DelegateThrows_TreatedAsAbsent()
    {
        // A presence probe must never leak an exception into discovery/UI.
        var reader = Create(() => throw new InvalidOperationException("boom"), _configPath);
        Assert.False(reader.IsPresent());
    }

    [Fact]
    public void TryGetToken_LinkedUserWithLiveToken_ReturnsToken()
    {
        var userId = Guid.NewGuid();
        WriteConfig(OneUser(userId, "acc-123", "ref-123", Iso(Now.AddHours(2))));

        var reader = Create(() => true, _configPath);
        var token = reader.TryGetToken(userId, Now);

        Assert.NotNull(token);
        Assert.Equal("acc-123", token!.AccessToken);
        Assert.Equal("ref-123", token.RefreshToken);
        Assert.True(token.AccessTokenExpiration > Now);
    }

    [Fact]
    public void TryGetToken_ExpiredToken_SkippedReturnsNull()
    {
        // Expired tokens are left for the official plugin to rotate; the reader never refreshes.
        var userId = Guid.NewGuid();
        WriteConfig(OneUser(userId, "acc", "ref", Iso(Now.AddMinutes(-1))));

        var reader = Create(() => true, _configPath);
        Assert.Null(reader.TryGetToken(userId, Now));
    }

    [Fact]
    public void TryGetToken_UserNotLinked_ReturnsNull()
    {
        WriteConfig(OneUser(Guid.NewGuid(), "acc", "ref", Iso(Now.AddHours(1))));

        var reader = Create(() => true, _configPath);
        Assert.Null(reader.TryGetToken(Guid.NewGuid(), Now));
    }

    [Fact]
    public void TryGetToken_LinkedUserButEmptyAccessToken_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        WriteConfig(OneUser(userId, string.Empty, "ref", Iso(Now.AddHours(1))));

        var reader = Create(() => true, _configPath);
        Assert.Null(reader.TryGetToken(userId, Now));
    }

    [Fact]
    public void TryGetToken_UnparseableExpiration_TreatedAsExpired()
    {
        var userId = Guid.NewGuid();
        WriteConfig(OneUser(userId, "acc", "ref", "not-a-date"));

        var reader = Create(() => true, _configPath);
        Assert.Null(reader.TryGetToken(userId, Now));
    }

    [Fact]
    public void TryGetToken_SelectsMatchingUserAmongMany()
    {
        var wanted = Guid.NewGuid();
        var other = Guid.NewGuid();
        WriteConfig(
            $"""
             <?xml version="1.0"?>
             <PluginConfiguration>
               <TraktUsers>
                 <TraktUser>
                   <AccessToken>other-acc</AccessToken>
                   <RefreshToken>other-ref</RefreshToken>
                   <LinkedMbUserId>{other}</LinkedMbUserId>
                   <AccessTokenExpiration>{Iso(Now.AddHours(1))}</AccessTokenExpiration>
                 </TraktUser>
                 <TraktUser>
                   <AccessToken>wanted-acc</AccessToken>
                   <RefreshToken>wanted-ref</RefreshToken>
                   <LinkedMbUserId>{wanted}</LinkedMbUserId>
                   <AccessTokenExpiration>{Iso(Now.AddHours(1))}</AccessTokenExpiration>
                 </TraktUser>
               </TraktUsers>
             </PluginConfiguration>
             """);

        var reader = Create(() => true, _configPath);
        var token = reader.TryGetToken(wanted, Now);

        Assert.NotNull(token);
        Assert.Equal("wanted-acc", token!.AccessToken);
    }

    [Fact]
    public void TryGetToken_MissingFile_ReturnsNull()
    {
        var reader = Create(() => true, _configPath); // nothing written
        Assert.Null(reader.TryGetToken(Guid.NewGuid(), Now));
    }

    [Fact]
    public void TryGetToken_NullConfigPath_ReturnsNull()
    {
        var reader = Create(() => true, configPath: null);
        Assert.Null(reader.TryGetToken(Guid.NewGuid(), Now));
    }

    [Fact]
    public void TryGetToken_EmptyUserId_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        WriteConfig(OneUser(userId, "acc", "ref", Iso(Now.AddHours(1))));

        var reader = Create(() => true, _configPath);
        Assert.Null(reader.TryGetToken(Guid.Empty, Now));
    }

    [Fact]
    public void TryGetToken_MalformedXml_FailsClosedReturnsNull()
    {
        WriteConfig("<PluginConfiguration><TraktUsers><TraktUser></broken>");

        var reader = Create(() => true, _configPath);
        Assert.Null(reader.TryGetToken(Guid.NewGuid(), Now));
    }

    [Fact]
    public void TryGetToken_DoctypeXxeAttempt_RejectedReturnsNull()
    {
        // DTD processing is prohibited and the resolver is null, so an external-entity payload cannot be
        // expanded; parsing fails closed to null rather than reading a local file.
        WriteConfig(
            """
            <?xml version="1.0"?>
            <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <PluginConfiguration><TraktUsers><TraktUser>
              <AccessToken>&xxe;</AccessToken>
              <LinkedMbUserId>00000000-0000-0000-0000-000000000001</LinkedMbUserId>
            </TraktUser></TraktUsers></PluginConfiguration>
            """);

        var reader = Create(() => true, _configPath);
        var token = reader.TryGetToken(new Guid("00000000-0000-0000-0000-000000000001"), Now);
        Assert.Null(token);
    }

    [Fact]
    public void TryGetToken_IsReadOnly_LeavesConfigFileUnchanged()
    {
        var userId = Guid.NewGuid();
        var xml = OneUser(userId, "acc", "ref", Iso(Now.AddHours(1)));
        WriteConfig(xml);
        var before = File.ReadAllText(_configPath);

        var reader = Create(() => true, _configPath);
        _ = reader.TryGetToken(userId, Now);

        Assert.Equal(before, File.ReadAllText(_configPath));
    }

    [Fact]
    public void TryGetToken_BareLocalExpiration_InterpretedAsLocalNotUtc()
    {
        // The official plugin serializes DateTime.Now (local) and often with no offset. A bare timestamp must
        // be read against the LOCAL clock. Pick an expiry one hour in the local future and compare against the
        // matching absolute instant: it must be considered live.
        var userId = Guid.NewGuid();
        var localExpiry = DateTime.Now.AddHours(1);
        var bare = localExpiry.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); // no offset
        WriteConfig(OneUser(userId, "acc-local", "ref", bare));

        var reader = Create(() => true, _configPath);
        var token = reader.TryGetToken(userId, DateTimeOffset.Now);

        Assert.NotNull(token);
        Assert.Equal("acc-local", token!.AccessToken);
    }

    [Fact]
    public void TryGetToken_BareLocalExpirationInPast_SkippedAsLocal()
    {
        var userId = Guid.NewGuid();
        var bare = DateTime.Now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        WriteConfig(OneUser(userId, "acc", "ref", bare));

        var reader = Create(() => true, _configPath);
        Assert.Null(reader.TryGetToken(userId, DateTimeOffset.Now));
    }

    [Fact]
    public void TryGetToken_NamespacedConfig_StillParsed()
    {
        // XmlSerializer output can carry a default namespace; the reader matches on LocalName so it stays
        // namespace-agnostic.
        var userId = Guid.NewGuid();
        WriteConfig(
            $"""
             <?xml version="1.0"?>
             <PluginConfiguration xmlns="http://schemas.example/trakt">
               <TraktUsers>
                 <TraktUser>
                   <AccessToken>ns-acc</AccessToken>
                   <RefreshToken>ns-ref</RefreshToken>
                   <LinkedMbUserId>{userId}</LinkedMbUserId>
                   <AccessTokenExpiration>{Iso(Now.AddHours(1))}</AccessTokenExpiration>
                 </TraktUser>
               </TraktUsers>
             </PluginConfiguration>
             """);

        var reader = Create(() => true, _configPath);
        var token = reader.TryGetToken(userId, Now);

        Assert.NotNull(token);
        Assert.Equal("ns-acc", token!.AccessToken);
    }

    [Fact]
    public void GetLinkedUserIds_ReturnsOnlyUsersWithUsableUnexpiredTokens()
    {
        var live1 = Guid.NewGuid();
        var live2 = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var noToken = Guid.NewGuid();
        WriteConfig(
            $"""
             <?xml version="1.0"?>
             <PluginConfiguration>
               <TraktUsers>
                 <TraktUser><AccessToken>a1</AccessToken><LinkedMbUserId>{live1}</LinkedMbUserId><AccessTokenExpiration>{Iso(Now.AddHours(1))}</AccessTokenExpiration></TraktUser>
                 <TraktUser><AccessToken>a2</AccessToken><LinkedMbUserId>{live2}</LinkedMbUserId><AccessTokenExpiration>{Iso(Now.AddDays(1))}</AccessTokenExpiration></TraktUser>
                 <TraktUser><AccessToken>a3</AccessToken><LinkedMbUserId>{expired}</LinkedMbUserId><AccessTokenExpiration>{Iso(Now.AddMinutes(-1))}</AccessTokenExpiration></TraktUser>
                 <TraktUser><AccessToken></AccessToken><LinkedMbUserId>{noToken}</LinkedMbUserId><AccessTokenExpiration>{Iso(Now.AddHours(1))}</AccessTokenExpiration></TraktUser>
               </TraktUsers>
             </PluginConfiguration>
             """);

        var reader = Create(() => true, _configPath);
        var ids = reader.GetLinkedUserIds(Now);

        Assert.Equal(2, ids.Count);
        Assert.Contains(live1, ids);
        Assert.Contains(live2, ids);
        Assert.DoesNotContain(expired, ids);
        Assert.DoesNotContain(noToken, ids);
    }

    [Fact]
    public void GetLinkedUserIds_MissingFile_ReturnsEmpty()
    {
        var reader = Create(() => true, _configPath); // nothing written
        Assert.Empty(reader.GetLinkedUserIds(Now));
    }

    [Fact]
    public void GetLinkedUserIds_MalformedXml_FailsClosedReturnsEmpty()
    {
        WriteConfig("<PluginConfiguration><TraktUsers><TraktUser></broken>");
        var reader = Create(() => true, _configPath);
        Assert.Empty(reader.GetLinkedUserIds(Now));
    }
}
