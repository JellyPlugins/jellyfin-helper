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

    [Theory]
    [InlineData(149, true, true, 0)]
    [InlineData(150, true, true, 0)]
    [InlineData(175, true, true, .1)]
    [InlineData(180, true, true, .12)]
    [InlineData(1000, true, true, .12)]
    [InlineData(0, false, true, .12)]
    [InlineData(1000, false, true, .12)]
    [InlineData(1000, false, false, 0)]
    [InlineData(1000, true, false, 0)]
    public void NormalNeuralMinimumPreservesTheHistoricalRampAndMissingModelException(int count, bool ownsNeural, bool available, double expected)
        => Assert.Equal(expected, OperatingMixturePolicy.NeuralMinimum(count, ownsNeural, available), 12);

    [Theory]
    [InlineData(.3, 0, .3)]
    [InlineData(.75, 0, .75)]
    [InlineData(.69, .06, .749999999999487)]
    [InlineData(.6000000000000001, .15, .75)]
    [InlineData(.45, .3, .75)]
    [InlineData(0, .3, .75)]
    public void NeuralMinimumStaysInsideTheExistingBudgetAndExactPublishedCaps(double learned, double neural, double alphaCap)
    {
        var original = new AdmissionMixture(learned, neural);
        var cap = alphaCap * .4;
        var result = OperatingMixturePolicy.WithNeuralMinimum(original, .12, alphaCap, cap);
        Assert.True(result.Alpha <= alphaCap);
        Assert.True(result.Alpha <= original.Alpha);
        Assert.InRange(result.Neural, 0, cap);
        Assert.True(result.Beta >= .12);
        Assert.InRange(1 - result.Alpha, .25, 1);
    }
}
