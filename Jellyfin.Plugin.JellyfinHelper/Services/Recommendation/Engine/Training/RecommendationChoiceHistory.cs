using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>Bounded observed-choice replay across playlist generations; publication never creates or renews evidence.</summary>
internal sealed class RecommendationChoiceHistory
{
    private const int MaximumEntries = 4096;
    private const int MaximumPerUser = 1024;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid User, Guid Title), TrainingExample> _entries = [];
    private readonly IFileSystem? _fileSystem;
    private readonly string? _path;

    internal RecommendationChoiceHistory(IFileSystem? fileSystem = null, string? path = null)
    {
        _fileSystem = fileSystem;
        _path = path is not null && Path.IsPathFullyQualified(path) ? path : null;
        Load();
    }

    internal List<TrainingExample> Merge(
        IReadOnlyList<TrainingExample> current,
        IReadOnlySet<Guid> activeUsers,
        DateTime referenceTime,
        IReadOnlySet<(Guid User, Guid Title)>? engagedTitles = null)
    {
        var engaged = current.Where(row => row.IsObserved && row.HasStableObservationId).Select(Key).ToHashSet();
        engaged.UnionWith(engagedTitles ?? new HashSet<(Guid, Guid)>());
        // Withdraw comparisons from both fitting and admission. LastPlayedDate cannot
        // recover an alternative's first engagement, and a later watch must not revive
        // a prior unobserved comparison on the next publication or restart.
        current = current.Where(row => !row.IsContrastSample || !engaged.Contains(Key(row)))
            .Select(row => WithoutEngagedAlternatives(row, engaged, referenceTime)).ToArray();
        var observed = current.Where(row => row.IsObserved && row.HasStableObservationId)
            .GroupBy(Key).ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.GeneratedAtUtc).First());
        var result = current.ToList();
        lock (_gate)
        {
            foreach (var key in _entries.Keys.ToArray())
            {
                _entries[key] = WithoutEngagedAlternatives(_entries[key], engaged, referenceTime);
            }

            foreach (var key in _entries.Keys.Where(key => !activeUsers.Contains(key.User) || !observed.TryGetValue(key, out var row)
                || row.Label <= .5 || !Valid(_entries[key], referenceTime)).ToArray())
            {
                _entries.Remove(key);
            }

            foreach (var row in current.Where(row => activeUsers.Contains(row.UserId) && Valid(row, referenceTime)
                && observed[Key(row)].Label > .5))
            {
                var key = Key(row);
                if (!_entries.TryGetValue(key, out var previous) || row.Choice!.ChosenAtUtc > previous.Choice!.ChosenAtUtc)
                {
                    _entries[key] = row.Copy();
                }
            }

            var retained = BalancedEntries();
            _entries.Clear();
            foreach (var row in retained)
            {
                _entries[Key(row)] = row;
                if (!current.Any(item => Key(item) == Key(row) && item.Choice?.IsValid(item.ObservationId, referenceTime) == true))
                {
                    AddReplay(result, row, referenceTime, engaged);
                }
            }
        }

        return result;
    }

    private static TrainingExample WithoutEngagedAlternatives(TrainingExample row, HashSet<(Guid User, Guid Title)> engaged, DateTime referenceTime)
    {
        if (row.Choice is not { } choice || !choice.IsValid(row.ObservationId, referenceTime))
        {
            return row;
        }

        var alternatives = choice.Alternatives.Where(alternative => !engaged.Contains((row.UserId, alternative.ObservationId))).ToImmutableArray();
        if (alternatives.Length == choice.Alternatives.Length)
        {
            return row;
        }

        var copy = row.Copy();
        copy.Choice = alternatives.IsEmpty ? null : (choice with { Alternatives = alternatives }).Copy();
        return copy;
    }

    private static void AddReplay(List<TrainingExample> result, TrainingExample row, DateTime referenceTime, HashSet<(Guid User, Guid Title)> engaged)
    {
        // Two feature views remain one observed title, not two watches. Preserve the
        // caller's prediction and labels while sharing its original sample-weight budget.
        var current = result.Select((item, index) => (Item: item, Index: index))
            .Where(item => item.Item.IsObserved && item.Item.HasStableObservationId && Key(item.Item) == Key(row)).ToArray();
        var budget = current.Sum(item => item.Item.SampleWeight);
        if (budget <= 0)
        {
            return;
        }

        var scale = budget / (budget + row.SampleWeight);
        foreach (var item in current)
        {
            var copy = item.Item.Copy();
            copy.SampleWeight *= scale;
            result[item.Index] = copy;
        }

        var replay = row.Copy();
        replay.SampleWeight *= scale;
        result.Add(replay);
        if (row.IsPermanentHoldout)
        {
            return;
        }

        var alternatives = row.Choice!.Alternatives.Where(alternative => !engaged.Contains((row.UserId, alternative.ObservationId)))
            .OrderBy(alternative => alternative.ObservationId)
            .Take(RecommendationChoiceBuilder.FittingAlternativeLimit(row.Choice.RecommendedAtUtc, referenceTime)).ToArray();
        foreach (var alternative in alternatives)
        {
            result.Add(new TrainingExample
            {
                UserId = row.UserId,
                ObservationId = alternative.ObservationId,
                HasStableObservationId = true,
                IsObserved = false,
                IsContrastSample = true,
                Label = EngineConstants.ExposureLabel,
                SampleWeight = RecommendationChoiceBuilder.ContrastWeightBudget / alternatives.Length,
                GeneratedAtUtc = row.Choice.RecommendedAtUtc,
                Features = alternative.Features.Copy()
            });
        }
    }

    private TrainingExample[] BalancedEntries()
    {
        var lanes = _entries.Values.GroupBy(row => row.UserId).OrderBy(group => group.Key)
            .Select(group => new Queue<TrainingExample>(group.OrderByDescending(row => row.IsPermanentHoldout)
                .ThenByDescending(row => row.Choice!.ChosenAtUtc).Take(MaximumPerUser))).ToArray();
        var retained = new List<TrainingExample>();
        while (retained.Count < MaximumEntries && lanes.Any(lane => lane.Count > 0))
        {
            foreach (var lane in lanes.Where(lane => lane.Count > 0))
            {
                retained.Add(lane.Dequeue());
                if (retained.Count == MaximumEntries)
                {
                    break;
                }
            }
        }

        return retained.ToArray();
    }

    internal bool Save()
    {
        if (_fileSystem is null || _path is null)
        {
            return false;
        }

        try
        {
            TrainingExample[] rows;
            lock (_gate)
            {
                rows = BalancedEntries().Select(row => row.Copy()).ToArray();
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(new StoredChoices(CandidateFeatures.FeatureCount, rows), JsonDefaults.Options);
            while (json.Length > MaximumFileBytes && rows.Length > 0)
            {
                rows = rows.Take(rows.Length / 2).ToArray();
                json = JsonSerializer.SerializeToUtf8Bytes(new StoredChoices(CandidateFeatures.FeatureCount, rows), JsonDefaults.Options);
            }

            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _fileSystem.File.WriteAllBytes(_path + ".tmp", json);
            _fileSystem.File.Move(_path + ".tmp", _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            return false;
        }
    }

    private void Load()
    {
        if (_fileSystem is null || _path is null)
        {
            return;
        }

        try
        {
            if (!_fileSystem.File.Exists(_path) || _fileSystem.FileInfo.New(_path).Length > MaximumFileBytes)
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<StoredChoices>(_fileSystem.File.ReadAllText(_path), JsonDefaults.Options);
            if (saved?.FeatureCount != CandidateFeatures.FeatureCount)
            {
                return;
            }

            foreach (var row in (saved.Examples ?? []).Where(row => row is not null && Valid(row, DateTime.UtcNow)).Take(MaximumEntries))
            {
                _entries[Key(row)] = row.Copy();
            }
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            // Corrupt optional replay is never a reason to discard the current real watches.
        }
    }

    private static bool Valid(TrainingExample row, DateTime referenceTime)
        => row.UserId != Guid.Empty && row.IsObserved && row.HasStableObservationId && row.Label > .5
            && row.Features is not null && row.Choice?.IsValid(row.ObservationId, referenceTime) == true
            && row.GeneratedAtUtc == row.Choice.RecommendedAtUtc
            && row.ComputeEffectiveWeight(referenceTime) >= LearnedScoringStrategy.MinSampleWeight;

    private static (Guid User, Guid Title) Key(TrainingExample row) => (row.UserId, row.ObservationId);

    private sealed record StoredChoices(int FeatureCount, TrainingExample[] Examples);
}
