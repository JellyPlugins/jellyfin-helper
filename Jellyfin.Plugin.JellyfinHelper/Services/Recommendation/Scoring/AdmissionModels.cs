namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Frozen prediction sources. The lifecycle must not pass live, concurrently trained strategies.</summary>
internal sealed record AdmissionModels(IScoringStrategy Heuristic, IScoringStrategy Learned, IScoringStrategy? Neural = null, IScoringStrategy? Incumbent = null);
