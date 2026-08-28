using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
///     xUnit collection definition for tests that modify shared plugin configuration. Tests in this collection are serialized (run sequentially) to avoid race conditions when reading/writing Plugin.Instance configuration.
/// </summary>
/// <remarks>
/// Used by: I18NServiceTests, BackupControllerTests, PathValidatorTests,
/// PluginLogServiceTests, LogsControllerTests.
/// </remarks>
[CollectionDefinition("ConfigOverride")]
public class ConfigOverrideCollection
{
    // Intentionally empty - this class only serves as an anchor for the [CollectionDefinition] attribute.
}