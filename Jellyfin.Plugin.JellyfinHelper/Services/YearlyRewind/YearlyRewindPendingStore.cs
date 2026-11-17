using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Retains an immutable pending publication across restarts, separate from the shared activity journal.</summary>
internal sealed class YearlyRewindPendingStore
{
    private readonly string _root;

    internal YearlyRewindPendingStore(string dataPath) => _root = Path.Join(dataPath, "yearly-rewind");

    internal YearlyRewindSnapshot? LoadPending(Guid userId)
    {
        var path = PendingPath(userId);
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        if (stream.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Pending rewind exceeds its storage limit.");
        }

        var snapshot = JsonSerializer.Deserialize<YearlyRewindSnapshot>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException("Invalid pending rewind.");
        return snapshot.UserId == userId ? snapshot : throw new InvalidDataException("Pending rewind belongs to another user.");
    }

    internal void Seal(YearlyRewindSnapshot snapshot) => AtomicFile.WriteAllText(PendingPath(snapshot.UserId), JsonSerializer.Serialize(snapshot, JsonDefaults.Options));

    internal void Complete(Guid userId)
    {
        if (File.Exists(PendingPath(userId)))
        {
            File.Delete(PendingPath(userId));
        }
    }

    internal Guid[] KnownUsers()
    {
        var directory = Path.Join(_root, "pending");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension).Select(name => Guid.TryParseExact(name, "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToArray();
    }

    internal void DeleteUser(Guid userId) => Complete(userId);

    private string PendingPath(Guid userId) => Path.Join(_root, "pending", userId.ToString("N") + ".json");
}
