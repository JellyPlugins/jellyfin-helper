using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>Captures availability and conservative saved playback evidence from a single stream lookup.</summary>
internal static class PlaybackLanguageEvidence
{
    /// <summary>Captures normalized metadata without guessing which multilingual default was actually played.</summary>
    /// <param name="row">The detached watch row.</param>
    /// <param name="data">Saved Jellyfin item playback data, not a historical stream event log.</param>
    /// <param name="streams">A successfully observed stream list; null denotes lookup failure.</param>
    internal static void Capture(WatchedItemInfo row, UserItemData data, IReadOnlyList<MediaStream>? streams)
    {
        if (streams is null)
        {
            return;
        }

        var audio = streams.Where(stream => stream.Type == MediaStreamType.Audio).ToArray();
        var subtitles = streams.Where(stream => stream.Type == MediaStreamType.Subtitle).ToArray();
        row.AudioLanguages = Languages(audio);
        row.SubtitleLanguages = Languages(subtitles);
        row.HasAudioLanguageMetadata = audio.All(stream => LanguageIdentity.GetIso6391Code(stream.Language) is not null);
        row.HasSubtitleLanguageMetadata = subtitles.All(stream => LanguageIdentity.GetIso6391Code(stream.Language) is not null);
        row.PlaybackAudioLanguage = SelectedLanguage(audio, data.AudioStreamIndex);
        if (!data.AudioStreamIndex.HasValue && audio.Length > 0 && row.AudioLanguages.Count == 1
            && row.HasAudioLanguageMetadata == true)
        {
            row.PlaybackAudioLanguage = row.AudioLanguages[0];
        }

        row.PlaybackSubtitleLanguage = SelectedLanguage(subtitles, data.SubtitleStreamIndex);
        row.LanguagePlaybackDate = row.HasPlaybackActivity() && data.LastPlayedDate.HasValue
            ? DateTimeNormalization.ToUtc(data.LastPlayedDate.Value)
            : null;
    }

    /// <summary>Accumulates each captured last-play once; missing dates can be used only in the current profile.</summary>
    /// <param name="profile">The current or already cutoff-filtered historical profile.</param>
    /// <param name="row">Captured item metadata and saved selection evidence.</param>
    internal static void Accumulate(UserWatchProfile profile, WatchedItemInfo row)
    {
        Add(profile.LanguageProfile, row.PlaybackAudioLanguage, row.AudioLanguages, row.HasAudioLanguageMetadata);
        Add(profile.SubtitleLanguageProfile, row.PlaybackSubtitleLanguage, row.SubtitleLanguages, row.HasSubtitleLanguageMetadata);
    }

    private static string[] Languages(IEnumerable<MediaStream> streams)
        => streams.Select(stream => WatchHistoryService.NormalizeLanguage(stream.Language))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    private static string? SelectedLanguage(IEnumerable<MediaStream> streams, int? index)
        => index is int selectedIndex && selectedIndex >= 0 ? LanguageIdentity.GetIso6391Code(streams.FirstOrDefault(stream => stream.Index == selectedIndex)?.Language) : null;

    private static void Add(Dictionary<string, LanguageProfileEntry> profile, string? language, IReadOnlyList<string>? available, bool? metadataComplete)
    {
        if (language is null || available is null || !available.Contains(language, StringComparer.OrdinalIgnoreCase)
            || (available.Count == 1 && metadataComplete != true))
        {
            return;
        }

        if (!profile.TryGetValue(language, out var entry))
        {
            entry = new LanguageProfileEntry();
            profile[language] = entry;
        }

        if (available.Count > 1)
        {
            entry.ChosenCount++;
        }
        else
        {
            entry.ForcedCount++;
        }
    }
}
