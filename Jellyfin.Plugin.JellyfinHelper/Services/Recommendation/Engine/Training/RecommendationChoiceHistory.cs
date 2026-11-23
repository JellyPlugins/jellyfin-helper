using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Lock _persistenceGate = new();
    private readonly Lock _writerGate = new();
    private readonly Dictionary<(Guid User, Guid Title), TrainingExample> _entries = [];
    private readonly IFileSystem? _fileSystem;
    private readonly string? _path;
    private Task<bool>? _pendingSave;
    private long _generation;
    private long _savedGeneration;

    internal RecommendationChoiceHistory(IFileSystem? fileSystem = null, string? path = null)
    {
        _fileSystem = fileSystem;
        _path = path is not null && Path.IsPathFullyQualified(path) ? path : null;
        Load();
    }

    internal TimeProvider WaitTimeProvider { get; init; } = TimeProvider.System;

    internal List<TrainingExample> Merge(
        IReadOnlyList<TrainingExample> current,
        IReadOnlySet<Guid> activeUsers,
        DateTime referenceTime,
        IReadOnlySet<(Guid User, Guid Title)>? engagedTitles = null,
        IReadOnlySet<(Guid User, Guid Title)>? eligibleTitles = null,
        Func<Guid, Guid, bool>? canUseLibraryItem = null,
        Func<Guid, Guid, Guid, bool>? canUseCapturedItem = null)
    {
        var engaged = current.Where(row => row.IsObserved && row.HasStableObservationId).Select(Key).ToHashSet();
        engaged.UnionWith(engagedTitles ?? new HashSet<(Guid, Guid)>());
        // Withdraw comparisons from both fitting and admission. LastPlayedDate cannot
        // recover an alternative's first engagement, and a later watch must not revive
        // a prior unobserved comparison on the next publication or restart.
        current = current.Where(row => !row.IsContrastSample || (!engaged.Contains(Key(row)) && (eligibleTitles is null || eligibleTitles.Contains(Key(row)))))
            .Select(row => WithoutEngagedAlternatives(row, engaged, referenceTime, eligibleTitles, canUseLibraryItem, canUseCapturedItem)).ToArray();
        var observed = current.Where(row => row.IsObserved && row.HasStableObservationId)
            .GroupBy(Key).ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.GeneratedAtUtc).First());
        var result = current.ToList();
        var replayed = new List<TrainingExample>();
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(key => activeUsers.Contains(key.User)).ToArray())
            {
                _entries[key] = PrepareStoredChoice(_entries[key], engaged, referenceTime, eligibleTitles, canUseLibraryItem, canUseCapturedItem);
            }

            foreach (var key in _entries.Keys.Where(key => !activeUsers.Contains(key.User) || !observed.TryGetValue(key, out var row)
                || row.Label <= .5 || !Valid(_entries[key], referenceTime)).ToArray())
            {
                _entries.Remove(key);
            }

            CaptureCurrentChoices(current, activeUsers, observed, referenceTime);

            var retained = BalancedEntries();
            _entries.Clear();
            foreach (var row in retained)
            {
                _entries[Key(row)] = row;
                var replay = WithoutEngagedAlternatives(row, engaged, referenceTime, eligibleTitles, canUseLibraryItem, canUseCapturedItem);
                if (!current.Any(item => Key(item) == Key(row) && item.Choice?.IsValid(item.ObservationId, referenceTime) == true)
                    && Valid(replay, referenceTime) && AddChoiceView(result, replay, referenceTime))
                {
                    replayed.Add(result[^1]);
                }
            }

            _generation++;
        }

        ApplyOpportunityContrasts(result, referenceTime, engaged, replayed);
        return result;
    }

    private void CaptureCurrentChoices(
        IReadOnlyList<TrainingExample> current,
        IReadOnlySet<Guid> activeUsers,
        Dictionary<(Guid User, Guid Title), TrainingExample> observed,
        DateTime referenceTime)
    {
        foreach (var row in current.Where(row => activeUsers.Contains(row.UserId) && Valid(row, referenceTime)
            && observed[Key(row)].Label > .5))
        {
            var key = Key(row);
            if (!_entries.TryGetValue(key, out var previous) || row.Choice!.ChosenAtUtc > previous.Choice!.ChosenAtUtc)
            {
                _entries[key] = row.Copy();
            }
        }
    }

    private static TrainingExample PrepareStoredChoice(TrainingExample stored, HashSet<(Guid User, Guid Title)> engaged, DateTime referenceTime, IReadOnlySet<(Guid User, Guid Title)>? eligibleTitles, Func<Guid, Guid, bool>? canUseLibraryItem, Func<Guid, Guid, Guid, bool>? canUseCapturedItem)
    {
        // Legacy rows stay dormant, never using an accessible duplicate as proof of
        // the historical physical copy. The separate fitting view applies live scope.
        if ((canUseLibraryItem is not null || canUseCapturedItem is not null) && !HasPhysicalProvenance(stored.Choice))
        {
            return WithoutEngagedAlternatives(stored, engaged, referenceTime, null, null, null);
        }

        return WithoutEngagedAlternatives(stored, engaged, referenceTime, eligibleTitles, canUseLibraryItem, canUseCapturedItem);
    }

    /// <summary>Checks only referenced replay identities against the current catalog, keeping memory bounded by the stored comparisons.</summary>
    /// <param name="current">This run's current outcomes and choices.</param>
    /// <param name="activeUsers">Users whose current profiles were available.</param>
    /// <param name="canUseLibraryItem">The separate current physical-copy permission check.</param>
    /// <param name="canUseCapturedItem">Current typed identity validation for this referenced copy.</param>
    /// <param name="cancellationToken">Cooperative cancellation during catalog matching.</param>
    /// <returns>Eligible referenced identities; missing/removed titles never become implicit engagement.</returns>
    internal HashSet<(Guid User, Guid Title)> CaptureEligibleReplayTitles(
        IReadOnlyList<TrainingExample> current,
        IReadOnlySet<Guid> activeUsers,
        Func<Guid, Guid, bool> canUseLibraryItem,
        Func<Guid, Guid, Guid, bool> canUseCapturedItem,
        CancellationToken cancellationToken)
    {
        var needed = CaptureNeededReplayReferences(current, activeUsers);
        var eligible = new HashSet<(Guid User, Guid Title)>();
        foreach (var reference in needed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.Item is { } item && item != Guid.Empty && reference.Title != Guid.Empty
                && canUseLibraryItem(reference.User, item) && canUseCapturedItem(reference.User, item, reference.Title))
            {
                eligible.Add((reference.User, reference.Title));
            }
        }

        return eligible;
    }

    private HashSet<(Guid User, Guid Title, Guid? Item)> CaptureNeededReplayReferences(IReadOnlyList<TrainingExample> current, IReadOnlySet<Guid> activeUsers)
    {
        var needed = new HashSet<(Guid User, Guid Title, Guid? Item)>();
        lock (_gate)
        {
            foreach (var row in current.Concat(_entries.Values).Where(row => activeUsers.Contains(row.UserId) && (row.Choice is not null || row.IsContrastSample)))
            {
                needed.Add((row.UserId, row.ObservationId, row.Choice?.ChosenItemId));
                if (row.Choice?.Alternatives is { IsDefaultOrEmpty: false } alternatives)
                {
                    foreach (var alternative in alternatives.OfType<RecommendationChoiceAlternative>().Take(RecommendationChoiceEvidence.MaximumAlternatives))
                    {
                        needed.Add((row.UserId, alternative.ObservationId, alternative.LibraryItemId));
                    }
                }
            }
        }

        return needed;
    }

    private static bool HasPhysicalProvenance(RecommendationChoiceEvidence? choice)
        => IsKnownLibraryId(choice?.ChosenItemId) && choice!.Alternatives.All(alternative => IsKnownLibraryId(alternative.LibraryItemId));

    private static bool IsKnownLibraryId(Guid? itemId) => itemId.HasValue && itemId.Value != Guid.Empty;

    private static TrainingExample WithoutEngagedAlternatives(TrainingExample row, HashSet<(Guid User, Guid Title)> engaged, DateTime referenceTime, IReadOnlySet<(Guid User, Guid Title)>? eligibleTitles, Func<Guid, Guid, bool>? canUseLibraryItem, Func<Guid, Guid, Guid, bool>? canUseCapturedItem)
    {
        if (row.Choice is not { } choice || !choice.IsValid(row.ObservationId, referenceTime))
        {
            return row;
        }

        var chosenEligible = (eligibleTitles is null || eligibleTitles.Contains(Key(row)))
            && IsCapturedReferenceAllowed(row.UserId, choice.ChosenItemId, row.ObservationId, canUseLibraryItem, canUseCapturedItem);
        var alternatives = choice.Alternatives.Where(alternative => chosenEligible && !engaged.Contains((row.UserId, alternative.ObservationId))
            && (eligibleTitles is null || eligibleTitles.Contains((row.UserId, alternative.ObservationId)))
            && IsCapturedReferenceAllowed(row.UserId, alternative.LibraryItemId, alternative.ObservationId, canUseLibraryItem, canUseCapturedItem)).ToImmutableArray();
        if (alternatives.Length == choice.Alternatives.Length)
        {
            return row;
        }

        var copy = row.Copy();
        copy.Choice = alternatives.IsEmpty ? null : (choice with { Alternatives = alternatives }).Copy();
        return copy;
    }

    private static bool IsCapturedReferenceAllowed(Guid user, Guid? item, Guid observation, Func<Guid, Guid, bool>? physical, Func<Guid, Guid, Guid, bool>? identity)
    {
        if (physical is null && identity is null)
        {
            return true;
        }

        return IsKnownLibraryId(item) && (physical is null || physical(user, item!.Value))
            && (identity is null || identity(user, item!.Value, observation));
    }

    internal static bool AddChoiceView(List<TrainingExample> result, TrainingExample row, DateTime referenceTime)
    {
        // Two feature views remain one observed title, not two watches. Preserve the
        // caller's prediction and labels while sharing its original sample-weight budget.
        var current = result.Select((item, index) => (Item: item, Index: index))
            .Where(item => item.Item.IsObserved && item.Item.HasStableObservationId && Key(item.Item) == Key(row)
                && item.Item.ComputeEffectiveWeight(referenceTime) >= LearnedScoringStrategy.MinSampleWeight).ToArray();
        var budget = current.Sum(item => item.Item.SampleWeight);
        if (budget <= 0)
        {
            return false;
        }

        var scale = budget / (budget + row.SampleWeight);
        if (row.ComputeEffectiveWeight(referenceTime) * scale < LearnedScoringStrategy.MinSampleWeight
            || current.Any(item => item.Item.ComputeEffectiveWeight(referenceTime) * scale < LearnedScoringStrategy.MinSampleWeight))
        {
            // A historical view must never erase an otherwise usable current observation.
            return false;
        }

        foreach (var item in current)
        {
            var copy = item.Item.Copy();
            copy.SampleWeight *= scale;
            result[item.Index] = copy;
        }

        var replay = row.Copy();
        replay.SampleWeight *= scale;
        replay.Label = current.OrderByDescending(item => item.Item.GeneratedAtUtc).First().Item.Label;
        result.Add(replay);
        return true;
    }

    internal static void ApplyOpportunityContrasts(
        List<TrainingExample> result,
        DateTime referenceTime,
        HashSet<(Guid User, Guid Title)> engaged,
        IReadOnlyList<TrainingExample>? replayed = null)
    {
        var opportunities = result.Where(row => Valid(row, referenceTime) && !row.IsPermanentHoldout)
            .GroupBy(row => (row.UserId, row.Choice!.RecommendedAtUtc)).ToArray();
        var qualified = opportunities.SelectMany(opportunity => opportunity.SelectMany(row => row.Choice!.Alternatives)
            .Select(alternative => (opportunity.Key.UserId, opportunity.Key.RecommendedAtUtc, alternative.ObservationId))).ToHashSet();
        // Unshown frontier candidates and other users' recommendations do not establish
        // an independent rejection. Only this user's actual chronological choice can
        // authorize its bounded, explicitly unobserved relative comparisons for fitting.
        result.RemoveAll(row => row.IsContrastSample && !qualified.Contains((row.UserId, row.GeneratedAtUtc, row.ObservationId)));
        foreach (var opportunity in opportunities)
        {
            var allowed = opportunity.SelectMany(row => row.Choice!.Alternatives).Select(alternative => alternative.ObservationId).ToHashSet();
            var existing = result.Where(row => row.IsContrastSample && row.UserId == opportunity.Key.UserId
                && row.GeneratedAtUtc == opportunity.Key.RecommendedAtUtc && allowed.Contains(row.ObservationId)).ToArray();
            var added = (replayed ?? []).Where(row => row.UserId == opportunity.Key.UserId && row.Choice!.RecommendedAtUtc == opportunity.Key.RecommendedAtUtc)
                .OrderBy(row => row.ObservationId).SelectMany(row => row.Choice!.Alternatives);
            var alternatives = existing.Select(row => new RecommendationChoiceAlternative(row.ObservationId, row.Features)).Concat(added)
                .Where(alternative => !engaged.Contains((opportunity.Key.UserId, alternative.ObservationId)))
                .GroupBy(alternative => alternative.ObservationId).Select(group => group.First())
                .OrderBy(alternative => alternative.ObservationId).ToArray();
            result.RemoveAll(row => row.IsContrastSample && row.UserId == opportunity.Key.UserId
                && row.GeneratedAtUtc == opportunity.Key.RecommendedAtUtc && allowed.Contains(row.ObservationId));
            var retained = alternatives.Take(RecommendationChoiceBuilder.FittingAlternativeLimit(opportunity.Key.RecommendedAtUtc, referenceTime)).ToArray();
            foreach (var alternative in retained)
            {
                result.Add(new TrainingExample
                {
                    UserId = opportunity.Key.UserId,
                    ObservationId = alternative.ObservationId,
                    HasStableObservationId = true,
                    IsObserved = false,
                    IsContrastSample = true,
                    Label = EngineConstants.ExposureLabel,
                    SampleWeight = RecommendationChoiceBuilder.ContrastWeightBudget / retained.Length,
                    GeneratedAtUtc = opportunity.Key.RecommendedAtUtc,
                    Features = alternative.Features.Copy()
                });
            }
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

        lock (_persistenceGate)
        {
            try
            {
                TrainingExample[] rows;
                long generation;
                lock (_gate)
                {
                    rows = BalancedEntries().Select(row => row.Copy()).ToArray();
                    generation = _generation;
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
                lock (_gate)
                {
                    _savedGeneration = generation;
                }

                return true;
            }
            catch (Exception exception) when (!exception.IsFatal())
            {
                return false;
            }
        }
    }

    internal async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fileSystem is null || _path is null)
        {
            return false;
        }

        long requested;
        lock (_gate)
        {
            requested = _generation;
        }

        Task<bool> writer;
        lock (_writerGate)
        {
            writer = _pendingSave is { IsCompleted: false } active ? active : Task.Run(Save, CancellationToken.None);
            _pendingSave = writer;
        }

        try
        {
            var saved = await writer.WaitAsync(TimeSpan.FromSeconds(2), WaitTimeProvider, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                return saved && _savedGeneration >= requested;
            }
        }
        catch (TimeoutException)
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
