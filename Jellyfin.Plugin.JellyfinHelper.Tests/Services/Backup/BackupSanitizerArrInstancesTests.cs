using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Tests for <see cref="BackupSanitizer" /> targeting the Arr-instance path
///     (Radarr/Sonarr): the null-list guard reached before validation, and the
///     null-entry drop, count cap, and field truncation applied to real instances.
/// </summary>
public class BackupSanitizerArrInstancesTests
{
    [Fact]
    public void Sanitize_NullRadarrInstances_ReturnsWithoutThrowing()
    {
        // A deserialized backup can carry an explicit JSON null for the array; the init
        // accessor + System.Text.Json bypass the =[] default. Sanitize runs BEFORE
        // validation, so it must tolerate the null rather than NRE.
        var data = new BackupData { RadarrInstances = null! };

        BackupSanitizer.Sanitize(data);

        Assert.Null(data.RadarrInstances);
    }

    [Fact]
    public void Sanitize_NullSonarrInstances_ReturnsWithoutThrowing()
    {
        // Same guard, reached through the second call site so both feeds are covered.
        var data = new BackupData { SonarrInstances = null! };

        BackupSanitizer.Sanitize(data);

        Assert.Null(data.SonarrInstances);
    }

    [Fact]
    public void Sanitize_ArrInstances_NullEntriesDroppedThenCappedAndTruncated()
    {
        // A leading null must be removed before the count cap so it cannot consume a real
        // instance's slot, and surviving instances' oversized fields must be truncated.
        var data = new BackupData();
        data.RadarrInstances.Add(null!);
        for (var i = 0; i < BackupValidator.MaxArrInstances + 2; i++)
        {
            data.RadarrInstances.Add(new BackupArrInstance
            {
                Name = new string('n', BackupValidator.MaxInstanceNameLength + 10),
                Url = "http://" + new string('u', BackupValidator.MaxUrlLength),
                ApiKey = new string('k', BackupValidator.MaxApiKeyLength + 10)
            });
        }

        BackupSanitizer.Sanitize(data);

        Assert.DoesNotContain(null, data.RadarrInstances);
        Assert.Equal(BackupValidator.MaxArrInstances, data.RadarrInstances.Count);
        Assert.All(data.RadarrInstances, instance =>
        {
            Assert.Equal(BackupValidator.MaxInstanceNameLength, instance.Name.Length);
            Assert.Equal(BackupValidator.MaxUrlLength, instance.Url.Length);
            Assert.Equal(BackupValidator.MaxApiKeyLength, instance.ApiKey.Length);
        });
    }
}
