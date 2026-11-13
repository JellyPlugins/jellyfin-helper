namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Coherent predictions captured once before calibration or repeated statistical resampling.</summary>
internal readonly record struct AdmissionScores(double Heuristic, double Learned, double Neural, double Incumbent);
