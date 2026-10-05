using System;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Security;

/// <summary>
///     Verifies the Data Protection secret wrapper: roundtrip, idempotency, legacy plaintext passthrough
///     (the lazy migration contract), empty handling, and fail-closed behavior on undecryptable blobs.
/// </summary>
public sealed class SecretProtectorTests
{
    private static SecretProtector CreateProtector(IDataProtectionProvider? provider = null)
        => new(provider ?? new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsOriginalValue()
    {
        var protector = CreateProtector();
        const string secret = "trakt-access-token-abc123";

        var stored = protector.Protect(secret);

        Assert.NotEqual(secret, stored);
        Assert.True(protector.IsProtected(stored));
        Assert.Equal(secret, protector.Unprotect(stored));
    }

    [Fact]
    public void Protect_IsIdempotent_WhenGivenAnAlreadyProtectedValue()
    {
        var protector = CreateProtector();

        var once = protector.Protect("my-api-key");
        var twice = protector.Protect(once);

        // Double-protecting must not nest ciphertext, or a second save would corrupt the stored secret.
        Assert.Equal(once, twice);
        Assert.Equal("my-api-key", protector.Unprotect(twice));
    }

    [Fact]
    public void Unprotect_ReturnsLegacyPlaintextUnchanged()
    {
        var protector = CreateProtector();

        // A value stored before encryption was introduced has no prefix and must be read as-is.
        Assert.Equal("legacy-plaintext-key", protector.Unprotect("legacy-plaintext-key"));
        Assert.False(protector.IsProtected("legacy-plaintext-key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Protect_ReturnsEmpty_ForNullOrWhitespace(string? input)
    {
        Assert.Equal(string.Empty, CreateProtector().Protect(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unprotect_ReturnsEmpty_ForNullOrWhitespace(string? input)
    {
        Assert.Equal(string.Empty, CreateProtector().Unprotect(input));
    }

    [Fact]
    public void Unprotect_FailsClosed_WhenCiphertextCannotBeDecrypted()
    {
        // A protected blob from one keyring must not decrypt under a different keyring. Fail closed to
        // empty rather than surfacing the ciphertext as if it were a real secret.
        var writer = CreateProtector();
        var stored = writer.Protect("secret-value");

        var otherKeyring = CreateProtector(new EphemeralDataProtectionProvider());

        Assert.Equal(string.Empty, otherKeyring.Unprotect(stored));
    }

    [Fact]
    public void IsProtected_DistinguishesProtectedFromPlaintext()
    {
        var protector = CreateProtector();

        Assert.True(protector.IsProtected(protector.Protect("x")));
        Assert.False(protector.IsProtected("plain"));
        Assert.False(protector.IsProtected(null));
        Assert.False(protector.IsProtected(string.Empty));
    }

    [Fact]
    public void Constructor_Throws_OnNullProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new SecretProtector(null!, NullLogger<SecretProtector>.Instance));
    }
}
