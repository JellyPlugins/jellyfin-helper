using System;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>One detached optimizer checkpoint, paired atomically with its model's weights and input coordinates.</summary>
internal sealed class NeuralTrainingState
{
    public int Version { get; set; } = 1;

    public int Timestep { get; set; }

    public double[][] FirstMoments { get; set; } = [];

    public double[][] SecondMoments { get; set; } = [];

    public double OutputFirstMoment { get; set; }

    public double OutputSecondMoment { get; set; }

    public double[] InputWeightLimits { get; set; } = [];

    public double[] InputBiasLimits { get; set; } = [];

    internal NeuralTrainingState Copy() => new()
    {
        Version = Version,
        Timestep = Timestep,
        FirstMoments = FirstMoments.Select(values => (double[])values.Clone()).ToArray(),
        SecondMoments = SecondMoments.Select(values => (double[])values.Clone()).ToArray(),
        OutputFirstMoment = OutputFirstMoment,
        OutputSecondMoment = OutputSecondMoment,
        InputWeightLimits = (double[])InputWeightLimits.Clone(),
        InputBiasLimits = (double[])InputBiasLimits.Clone()
    };

    internal bool Valid(int inputSize)
    {
        int[] lengths =
        [
            NeuralScoringStrategy.Hidden1Size * inputSize,
            NeuralScoringStrategy.Hidden1Size * NeuralScoringStrategy.Hidden2Size,
            NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden3Size,
            NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden4Size,
            NeuralScoringStrategy.Hidden4Size, NeuralScoringStrategy.Hidden1Size,
            NeuralScoringStrategy.Hidden2Size, NeuralScoringStrategy.Hidden3Size, NeuralScoringStrategy.Hidden4Size
        ];
        return Version == 1 && Timestep >= 0 && FirstMoments is { Length: 9 } && SecondMoments is { Length: 9 }
            && lengths.Select((length, index) => FirstMoments[index] is { } first && SecondMoments[index] is { } second
                && first.Length == length && second.Length == length && first.All(double.IsFinite)
                && second.All(value => double.IsFinite(value) && value >= 0)).All(valid => valid)
            && double.IsFinite(OutputFirstMoment) && double.IsFinite(OutputSecondMoment) && OutputSecondMoment >= 0
            && InputWeightLimits is { } && InputWeightLimits.Length == inputSize && InputWeightLimits.All(ValidLimit)
            && InputBiasLimits is { Length: NeuralScoringStrategy.Hidden1Size } && InputBiasLimits.All(ValidLimit)
            && (Timestep > 0 || (OutputFirstMoment == 0 && OutputSecondMoment == 0
                && FirstMoments.Concat(SecondMoments).All(values => values.All(value => value == 0))));
    }

    private static bool ValidLimit(double value) => double.IsFinite(value) && value > 0;
}
