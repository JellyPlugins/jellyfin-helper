using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the Trakt per-user token store: roundtrip, encryption at rest, remove, unlinked reads, and
///     persistence across store instances backed by the same file.
/// </summary>
public sealed class TraktUserStoreTests : IDisposable
{
    private readonly string _dataPath;
    private readonly Jellyfin.Plugin.JellyfinHelper.Services.Security.ISecretProtector _secretProtector
        = TestMockFactory.CreateSecretProtector();

    public TraktUserStoreTests()
    {
        _dataPath = Path.Join(Path.GetTempPath(), "jfh-trakt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataPath))
            {
                Directory.Delete(_dataPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private TraktUserStore CreateStore()
        => new(_secretProtector, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<TraktUserStore>().Object, _dataPath);

    private string TokenFilePath => Path.Join(_dataPath, "jellyfin-helper-trakt-tokens.json");

    [Fact]
    public async Task SaveThenGet_RoundTripsDecryptedTokens()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var expiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await store.SaveAsync(userId, new TraktUserToken { AccessToken = "acc", RefreshToken = "ref", ExpiresAtUtc = expiry }, CancellationToken.None);

        var got = store.Get(userId);
        Assert.NotNull(got);
        Assert.Equal("acc", got!.AccessToken);
        Assert.Equal("ref", got.RefreshToken);
        Assert.Equal(expiry, got.ExpiresAtUtc);
        Assert.True(got.IsLinked);
    }

    [Fact]
    public async Task Save_EncryptsTokensAtRest()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        await store.SaveAsync(userId, new TraktUserToken { AccessToken = "plaintext-access", RefreshToken = "plaintext-refresh" }, CancellationToken.None);

        var onDisk = await File.ReadAllTextAsync(TokenFilePath);

        // The raw file must never contain the plaintext token values.
        Assert.DoesNotContain("plaintext-access", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-refresh", onDisk, StringComparison.Ordinal);

        // And the stored values must be in protected form.
        using var doc = JsonDocument.Parse(onDisk);
        var entry = doc.RootElement.EnumerateObject().First().Value;
        Assert.True(_secretProtector.IsProtected(entry.GetProperty("AccessToken").GetString()));
        Assert.True(_secretProtector.IsProtected(entry.GetProperty("RefreshToken").GetString()));
    }

    [Fact]
    public void Get_UnlinkedUser_ReturnsNull()
    {
        var store = CreateStore();
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public async Task Remove_DeletesTheUsersToken()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        await store.SaveAsync(userId, new TraktUserToken { AccessToken = "a", RefreshToken = "r" }, CancellationToken.None);

        await store.RemoveAsync(userId, CancellationToken.None);

        Assert.Null(store.Get(userId));
    }

    [Fact]
    public async Task Remove_UnknownUser_IsNoOp()
    {
        var store = CreateStore();
        // Must not throw when removing a user that was never stored.
        await store.RemoveAsync(Guid.NewGuid(), CancellationToken.None);
    }

    [Fact]
    public async Task Tokens_PersistAcrossStoreInstances()
    {
        var userId = Guid.NewGuid();
        var first = CreateStore();
        await first.SaveAsync(userId, new TraktUserToken { AccessToken = "acc", RefreshToken = "ref" }, CancellationToken.None);

        // A fresh store over the same file must read the persisted token back.
        var second = CreateStore();
        var got = second.Get(userId);
        Assert.NotNull(got);
        Assert.Equal("acc", got!.AccessToken);
        Assert.Equal("ref", got.RefreshToken);
    }

    [Fact]
    public async Task Save_IsolatesTokensPerUser()
    {
        var store = CreateStore();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await store.SaveAsync(userA, new TraktUserToken { AccessToken = "a-acc", RefreshToken = "a-ref" }, CancellationToken.None);
        await store.SaveAsync(userB, new TraktUserToken { AccessToken = "b-acc", RefreshToken = "b-ref" }, CancellationToken.None);

        Assert.Equal("a-acc", store.Get(userA)!.AccessToken);
        Assert.Equal("b-acc", store.Get(userB)!.AccessToken);
    }

    [Fact]
    public async Task Save_OverwritesExistingToken()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();

        await store.SaveAsync(userId, new TraktUserToken { AccessToken = "old", RefreshToken = "old-ref" }, CancellationToken.None);
        await store.SaveAsync(userId, new TraktUserToken { AccessToken = "new", RefreshToken = "new-ref" }, CancellationToken.None);

        Assert.Equal("new", store.Get(userId)!.AccessToken);
    }

    [Fact]
    public void Constructor_Throws_OnNullDependencies()
    {
        var log = TestMockFactory.CreatePluginLogService();
        var logger = TestMockFactory.CreateLogger<TraktUserStore>().Object;
        Assert.Throws<ArgumentNullException>(() => new TraktUserStore(null!, log, logger, _dataPath));
    }
}
