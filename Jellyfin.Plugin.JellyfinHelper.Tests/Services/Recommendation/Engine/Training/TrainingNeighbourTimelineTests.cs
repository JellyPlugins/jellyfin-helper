using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

public sealed class TrainingNeighbourTimelineTests
{
    [Fact]
    public void IndexedOverlapAndIntervals_MatchMaterializedSetsIncludingExactBoundaries()
    {
        var reference = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var target = new Dictionary<Guid, DateTime>();
        var neighbour = new Dictionary<Guid, DateTime>();
        for (var i = 0; i < 60; i++)
        {
            var id = new Guid(i + 1, 0, 0, new byte[8]);
            if (i % 4 != 0) target[id] = reference.AddMinutes((i % 11) - 5);
            if (i % 3 != 0) neighbour[id] = reference.AddMinutes((i % 13) - 6);
        }

        var timeline = new TrainingNeighbourTimeline(target, neighbour, CancellationToken.None);
        foreach (var cutoff in Enumerable.Range(-8, 18).Select(offset => reference.AddMinutes(offset)).Reverse())
        {
            var a = target.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToHashSet();
            var b = neighbour.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToHashSet();
            Assert.Equal(a.Intersect(b).Count(), timeline.CountOverlap(cutoff));
            Assert.Equal(b.Except(a).Order(), timeline.Difference(cutoff).Order());
        }
    }

    [Fact]
    public void EmptyOrIdenticalHistories_HaveNoNeighbourOnlyIntervals()
    {
        var date = DateTime.UtcNow;
        var empty = new TrainingNeighbourTimeline(new Dictionary<Guid, DateTime>(), new Dictionary<Guid, DateTime>(), CancellationToken.None);
        Assert.Equal(0, empty.RowCost);
        Assert.Equal(0, empty.CountOverlap(date));
        Assert.Empty(empty.Difference(date));
        var shared = new Dictionary<Guid, DateTime> { [Guid.NewGuid()] = date };
        var same = new TrainingNeighbourTimeline(shared, shared, CancellationToken.None);
        Assert.Equal(0, same.CountOverlap(date));
        Assert.Equal(1, same.CountOverlap(date.AddTicks(1)));
        Assert.Empty(same.Difference(date.AddTicks(1)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new TrainingNeighbourTimeline(shared, shared, cancelled.Token));
    }
}
