namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Assessment only; rejecting it leaves the incumbent selection and its files untouched.</summary>
internal sealed record AdmissionDecision(bool Accepted, string Reason, AdmissionMixture Candidate, AdmissionEvidence Evidence, AdmissionUtility Utility)
{
    /// <summary>Creates a non-admission without inventing zero loss or positive quality.</summary>
    /// <param name="reason">The evidence limitation.</param>
    /// <param name="calibrationTitles">Available unique calibration titles.</param>
    /// <param name="acceptanceTitles">Available unique acceptance titles.</param>
    /// <returns>A diagnostic unknown result.</returns>
    internal static AdmissionDecision Unknown(string reason, int calibrationTitles, int acceptanceTitles)
        => new(false, reason, default, new AdmissionEvidence(calibrationTitles, acceptanceTitles, 0, 0), new AdmissionUtility(null, null, null, null, null));
}
