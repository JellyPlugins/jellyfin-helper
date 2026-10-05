using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Default <see cref="ITraktUserStore"/>: a single JSON file under the plugin data path holding one entry
///     per linked user. Access and refresh tokens are encrypted with <see cref="ISecretProtector"/> before
///     they are written and decrypted on read, so a token value is never on disk or in a log in plain text.
/// </summary>
public sealed class TraktUserStore : ITraktUserStore
{
    private const string LogSource = "Trakt";

    // Guards both the in-memory map and the lazy load so concurrent requests cannot double-load or observe a
    // half-populated map. The store is request-driven and tiny, so a single lock is cheaper than finer locking.
    private readonly Lock _gate = new();
    private readonly ISecretProtector _secretProtector;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<TraktUserStore> _logger;
    private readonly string? _filePath;

    // Stored encrypted (DP:: ciphertext). Keyed by user id in "N" (32 hex) form so the JSON is stable.
    private Dictionary<string, StoredEntry>? _entries;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktUserStore"/> class.
    /// </summary>
    /// <param name="secretProtector">Encrypts tokens at rest and decrypts them on read.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger. Never receives token values.</param>
    /// <param name="dataPath">The plugin data path, or null when unavailable (tokens then live in memory only).</param>
    public TraktUserStore(
        ISecretProtector secretProtector,
        IPluginLogService pluginLog,
        ILogger<TraktUserStore> logger,
        string? dataPath)
    {
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = string.IsNullOrEmpty(dataPath) ? null : Path.Join(dataPath, "jellyfin-helper-trakt-tokens.json");
    }

    /// <inheritdoc />
    public TraktUserToken? GetToken(Guid userId)
    {
        var key = Key(userId);
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries is null || !_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            // Decrypt at the boundary so callers only ever see plaintext. A blob that fails to decrypt
            // (keyring lost) surfaces as empty tokens, which IsLinked treats as unlinked.
            return new TraktUserToken
            {
                AccessToken = _secretProtector.Unprotect(entry.AccessToken),
                RefreshToken = _secretProtector.Unprotect(entry.RefreshToken),
                ExpiresAtUtc = entry.ExpiresAtUtc,
            };
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetLinkedUserIds()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries is null || _entries.Count == 0)
            {
                return [];
            }

            // Keys are stored in "N" (32 hex) form; skip any that do not parse rather than throw.
            return _entries.Keys
                .Select(key => Guid.TryParseExact(key, "N", out var id) ? (Guid?)id : null)
                .Where(parsed => parsed.HasValue)
                .Select(parsed => parsed.GetValueOrDefault())
                .ToList();
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(Guid userId, TraktUserToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var key = Key(userId);

        string json;
        lock (_gate)
        {
            EnsureLoaded();
            _entries ??= new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
            _entries[key] = new StoredEntry
            {
                AccessToken = _secretProtector.Protect(token.AccessToken),
                RefreshToken = _secretProtector.Protect(token.RefreshToken),
                ExpiresAtUtc = token.ExpiresAtUtc,
            };
            json = Serialize(_entries);
        }

        return PersistAsync(json, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var key = Key(userId);

        string json;
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries is null || !_entries.Remove(key))
            {
                return Task.CompletedTask;
            }

            json = Serialize(_entries);
        }

        return PersistAsync(json, cancellationToken);
    }

    private static string Key(Guid userId) => userId.ToString("N");

    private static string Serialize(Dictionary<string, StoredEntry> entries)
        => JsonSerializer.Serialize(entries);

    private async Task PersistAsync(string json, CancellationToken cancellationToken)
    {
        if (_filePath is null)
        {
            // No data path (tests, early startup): the in-memory map is authoritative; nothing to persist.
            return;
        }

        try
        {
            await AtomicFile.WriteAllTextAsync(_filePath, json, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best effort; the in-memory map still serves the current process. Surface it so a
            // read-only data dir is diagnosable, but never fail the OAuth flow over a disk write.
            _pluginLog.LogWarning(LogSource, "Failed to persist Trakt tokens to disk.", ex, _logger);
        }
    }

    // Loads the JSON file once on first access. A missing or unreadable file yields an empty map so the store
    // degrades to "no linked users" rather than throwing into a request.
    private void EnsureLoaded()
    {
        if (_entries is not null)
        {
            return;
        }

        if (_filePath is null || !File.Exists(_filePath))
        {
            _entries = new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<Dictionary<string, StoredEntry>>(json)
                ?? new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _pluginLog.LogWarning(LogSource, "Failed to load Trakt tokens from disk; starting empty.", ex, _logger);
            _entries = new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
        }
    }

    // On-disk shape. Token fields hold DP:: ciphertext; never plaintext.
    private sealed class StoredEntry
    {
        public string AccessToken { get; set; } = string.Empty;

        public string RefreshToken { get; set; } = string.Empty;

        public DateTime ExpiresAtUtc { get; set; }
    }
}
