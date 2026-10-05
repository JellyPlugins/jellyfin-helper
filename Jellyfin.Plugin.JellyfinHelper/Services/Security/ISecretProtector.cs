using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Security;

/// <summary>
///     Encrypts and decrypts secrets at rest (API keys, OAuth tokens) using ASP.NET Core Data Protection.
///     Protected values carry a prefix marker so a stored value can be told apart from a legacy plaintext
///     value, which lets secrets written before encryption was introduced be read and lazily re-encrypted
///     on the next save without a manual migration step.
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    ///     Encrypts a plaintext secret for storage. An empty or whitespace input returns empty so callers
    ///     can round-trip "no secret" without producing a protected blob. An already-protected value is
    ///     returned unchanged so Protect is idempotent across repeated saves.
    /// </summary>
    /// <param name="plaintext">The plaintext secret, or null/empty for "no secret".</param>
    /// <returns>The protected, prefix-marked value, or empty string when there is nothing to protect.</returns>
    string Protect(string? plaintext);

    /// <summary>
    ///     Decrypts a stored secret. A value without the protection prefix is treated as legacy plaintext
    ///     and returned unchanged, so secrets stored before encryption keep working until the next save.
    ///     A protected value that cannot be decrypted (for example a keyring rotated out from under it)
    ///     returns empty so callers fail closed rather than surfacing a ciphertext.
    /// </summary>
    /// <param name="stored">The stored value (protected, legacy plaintext, or null/empty).</param>
    /// <returns>The plaintext secret, or empty string when there is nothing to decrypt.</returns>
    string Unprotect(string? stored);

    /// <summary>
    ///     Reports whether a stored value is already in protected form.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <returns><see langword="true"/> when the value carries the protection prefix.</returns>
    bool IsProtected(string? value);
}
