using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Forward provenance outside the v6 vector; absent legacy provenance is never inferred.</summary>
internal static class FeatureDerivationContract
{
    /// <summary>Unchanged legacy coordinates may use a legacy predictor; changed or unsupported derivations may not.</summary>
    /// <param name="inputVersion">The original candidate/input marker.</param>
    /// <param name="modelVersion">The model's captured fitting/publication marker.</param>
    /// <returns>Whether both describe the same supported input contract.</returns>
    internal static bool IsCompatible(int inputVersion, int modelVersion)
        => inputVersion == modelVersion && inputVersion >= 0 && inputVersion <= CandidateFeatures.CurrentFeatureDerivationVersion;

    /// <summary>Selects a natural current cohort when present, otherwise retaining unmarked legacy evidence.</summary>
    /// <param name="fitting">The unchanged permanent fitting partition.</param>
    /// <param name="holdout">The unchanged permanent holdout partition.</param>
    /// <returns>The current derivation when supplied, otherwise zero for legacy/unknown inputs.</returns>
    internal static int SelectTrainingVersion(IReadOnlyList<TrainingExample> fitting, IReadOnlyList<TrainingExample> holdout)
        => fitting.Concat(holdout).Select(row => row.Features?.FeatureDerivationVersion ?? 0)
            .Where(version => version >= 0 && version <= CandidateFeatures.CurrentFeatureDerivationVersion).DefaultIfEmpty(0).Max();

    /// <summary>Selects observed inputs without relabeling them; incompatible nested comparisons are withheld.</summary>
    /// <param name="examples">Original observations.</param>
    /// <param name="version">The selected input derivation.</param>
    /// <returns>Original rows, or a choice-free copy when nested comparisons have incompatible provenance.</returns>
    internal static TrainingExample[] SelectCohort(IEnumerable<TrainingExample> examples, int version)
        => examples.Where(row => row.Features is not null && row.Features.FeatureDerivationVersion == version)
            .Select(row => WithoutIncompatibleChoice(row, version)).ToArray();

    private static TrainingExample WithoutIncompatibleChoice(TrainingExample row, int version)
    {
        if (row.Choice is null || row.Choice.HasCompatibleDerivation(version))
        {
            return row;
        }

        var copy = row.Copy();
        copy.Choice = null;
        return copy;
    }

    /// <summary>Prevents mixed or unsupported feature derivations from claiming one admission contract.</summary>
    /// <param name="examples">The original calibration/acceptance observations.</param>
    /// <param name="version">Their uniform derivation, or zero when no evidence exists.</param>
    /// <returns>True for one supported cohort; zero remains explicitly legacy/unknown.</returns>
    internal static bool TryResolveUniformVersion(IEnumerable<TrainingExample> examples, out int version)
    {
        version = 0;
        var found = false;
        foreach (var row in examples)
        {
            var features = row.Features;
            if (features is null)
            {
                return false;
            }

            var next = features.FeatureDerivationVersion;
            if (next < 0 || next > CandidateFeatures.CurrentFeatureDerivationVersion)
            {
                return false;
            }

            if ((found && version != next) || (row.Choice is not null && !row.Choice.HasCompatibleDerivation(next)))
            {
                return false;
            }

            version = next;
            found = true;
        }

        return true;
    }

    /// <summary>Describes compatibility without dropping or reweighting an older operating prior.</summary>
    /// <param name="version">The captured model/cohort derivation.</param>
    /// <returns>An explicit current, legacy/unknown or different-derivation status.</returns>
    internal static string Status(int version)
        => version switch
        {
            CandidateFeatures.CurrentFeatureDerivationVersion => "Current",
            0 => "LegacyOrUnknownPrior",
            _ => "DifferentDerivationPrior"
        };

    /// <summary>Calibration-only retention cannot upgrade the provenance of unchanged older parameters.</summary>
    /// <param name="cohortVersion">The fresh input cohort.</param>
    /// <param name="retainedParameters">Whether provisional serving retained the incumbent parameters.</param>
    /// <param name="priorVersion">The incumbent's captured provenance.</param>
    /// <returns>The prior provenance on retention, otherwise the fresh publication's cohort.</returns>
    internal static int PublicationVersion(int cohortVersion, bool retainedParameters, int priorVersion)
        => retainedParameters ? priorVersion : cohortVersion;
}
