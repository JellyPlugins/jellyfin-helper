using System.Xml.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Configuration;

/// <summary>
///     Tests for XML serialization/deserialization of PluginConfiguration,
///     specifically verifying that multiple Arr instances persist correctly.
/// </summary>
public class PluginConfigurationSerializationTests
{
    private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

    /// <summary>
    ///     Serializes the configuration to XML and deserializes it back,
    ///     verifying round-trip fidelity.
    /// </summary>
    private static PluginConfiguration RoundTrip(PluginConfiguration config)
    {
        using var writer = new StringWriter();
        Serializer.Serialize(writer, config);
        var xml = writer.ToString();

        using var reader = new StringReader(xml);
        return (PluginConfiguration)Serializer.Deserialize(reader)!;
    }

    [Fact]
    public void XmlRoundTrip_SingleRadarrInstance_Preserved()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr",
            Url = "http://localhost:7878",
            ApiKey = "key1"
        });

        var restored = RoundTrip(config);

        Assert.Single(restored.RadarrInstances);
        Assert.Equal("Radarr", restored.RadarrInstances[0].Name);
        Assert.Equal("http://localhost:7878", restored.RadarrInstances[0].Url);
        Assert.Equal("key1", restored.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public void XmlRoundTrip_MultipleRadarrInstances_AllPreserved()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr HD",
            Url = "http://localhost:7878",
            ApiKey = "key1"
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr 4K",
            Url = "http://localhost:7879",
            ApiKey = "key2"
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr Anime",
            Url = "http://localhost:7880",
            ApiKey = "key3"
        });

        var restored = RoundTrip(config);

        Assert.Equal(3, restored.RadarrInstances.Count);

        Assert.Equal("Radarr HD", restored.RadarrInstances[0].Name);
        Assert.Equal("http://localhost:7878", restored.RadarrInstances[0].Url);
        Assert.Equal("key1", restored.RadarrInstances[0].ApiKey);

        Assert.Equal("Radarr 4K", restored.RadarrInstances[1].Name);
        Assert.Equal("http://localhost:7879", restored.RadarrInstances[1].Url);
        Assert.Equal("key2", restored.RadarrInstances[1].ApiKey);

        Assert.Equal("Radarr Anime", restored.RadarrInstances[2].Name);
        Assert.Equal("http://localhost:7880", restored.RadarrInstances[2].Url);
        Assert.Equal("key3", restored.RadarrInstances[2].ApiKey);
    }

    [Fact]
    public void XmlRoundTrip_MultipleSonarrInstances_AllPreserved()
    {
        var config = new PluginConfiguration();
        config.SonarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Sonarr HD",
            Url = "http://localhost:8989",
            ApiKey = "skey1"
        });
        config.SonarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Sonarr Anime",
            Url = "http://localhost:8990",
            ApiKey = "skey2"
        });

        var restored = RoundTrip(config);

        Assert.Equal(2, restored.SonarrInstances.Count);

        Assert.Equal("Sonarr HD", restored.SonarrInstances[0].Name);
        Assert.Equal("http://localhost:8989", restored.SonarrInstances[0].Url);
        Assert.Equal("skey1", restored.SonarrInstances[0].ApiKey);

        Assert.Equal("Sonarr Anime", restored.SonarrInstances[1].Name);
        Assert.Equal("http://localhost:8990", restored.SonarrInstances[1].Url);
        Assert.Equal("skey2", restored.SonarrInstances[1].ApiKey);
    }

    [Fact]
    public void XmlRoundTrip_MixedRadarrAndSonarr_AllPreserved()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr",
            Url = "http://radarr:7878",
            ApiKey = "rkey"
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr 4K",
            Url = "http://radarr4k:7878",
            ApiKey = "rkey4k"
        });
        config.SonarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Sonarr",
            Url = "http://sonarr:8989",
            ApiKey = "skey"
        });

        var restored = RoundTrip(config);

        Assert.Equal(2, restored.RadarrInstances.Count);
        Assert.Single(restored.SonarrInstances);

        Assert.Equal("Radarr", restored.RadarrInstances[0].Name);
        Assert.Equal("Radarr 4K", restored.RadarrInstances[1].Name);
        Assert.Equal("Sonarr", restored.SonarrInstances[0].Name);
    }

    [Fact]
    public void XmlRoundTrip_EmptyInstances_PreservedAsEmpty()
    {
        var config = new PluginConfiguration();

        var restored = RoundTrip(config);

        Assert.Empty(restored.RadarrInstances);
        Assert.Empty(restored.SonarrInstances);
    }

    [Fact]
    public void XmlRoundTrip_OtherSettingsPreservedWithInstances()
    {
        var config = new PluginConfiguration
        {
            ExcludedLibraries = "Music",
            OrphanMinAgeDays = 7,
            TrickplayTaskMode = TaskMode.Activate,
            Language = "de",
            UseTrash = true,
            TrashFolderPath = "/tmp/trash",
            TrashRetentionDays = 14
        };
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr",
            Url = "http://localhost:7878",
            ApiKey = "testkey"
        });

        var restored = RoundTrip(config);

        Assert.Equal("Music", restored.ExcludedLibraries);
        Assert.Equal(7, restored.OrphanMinAgeDays);
        Assert.Equal(TaskMode.Activate, restored.TrickplayTaskMode);
        Assert.Equal("de", restored.Language);
        Assert.True(restored.UseTrash);
        Assert.Equal("/tmp/trash", restored.TrashFolderPath);
        Assert.Equal(14, restored.TrashRetentionDays);
        Assert.Single(restored.RadarrInstances);
        Assert.Equal("Radarr", restored.RadarrInstances[0].Name);
    }

    [Fact]
    public void XmlRoundTrip_InstancesNotDuplicated_AfterMultipleRoundTrips()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr",
            Url = "http://localhost:7878",
            ApiKey = "key1"
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr 4K",
            Url = "http://localhost:7879",
            ApiKey = "key2"
        });

        // Round-trip multiple times to ensure no duplication
        var restored1 = RoundTrip(config);
        var restored2 = RoundTrip(restored1);
        var restored3 = RoundTrip(restored2);

        Assert.Equal(2, restored3.RadarrInstances.Count);
        Assert.Equal("Radarr", restored3.RadarrInstances[0].Name);
        Assert.Equal("Radarr 4K", restored3.RadarrInstances[1].Name);
    }

    // The tests above cover Arr instances plus a mixed subset of settings. What they miss is "did every single property survive a round-trip at its default?" and "does a fully non-default configuration round-trip without dropping / renaming any property?".

    [Fact]
    public void XmlRoundTrip_DefaultConfiguration_AllPropertiesUnchanged()
    {
        var original = new PluginConfiguration();
        var restored = RoundTrip(original);

        // String defaults
        Assert.Equal(string.Empty, restored.ExcludedLibraries);
        Assert.Equal(string.Empty, restored.SeerrUrl);
        Assert.Equal(string.Empty, restored.SeerrApiKey);
        Assert.Equal(".jellyfin-trash", restored.TrashFolderPath);
        Assert.Equal("en", restored.Language);
        Assert.Equal("INFO", restored.PluginLogLevel);

        // Numeric defaults
        Assert.Equal(0, restored.OrphanMinAgeDays);
        Assert.Equal(365, restored.SeerrCleanupAgeDays);
        Assert.Equal(0, restored.ConfigVersion);
        Assert.Equal(30, restored.TrashRetentionDays);
        Assert.Equal(20, restored.MaxRecommendationsPerUser);
        Assert.Equal(0.3, restored.EnsembleAlphaMin, 5);
        Assert.Equal(0.75, restored.EnsembleAlphaMax, 5);
        Assert.Equal(0.10, restored.EnsembleGenrePenaltyFloor, 5);
        Assert.Equal(0L, restored.TotalBytesFreed);
        Assert.Equal(0, restored.TotalItemsDeleted);
        // DateTime default: DateTime.MinValue means "no cleanup has ever run".
        // Persisted so restart-after-first-run does not reset the metric to "unknown".
        Assert.Equal(DateTime.MinValue, restored.LastCleanupTimestamp);

        // Boolean defaults
        Assert.False(restored.DiscoveryUserAccessEnabled);
        Assert.False(restored.UseTrash);
        Assert.False(restored.SyncRecommendationsToPlaylist);

        // TaskMode defaults
        Assert.Equal(TaskMode.DryRun, restored.TrickplayTaskMode);
        Assert.Equal(TaskMode.DryRun, restored.EmptyMediaFolderTaskMode);
        Assert.Equal(TaskMode.DryRun, restored.OrphanedSubtitleTaskMode);
        Assert.Equal(TaskMode.DryRun, restored.LinkRepairTaskMode);
        Assert.Equal(TaskMode.Deactivate, restored.SeerrCleanupTaskMode);
        Assert.Equal(TaskMode.DryRun, restored.RecommendationsTaskMode);

        // Collections default to empty
        Assert.Empty(restored.RadarrInstances);
        Assert.Empty(restored.SonarrInstances);
    }

    [Fact]
    public void XmlRoundTrip_AllPropertiesNonDefault_AllValuesPreserved()
    {
        // Every property set to a value distinct from its default. If any property is
        // silently dropped or misspelled during (de)serialization, one of the asserts below
        // will fire.
        var original = new PluginConfiguration
        {
            ExcludedLibraries = "Music,Home Videos",
            OrphanMinAgeDays = 42,
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Activate,
            OrphanedSubtitleTaskMode = TaskMode.Activate,
            LinkRepairTaskMode = TaskMode.Activate,
            SeerrCleanupTaskMode = TaskMode.Activate,
            SeerrCleanupAgeDays = 90,
            SeerrUrl = "http://seerr.local:5055",
            SeerrApiKey = "seerr-key",
            DiscoveryUserAccessEnabled = true,
            ConfigVersion = 3,
            UseTrash = true,
            TrashFolderPath = "/mnt/trash",
            TrashRetentionDays = 14,
            Language = "de",
            RecommendationsTaskMode = TaskMode.Activate,
            MaxRecommendationsPerUser = 50,
            SyncRecommendationsToPlaylist = true,
            EnsembleAlphaMin = 0.4,
            EnsembleAlphaMax = 0.6,
            EnsembleGenrePenaltyFloor = 0.2,
            PluginLogLevel = "DEBUG",
            TotalBytesFreed = 123_456_789L,
            TotalItemsDeleted = 42,
            // Any DateTime distinct from DateTime.MinValue proves the property was written. Kind=Utc + subseconds=0 avoids XML round-trip surprises (fractional seconds and local/UTC conversion are both silently lossy in some serializer configurations).
            LastCleanupTimestamp = new DateTime(2026, 7, 9, 10, 17, 12, DateTimeKind.Utc)
        };
        original.RadarrInstances.Add(new ArrInstanceConfig { Name = "R", Url = "http://r", ApiKey = "rk" });
        original.SonarrInstances.Add(new ArrInstanceConfig { Name = "S", Url = "http://s", ApiKey = "sk" });

        var restored = RoundTrip(original);

        Assert.Equal("Music,Home Videos", restored.ExcludedLibraries);
        Assert.Equal(42, restored.OrphanMinAgeDays);
        Assert.Equal(TaskMode.Activate, restored.TrickplayTaskMode);
        Assert.Equal(TaskMode.Activate, restored.EmptyMediaFolderTaskMode);
        Assert.Equal(TaskMode.Activate, restored.OrphanedSubtitleTaskMode);
        Assert.Equal(TaskMode.Activate, restored.LinkRepairTaskMode);
        Assert.Equal(TaskMode.Activate, restored.SeerrCleanupTaskMode);
        Assert.Equal(90, restored.SeerrCleanupAgeDays);
        Assert.Equal("http://seerr.local:5055", restored.SeerrUrl);
        Assert.Equal("seerr-key", restored.SeerrApiKey);
        Assert.True(restored.DiscoveryUserAccessEnabled);
        Assert.Equal(3, restored.ConfigVersion);
        Assert.True(restored.UseTrash);
        Assert.Equal("/mnt/trash", restored.TrashFolderPath);
        Assert.Equal(14, restored.TrashRetentionDays);
        Assert.Equal("de", restored.Language);
        Assert.Equal(TaskMode.Activate, restored.RecommendationsTaskMode);
        Assert.Equal(50, restored.MaxRecommendationsPerUser);
        Assert.True(restored.SyncRecommendationsToPlaylist);
        // Use precision-based comparison for doubles - this is xUnit's idiomatic style
        // and stays robust against any future serializer-format tweaks.
        Assert.Equal(0.4, restored.EnsembleAlphaMin, 5);
        Assert.Equal(0.6, restored.EnsembleAlphaMax, 5);
        Assert.Equal(0.2, restored.EnsembleGenrePenaltyFloor, 5);
        Assert.Equal("DEBUG", restored.PluginLogLevel);
        Assert.Equal(123_456_789L, restored.TotalBytesFreed);
        Assert.Equal(42, restored.TotalItemsDeleted);
        Assert.Equal(original.LastCleanupTimestamp, restored.LastCleanupTimestamp);
        Assert.Single(restored.RadarrInstances);
        Assert.Single(restored.SonarrInstances);
    }

    [Fact]
    public void XmlRoundTrip_OutOfRangeNumericProperties_AreClampedBySetter()
    {
        // Values assigned via property are clamped BEFORE serialization, so the XML never contains out-of-range values.
        var original = new PluginConfiguration
        {
            OrphanMinAgeDays = 10_000,          // clamped to 3650
            MaxRecommendationsPerUser = 999,    // clamped to 100
            EnsembleAlphaMin = -0.5,            // clamped to 0.0
            EnsembleAlphaMax = 2.0,             // clamped to 1.0
            EnsembleGenrePenaltyFloor = 5.0     // clamped to 1.0
        };

        var restored = RoundTrip(original);

        Assert.Equal(3650, restored.OrphanMinAgeDays);
        Assert.Equal(100, restored.MaxRecommendationsPerUser);
        Assert.Equal(0.0, restored.EnsembleAlphaMin, 5);
        Assert.Equal(1.0, restored.EnsembleAlphaMax, 5);
        Assert.Equal(1.0, restored.EnsembleGenrePenaltyFloor, 5);
    }

    [Fact]
    public void Deserialize_HandEditedXmlWithOutOfRangeValues_ClampsOnLoad()
    {
        // Simulates the real-world case: an admin hand-edits the plugin config XML file and puts values outside the valid ranges.
        const string xml = @"<?xml version=""1.0"" encoding=""utf-16""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <OrphanMinAgeDays>99999</OrphanMinAgeDays>
  <MaxRecommendationsPerUser>500</MaxRecommendationsPerUser>
  <EnsembleAlphaMin>-1.5</EnsembleAlphaMin>
  <EnsembleAlphaMax>2.5</EnsembleAlphaMax>
  <EnsembleGenrePenaltyFloor>10.0</EnsembleGenrePenaltyFloor>
</PluginConfiguration>";

        using var reader = new StringReader(xml);
        var restored = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.Equal(3650, restored.OrphanMinAgeDays);
        Assert.Equal(100, restored.MaxRecommendationsPerUser);
        Assert.Equal(0.0, restored.EnsembleAlphaMin, 5);
        Assert.Equal(1.0, restored.EnsembleAlphaMax, 5);
        Assert.Equal(1.0, restored.EnsembleGenrePenaltyFloor, 5);
    }

    [Fact]
    public void Deserialize_HandEditedXmlWithNaNDoubles_CoercedToFiniteValueAndReported()
    {
        // Math.Clamp passes NaN through unchanged. Without an explicit NaN guard the ensemble blend would then multiply by NaN and poison every recommendation score.
        const string xml = @"<?xml version=""1.0"" encoding=""utf-16""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <EnsembleAlphaMin>NaN</EnsembleAlphaMin>
  <EnsembleAlphaMax>NaN</EnsembleAlphaMax>
  <EnsembleGenrePenaltyFloor>NaN</EnsembleGenrePenaltyFloor>
</PluginConfiguration>";

        using var reader = new StringReader(xml);
        var restored = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.False(double.IsNaN(restored.EnsembleAlphaMin));
        Assert.False(double.IsNaN(restored.EnsembleAlphaMax));
        Assert.False(double.IsNaN(restored.EnsembleGenrePenaltyFloor));
        Assert.Equal(0.0, restored.EnsembleAlphaMin, 5);
        Assert.Equal(0.0, restored.EnsembleAlphaMax, 5);
        Assert.Equal(0.0, restored.EnsembleGenrePenaltyFloor, 5);

        // The clamp report must surface the coercion so an admin sees the diagnostic on startup.
        var reports = restored.DrainClampReports();
        Assert.Contains(reports, r => r.PropertyName == nameof(PluginConfiguration.EnsembleAlphaMin));
        Assert.Contains(reports, r => r.PropertyName == nameof(PluginConfiguration.EnsembleAlphaMax));
        Assert.Contains(reports, r => r.PropertyName == nameof(PluginConfiguration.EnsembleGenrePenaltyFloor));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3651, 3650)]
    [InlineData(999999, 3650)]
    public void SeerrCleanupAgeDays_OutOfRange_IsClamped(int raw, int expected)
    {
        var config = new PluginConfiguration { SeerrCleanupAgeDays = raw };
        Assert.Equal(expected, config.SeerrCleanupAgeDays);
    }

    [Fact]
    public void SeerrCleanupAgeDays_Zero_StoresZeroAndProducesNoClampReport()
    {
        var config = new PluginConfiguration { SeerrCleanupAgeDays = 0 };
        Assert.Equal(0, config.SeerrCleanupAgeDays);
        Assert.Empty(config.DrainClampReports());
    }

    [Fact]
    public void SeerrCleanupAgeDays_Negative_CollapseToDisabledSentinel()
    {
        var config = new PluginConfiguration { SeerrCleanupAgeDays = -5 };
        Assert.Equal(0, config.SeerrCleanupAgeDays);
    }

    [Theory]
    [InlineData(3651, 3650)]
    public void SeerrCleanupAgeDays_OutOfRange_AppearsInClampReport(int raw, int expected)
    {
        var config = new PluginConfiguration { SeerrCleanupAgeDays = raw };
        var reports = config.DrainClampReports();
        Assert.Contains(reports, r => r.PropertyName == "SeerrCleanupAgeDays"
            && r.RawValue == raw.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && r.ClampedValue == expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SeerrCleanupAgeDays_Valid365_NoClampReport()
    {
        var config = new PluginConfiguration { SeerrCleanupAgeDays = 365 };
        var reports = config.DrainClampReports();
        Assert.DoesNotContain(reports, r => r.PropertyName == "SeerrCleanupAgeDays");
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-100, 0)]
    [InlineData(3651, 3650)]
    public void TrashRetentionDays_OutOfRange_IsClamped(int raw, int expected)
    {
        var config = new PluginConfiguration { TrashRetentionDays = raw };
        Assert.Equal(expected, config.TrashRetentionDays);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(9999, 3650)]
    public void TrashRetentionDays_OutOfRange_AppearsInClampReport(int raw, int expected)
    {
        var config = new PluginConfiguration { TrashRetentionDays = raw };
        var reports = config.DrainClampReports();
        Assert.Contains(reports, r => r.PropertyName == "TrashRetentionDays"
            && r.RawValue == raw.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && r.ClampedValue == expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NormalizeAlphaRange_MinGreaterThanMax_SwappedAutomaticallyByMaxSetter()
    {
        // Set Max=0.8 first, then Min=0.3 - both setters call NormalizeAlphaRange.
        // Setting Max first establishes the upper bound; Min below it stays valid.
        var config = new PluginConfiguration
        {
            EnsembleAlphaMax = 0.8,
            EnsembleAlphaMin = 0.3
        };

        Assert.Equal(0.3, config.EnsembleAlphaMin, precision: 10);
        Assert.Equal(0.8, config.EnsembleAlphaMax, precision: 10);

        var reports = config.DrainClampReports();
        Assert.DoesNotContain(reports, r => r.PropertyName.Contains("EnsembleAlphaRange",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeAlphaRange_CalledManually_IsIdempotentWhenAlreadyOrdered()
    {
        // Set Max first, then Min below it - both setters now call NormalizeAlphaRange,
        // so after construction Min <= Max invariant holds.
        // Calling NormalizeAlphaRange() again must be a no-op.
        var config = new PluginConfiguration
        {
            EnsembleAlphaMax = 0.8,
            EnsembleAlphaMin = 0.3
        };
        config.DrainClampReports();

        config.NormalizeAlphaRange();

        var reports = config.DrainClampReports();
        Assert.DoesNotContain(reports, r => r.PropertyName.Contains("EnsembleAlphaRange",
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0.3, config.EnsembleAlphaMin, precision: 10);
        Assert.Equal(0.8, config.EnsembleAlphaMax, precision: 10);
    }

    [Fact]
    public void NormalizeAlphaRange_MinEqualsMax_NoClampReport()
    {
        var config = new PluginConfiguration { EnsembleAlphaMin = 0.5, EnsembleAlphaMax = 0.5 };
        config.DrainClampReports();
        config.NormalizeAlphaRange();
        var reports = config.DrainClampReports();
        Assert.DoesNotContain(reports, r => r.PropertyName.Contains("EnsembleAlphaRange",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsembleAlphaRange_InvariantHolds_WithoutManualNormalizeCall()
    {
        // Constructing with inverted alpha values must
        // produce a consistent min<=max range even without calling NormalizeAlphaRange().
        var config = new PluginConfiguration
        {
            EnsembleAlphaMin = 0.8,
            EnsembleAlphaMax = 0.4
        };

        Assert.True(config.EnsembleAlphaMin <= config.EnsembleAlphaMax,
            $"Expected Min({config.EnsembleAlphaMin}) <= Max({config.EnsembleAlphaMax})");
    }

    [Fact]
    public void SeerrCleanupAgeDays_Negative_EnqueuesClampReport()
    {
        // Negative values must go through ClampAndReport
        // so the admin sees a diagnostic on startup, not a silent coercion to 0.
        var config = new PluginConfiguration { SeerrCleanupAgeDays = -5 };
        Assert.Equal(0, config.SeerrCleanupAgeDays);
        var reports = config.DrainClampReports();
        Assert.Contains(reports, r => r.PropertyName == nameof(PluginConfiguration.SeerrCleanupAgeDays));
    }

    [Fact]
    public void EnsembleAlphaMin_SetAboveMax_ClampsMaxToMin()
    {
        var config = new PluginConfiguration { EnsembleAlphaMax = 0.5 };
        config.EnsembleAlphaMin = 0.8;

        Assert.True(config.EnsembleAlphaMin <= config.EnsembleAlphaMax,
            $"Min({config.EnsembleAlphaMin}) must be <= Max({config.EnsembleAlphaMax}) after setting Min above Max");
    }

    [Fact]
    public void EnsembleAlphaMin_SetBelowMax_PreservesMax()
    {
        var config = new PluginConfiguration { EnsembleAlphaMax = 0.7 };
        config.EnsembleAlphaMin = 0.3;

        Assert.Equal(0.3, config.EnsembleAlphaMin);
        Assert.Equal(0.7, config.EnsembleAlphaMax);
    }

    [Fact]
    public void EnsembleAlphaMin_SetEqual_IsAllowed()
    {
        var config = new PluginConfiguration { EnsembleAlphaMax = 0.5 };
        config.EnsembleAlphaMin = 0.5;

        Assert.Equal(0.5, config.EnsembleAlphaMin);
        Assert.Equal(0.5, config.EnsembleAlphaMax);
    }
}
