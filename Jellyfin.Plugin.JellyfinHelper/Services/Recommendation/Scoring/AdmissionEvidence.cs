namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Independent title support; repeated runs and unlabeled contrasts never increase these counts.</summary>
internal readonly record struct AdmissionEvidence(int CalibrationTitles, int AcceptanceTitles, double CalibrationEffectiveTitles, double AcceptanceEffectiveTitles);
