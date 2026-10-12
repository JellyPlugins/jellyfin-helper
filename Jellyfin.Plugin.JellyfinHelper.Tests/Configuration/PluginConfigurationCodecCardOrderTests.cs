using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Configuration;

/// <summary>
///     Tests for the <see cref="PluginConfiguration.CodecCardOrder" /> property: the null-guard setter
///     and XML (de)serialization fidelity, including backward compatibility with configs saved before
///     the element existed.
/// </summary>
public class PluginConfigurationCodecCardOrderTests
{
    private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

    private static PluginConfiguration RoundTrip(PluginConfiguration config)
    {
        using var writer = new StringWriter();
        Serializer.Serialize(writer, config);
        var xml = writer.ToString();

        using var reader = new StringReader(xml);
        return (PluginConfiguration)Serializer.Deserialize(reader)!;
    }

    [Fact]
    public void CodecCardOrder_SetToNull_CoercedToEmptyList()
    {
        var config = new PluginConfiguration { CodecCardOrder = null! };

        Assert.NotNull(config.CodecCardOrder);
        Assert.Empty(config.CodecCardOrder);
    }

    [Fact]
    public void CodecCardOrder_DefaultsToEmptyList()
    {
        var config = new PluginConfiguration();

        Assert.NotNull(config.CodecCardOrder);
        Assert.Empty(config.CodecCardOrder);
    }

    [Fact]
    public void XmlRoundTrip_Permutation_Preserved()
    {
        var permutation = CodecCardCatalog.Canonical.Reverse().ToList();
        var config = new PluginConfiguration { CodecCardOrder = permutation };

        var restored = RoundTrip(config);

        Assert.Equal(permutation, restored.CodecCardOrder);
    }

    [Fact]
    public void Deserialize_LegacyXmlWithoutElement_DefaultsToEmptyList()
    {
        // A config file written before CodecCardOrder existed has no element for it; deserialization
        // must leave the property at its empty default rather than null.
        const string xml = @"<?xml version=""1.0"" encoding=""utf-16""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <OrphanMinAgeDays>7</OrphanMinAgeDays>
</PluginConfiguration>";

        using var reader = new StringReader(xml);
        var restored = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.NotNull(restored.CodecCardOrder);
        Assert.Empty(restored.CodecCardOrder);
    }
}
