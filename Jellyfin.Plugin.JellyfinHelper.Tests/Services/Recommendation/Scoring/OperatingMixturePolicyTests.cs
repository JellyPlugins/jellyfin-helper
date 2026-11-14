using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Release 3.1.0.1 maturity/ramp reference and explicit safe cold-start extension.</summary>
public sealed class OperatingMixturePolicyTests
{
    [Theory]
    [InlineData(0, .3341361810095596)]
    [InlineData(50, .525)]
    [InlineData(1000, .75)]
    public void HistoricalSigmoidBoundsAndMidpointArePreserved(int count, double expected)
        => Assert.Equal(expected, OperatingMixturePolicy.Maturity(count, 50, .3, .75), 12);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(149, 0)]
    [InlineData(150, 0)]
    [InlineData(175, .1)]
    [InlineData(250, .4)]
    [InlineData(1000, .4)]
    public void GlobalNeuralUsesTheHistoricalActivationAndRamp(int count, double expected)
        => Assert.Equal(expected, OperatingMixturePolicy.NeuralTarget(count, ownsNeural: true, available: true), 12);

    [Theory]
    [InlineData(0, .12)]
    [InlineData(6, .12)]
    [InlineData(149, .12)]
    [InlineData(150, .12)]
    [InlineData(175, .19)]
    [InlineData(250, .4)]
    public void PersonalNeuralHasABoundedColdWarmPriorThenItsOwnHistoricalRamp(int count, double expected)
        => Assert.Equal(expected, OperatingMixturePolicy.NeuralTarget(count, ownsNeural: false, available: true), 12);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrUntrainedNeuralNeverGetsForcedIntoTheMix(bool global)
        => Assert.Equal(0, OperatingMixturePolicy.NeuralTarget(1000, global, available: false));

    [Fact]
    public void CalibrationDampingAndGradualCorrectionRespectTheHistoricalProtection()
    {
        var measured = new EnsembleAdmissionEvaluator.ServingCalibration(20, .1, .45, .4);
        var target = OperatingMixturePolicy.Target(.75, .3, .4, measured);
        Assert.Equal(.525, target.Alpha, 12);
        Assert.Equal(.2, target.Beta, 12);
        var adjusted = OperatingMixturePolicy.Approach(new AdmissionMixture(.3, 0), target);
        Assert.InRange(adjusted.Alpha, .3, .525);
        Assert.InRange(adjusted.Neural, 0, .3);
        Assert.InRange(1 - adjusted.Alpha, .25, 1);
    }
}
