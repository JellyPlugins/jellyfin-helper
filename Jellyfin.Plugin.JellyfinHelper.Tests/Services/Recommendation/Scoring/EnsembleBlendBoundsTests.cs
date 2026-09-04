using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="EnsembleBlendBounds"/>: the null-configuration fallback to ensemble defaults, the
///     pass-through of configured values, and record value-equality.
/// </summary>
public sealed class EnsembleBlendBoundsTests
{
    [Fact]
    public void FromConfiguration_NullConfig_UsesEnsembleDefaults()
    {
        var bounds = EnsembleBlendBounds.FromConfiguration(null);

        Assert.Equal(EnsembleScoringStrategy.DefaultAlphaMin, bounds.AlphaMin);
        Assert.Equal(EnsembleScoringStrategy.DefaultAlphaMax, bounds.AlphaMax);
        Assert.Equal(EnsembleScoringStrategy.DefaultGenrePenaltyFloor, bounds.GenrePenaltyFloor);
    }

    [Fact]
    public void FromConfiguration_WithConfig_UsesConfiguredValues()
    {
        // Values inside the [0,1] range the configuration setters clamp to, and ordered so the alpha-range
        // normalization leaves them unchanged.
        var config = new PluginConfiguration
        {
            EnsembleAlphaMin = 0.2,
            EnsembleAlphaMax = 0.8,
            EnsembleGenrePenaltyFloor = 0.25
        };

        var bounds = EnsembleBlendBounds.FromConfiguration(config);

        Assert.Equal(config.EnsembleAlphaMin, bounds.AlphaMin);
        Assert.Equal(config.EnsembleAlphaMax, bounds.AlphaMax);
        Assert.Equal(config.EnsembleGenrePenaltyFloor, bounds.GenrePenaltyFloor);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new EnsembleBlendBounds(0.3, 0.75, 0.1);
        var b = new EnsembleBlendBounds(0.3, 0.75, 0.1);
        var different = new EnsembleBlendBounds(0.3, 0.75, 0.2);

        Assert.Equal(a, b);
        Assert.NotEqual(a, different);
    }
}
