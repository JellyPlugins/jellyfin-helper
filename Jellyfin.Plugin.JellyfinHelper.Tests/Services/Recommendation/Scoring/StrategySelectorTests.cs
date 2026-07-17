using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for the internal <see cref="StrategySelector" /> cohort router. The class ties three
///     concerns together — exploration-gate activation, deterministic user-hash bucketing, and the
///     mapping from bucket-integer to cohort name / alpha-offset. Each of these has hard invariants
///     that break user experience if regressed:
///     <list type="bullet">
///         <item>a user must land in the same cohort across restarts (stable Guid hash);</item>
///         <item>exploration must stay OFF until the ensemble has enough training data;</item>
///         <item>the alpha-offset and cohort name must agree on the bucket (no drift between them).</item>
///     </list>
/// </summary>
public class StrategySelectorTests
{
    [Fact]
    public void Constructor_NullEnsemble_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StrategySelector(null!));
    }

    [Fact]
    public void GetAlphaOffset_ExplorationInactive_AllUsersControl()
    {
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(0.0, selector.GetAlphaOffset(Guid.NewGuid()));
        }
    }

    [Fact]
    public void GetCohortName_ExplorationInactive_AllUsersControl()
    {
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal("control", selector.GetCohortName(Guid.NewGuid()));
        }
    }

    [Fact]
    public void GetAlphaOffset_SameUser_SameOffsetAcrossCalls()
    {
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);
        var userId = Guid.NewGuid();

        var first = selector.GetAlphaOffset(userId);
        var second = selector.GetAlphaOffset(userId);
        var third = selector.GetAlphaOffset(userId);

        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void GetCohortName_SameUser_SameCohortAcrossCalls()
    {
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);
        var userId = Guid.NewGuid();

        Assert.Equal(selector.GetCohortName(userId), selector.GetCohortName(userId));
    }

    [Fact]
    public void CohortAndOffset_DoNotDriftForSameUser()
    {
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);

        for (var i = 0; i < 30; i++)
        {
            var id = Guid.NewGuid();
            var cohort = selector.GetCohortName(id);
            var offset = selector.GetAlphaOffset(id);

            if (cohort == "control")
            {
                Assert.Equal(0.0, offset);
            }
            else if (cohort == "explore-high")
            {
                Assert.Equal(StrategySelector.ExploreHighOffset, offset);
            }
            else if (cohort == "explore-low")
            {
                Assert.Equal(StrategySelector.ExploreLowOffset, offset);
            }
            else
            {
                Assert.Fail($"Unknown cohort name '{cohort}'.");
            }
        }
    }

    [Fact]
    public void GetAlphaOffset_KnownGuids_ProduceStableBuckets()
    {
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);

        var id1 = new Guid("00000000-0000-0000-0000-000000000001");
        var id2 = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(selector.GetAlphaOffset(id1), selector.GetAlphaOffset(id1));
        Assert.Equal(selector.GetCohortName(id1), selector.GetCohortName(id1));
        Assert.Equal(selector.GetAlphaOffset(id2), selector.GetAlphaOffset(id2));
        Assert.Equal(selector.GetCohortName(id2), selector.GetCohortName(id2));
    }

    [Fact]
    public void ExploreOffsets_HaveExpectedSigns()
    {
        Assert.True(StrategySelector.ExploreHighOffset > 0);
        Assert.True(StrategySelector.ExploreLowOffset < 0);
        Assert.Equal(StrategySelector.ExploreHighOffset, -StrategySelector.ExploreLowOffset);
    }

    [Fact]
    public void ExplorationGates_HaveSensibleValues()
    {
        Assert.True(StrategySelector.MinExamplesForExploration > 0);
        Assert.True(StrategySelector.MinMetricsHistoryForExploration > 0);
        Assert.True(StrategySelector.MinExamplesForExploration >= 30);
    }

    [Fact]
    public void EmptyGuid_HandledDeterministically()
    {
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);

        var offset1 = selector.GetAlphaOffset(Guid.Empty);
        var offset2 = selector.GetAlphaOffset(Guid.Empty);
        var cohort1 = selector.GetCohortName(Guid.Empty);
        var cohort2 = selector.GetCohortName(Guid.Empty);

        Assert.Equal(offset1, offset2);
        Assert.Equal(cohort1, cohort2);
    }

    [Fact]
    public void AllZeroGuid_BucketsToControl_WhenExplorationInactive()
    {
        // Guid.Empty bytes are all 0 → XOR-fold produces bucket 0.
        // But exploration inactive → offset must still be 0 and cohort "control".
        var ensemble = new EnsembleScoringStrategy();
        var selector = new StrategySelector(ensemble);

        Assert.Equal(0.0, selector.GetAlphaOffset(Guid.Empty));
        Assert.Equal("control", selector.GetCohortName(Guid.Empty));
    }
}