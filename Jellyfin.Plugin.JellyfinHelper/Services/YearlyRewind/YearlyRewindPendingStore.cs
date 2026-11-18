using System;
using System.IO;
using System.Linq;
using System.Text;
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

        using (var stream = File.OpenRead(path))
        {
            if (stream.Length <= YearlyRewindStore.MaxSnapshotBytes)
            {
                var snapshot = JsonSerializer.Deserialize<YearlyRewindSnapshot>(stream, JsonDefaults.Options)
                    ?? throw new InvalidDataException("Invalid pending rewind.");
                return snapshot.UserId == userId ? snapshot : throw new InvalidDataException("Pending rewind belongs to another user.");
            }
        }

        // Retain the rejected input for diagnosis without blocking this or later annual cutoffs.
        File.Move(path, path + ".oversized-" + Guid.NewGuid().ToString("N"));
        return null;
    }

    internal void Seal(YearlyRewindSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonDefaults.Options);
        if (Encoding.UTF8.GetByteCount(json) > YearlyRewindStore.MaxSnapshotBytes)
        {
            throw new InvalidDataException("Pending rewind exceeds its storage limit.");
        }

        AtomicFile.WriteAllText(PendingPath(snapshot.UserId), json);
    }

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
