using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Configuration;

/// <summary>
/// Tests for XML serialization/deserialization of PluginConfiguration,
/// specifically verifying that multiple Arr instances persist correctly.
/// </summary>
public class PluginConfigurationSerializationTests
{
    private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

    /// <summary>
    /// Serializes the configuration to XML and deserializes it back,
    /// verifying round-trip fidelity.
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
            ApiKey = "key1",
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
            ApiKey = "key1",
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr 4K",
            Url = "http://localhost:7879",
            ApiKey = "key2",
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr Anime",
            Url = "http://localhost:7880",
            ApiKey = "key3",
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
            ApiKey = "skey1",
        });
        config.SonarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Sonarr Anime",
            Url = "http://localhost:8990",
            ApiKey = "skey2",
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
            ApiKey = "rkey",
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr 4K",
            Url = "http://radarr4k:7878",
            ApiKey = "rkey4k",
        });
        config.SonarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Sonarr",
            Url = "http://sonarr:8989",
            ApiKey = "skey",
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
            IncludedLibraries = "Movies, TV",
            ExcludedLibraries = "Music",
            OrphanMinAgeDays = 7,
            TrickplayTaskMode = TaskMode.Activate,
            Language = "de",
            UseTrash = true,
            TrashFolderPath = "/tmp/trash",
            TrashRetentionDays = 14,
        };
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr",
            Url = "http://localhost:7878",
            ApiKey = "testkey",
        });

        var restored = RoundTrip(config);

        Assert.Equal("Movies, TV", restored.IncludedLibraries);
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
            ApiKey = "key1",
        });
        config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr 4K",
            Url = "http://localhost:7879",
            ApiKey = "key2",
        });

        // Round-trip multiple times to ensure no duplication
        var restored1 = RoundTrip(config);
        var restored2 = RoundTrip(restored1);
        var restored3 = RoundTrip(restored2);

        Assert.Equal(2, restored3.RadarrInstances.Count);
        Assert.Equal("Radarr", restored3.RadarrInstances[0].Name);
        Assert.Equal("Radarr 4K", restored3.RadarrInstances[1].Name);
    }
}