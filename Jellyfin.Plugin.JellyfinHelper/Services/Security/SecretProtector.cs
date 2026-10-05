using System;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Security;

/// <summary>
///     Data Protection backed implementation of <see cref="ISecretProtector"/>. The underlying keyring is
///     persisted to the plugin data path (see PluginServiceRegistrator) and is not machine bound, so a
///     data path that travels with its keyring stays decryptable on another host.
/// </summary>
public sealed class SecretProtector : ISecretProtector
{
    // Marks a stored value as ciphertext this protector produced. A value lacking the marker is read as
    // legacy plaintext so secrets written before encryption keep working until their next save rewrites them.
    private const string Prefix = "DP::";

    private readonly IDataProtector _protector;
    private readonly ILogger<SecretProtector> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SecretProtector"/> class.
    /// </summary>
    /// <param name="provider">The Data Protection provider supplied by the host DI container.</param>
    /// <param name="logger">Logger for decrypt failures. Never receives secret values.</param>
    public SecretProtector(IDataProtectionProvider provider, ILogger<SecretProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The purpose string scopes the derived key. It must stay stable across releases or previously
        // protected secrets become undecryptable.
        _protector = provider.CreateProtector("JellyfinHelper.Secrets.v1");
    }

    /// <inheritdoc />
    public string Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return string.Empty;
        }

        if (IsProtected(plaintext))
        {
            return plaintext;
        }

        return Prefix + _protector.Protect(plaintext);
    }

    /// <inheritdoc />
    public string Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return string.Empty;
        }

        if (!IsProtected(stored))
        {
            // Legacy plaintext written before encryption was introduced. Returned as-is; the next save
            // through Protect rewrites it in encrypted form.
            return stored;
        }

        var payload = stored[Prefix.Length..];
        try
        {
            return _protector.Unprotect(payload);
        }
        catch (CryptographicException ex)
        {
            // Fail closed: a blob we cannot decrypt (keyring lost or rotated) must never surface as a
            // ciphertext that a caller might treat as a real key. The value itself is never logged.
            _logger.LogWarning(ex, "[SecretProtector] Failed to decrypt a stored secret; treating it as absent");
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public bool IsProtected(string? value)
        => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);
}
