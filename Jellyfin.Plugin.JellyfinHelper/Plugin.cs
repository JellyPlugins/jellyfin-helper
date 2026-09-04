using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper;

/// <summary>
///     The main plugin.
/// </summary>
public partial class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    ///     Upper bound on the number of link hops any single path resolution will follow before
    ///     giving up. Mirrors the typical operating-system SYMLOOP_MAX so a maliciously deep or
    ///     cyclic chain cannot make resolution run unbounded.
    /// </summary>
    internal const int MaxLinkHops = 40;

    /// <summary>
    ///     Data files this plugin persists to DataPath that do <b>not</b> follow the jellyfin-helper-*.json naming convention and therefore would not be matched by the prefix glob in CleanupDataFiles.
    /// </summary>
    private static readonly string[] UnprefixedDataFiles =
    [
        "ml_weights.json",
        "neural_weights.json",
        "ensemble_state.json",
        "jellyfin-helper-batch-generation.txt",
    ];

    /// <summary>
    ///     Resolves the link target of a single path component against the real filesystem, returning
    ///     the final target when the component is a symlink or junction and <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    ///     An <see cref="IOException"/> raised here (for example when the OS reports <c>ELOOP</c> on a
    ///     link cycle) is allowed to propagate so the caller can fail closed.
    /// </remarks>
    internal static readonly Func<string, string?> RealLeafResolver = candidate =>
    {
        FileSystemInfo leaf = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);

        // Resolve a single link hop, not the whole chain. ResolveRealPathCore counts hops and detects
        // cycles between calls, so following the entire chain here would bypass MaxLinkHops and leave
        // the documented bound resting only on the OS ELOOP limit, which differs across platforms.
        return leaf.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
    };

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    ///     Serializes the read-modify-write in UpdateIndexHtml.
    /// </summary>
    private readonly object _indexHtmlLock = new();

    /// <summary>
    ///     Guards the "install File Transformation" warning so it is emitted at most once per server start, even though InjectScript runs both from the constructor and again from the startup hosted service (and could be retried).
    /// </summary>
    private int _readOnlyWarningEmitted;

    /// <summary>
    ///     Initializes a new instance of the <see cref="Plugin" /> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths" /> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer" /> interface.</param>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        Instance = this;
        Api.UserDiscoveryController.ClearRateLimitState();
        ReportClampedConfigValues();
        InjectScript();
    }

    /// <summary>
    ///     Outcome of a fallback UpdateIndexHtml attempt, so callers can react to a genuine write failure (e.g.
    /// </summary>
    internal enum IndexHtmlUpdateResult
    {
        /// <summary>
        ///     The desired state was achieved: the tag was injected/removed and persisted, or the file already matched the desired content so no write was needed.
        /// </summary>
        Success,

        /// <summary>
        ///     The file could not be modified for a reason that installing File Transformation would resolve - most importantly the web directory being read-only (the write threw UnauthorizedAccessException/IOException), but also a missing index.html that we cannot create on a read-only image.
        /// </summary>
        WriteFailed,

        /// <summary>
        ///     Injection did not apply for a content/layout reason (no &lt;/body&gt; to anchor to).
        /// </summary>
        NotApplicable,
    }

    /// <inheritdoc />
    public override string Name => "Jellyfin Helper";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("0c737645-5cbb-4bd8-80c7-d377b560aaa4");

    /// <inheritdoc />
    public override string Description =>
        "Automated cleanup (trickplay, empty folders, subtitles, link repair), media statistics, ML-powered smart recommendations, user activity insights, trash bin, Arr/Seerr integration.";

    /// <summary>
    ///     Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    ///     Gets the logger for use by internal helpers that share the plugin's logging category.
    /// </summary>
    internal ILogger<Plugin> Logger => _logger;

    /// <summary>
    ///     Gets the path to Jellyfin's web UI index.html file.
    /// </summary>
    private string IndexHtmlPath => Path.Combine(_applicationPaths.WebPath, "index.html");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            config.NormalizeAlphaRange();
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        UnregisterFileTransformation();
        UpdateIndexHtml(false);
        CleanupDataFiles();
        CleanupWebPathTempFiles();
        CleanupRecommendationPlaylists();
        try
        {
            Api.UserDiscoveryController.ClearRateLimitState();
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[OnUninstalling] ClearRateLimitState failed (best-effort)");
            }
        }

        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Jellyfin Helper",
                EnableInMainMenu = true,
                MenuIcon = "handyman",
                EmbeddedResourcePath = GetType().Namespace + ".PluginPages.configPage.html"
            },
        ];
    }

    /// <summary>
    ///     Surfaces any config values that were clamped during XML deserialization as a single warning line per affected property.
    /// </summary>
    private void ReportClampedConfigValues()
    {
        // BasePlugin<T>.Configuration is lazily materialised - in the real host it is populated before this ctor runs, but tests spin up a bare Plugin instance without a serializer wiring, so Configuration may still be null here.
        PluginConfiguration? config = null;
        try
        {
            config = Configuration;
        }
        catch (Exception ex)
        {
            // Guarded like the rest of the LogDebug calls in this class so a future
            // parameterized message does not accidentally regress the CA1873 pattern.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[Configuration] Configuration unavailable at startup; skipping clamp report");
            }

            return;
        }

        if (config is null)
        {
            return;
        }

        // Normalize alpha range BEFORE draining reports so any Min > Max swap is included in this drain rather than being silently discarded (PluginServiceRegistrator calls NormalizeAlphaRange during DI build, after the constructor drain already ran).
        config.NormalizeAlphaRange();

        var reports = config.DrainClampReports();
        if (reports.Count == 0)
        {
            return;
        }

        foreach (var entry in reports)
        {
            _logger.LogWarning(
                "[Configuration] Value for {Property} was outside its accepted range and was clamped: {Raw} -> {Clamped}",
                entry.PropertyName,
                entry.RawValue,
                entry.ClampedValue);
        }
    }

    /// <summary>
    ///     Injects the discovery sidebar script tag into Jellyfin's index.html.
    /// </summary>
    internal void InjectScript()
    {
        var registered = RegisterFileTransformation();
        if (registered && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[Discovery Sidebar] Registered with File Transformation plugin (on-the-fly rewriting active)");
        }

        // Always attempt the disk fallback too.
        var result = UpdateIndexHtml(true);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[Discovery Sidebar] Injection at startup: fileTransformationRegistered={Registered}, diskFallback={Result}, webPath={WebPath}",
                registered,
                result,
                _applicationPaths.WebPath);
        }

        if (result == IndexHtmlUpdateResult.WriteFailed
            && !registered
            && Interlocked.Exchange(ref _readOnlyWarningEmitted, 1) == 0)
        {
            // The disk fallback could not write (read-only web dir - the common case on Jellyfin 12 / Docker) AND File Transformation is not available to rewrite the response instead.
            _logger.LogWarning(
                "[Discovery Sidebar] Could not inject the sidebar script into index.html (the Jellyfin web directory appears to be read-only) and the File Transformation plugin is not installed. Install the 'File Transformation' plugin so the Discovery sidebar can be injected without writing to disk.");
        }
    }

    /// <summary>
    ///     Determines whether a loaded assembly is the File Transformation plugin, matching on its exact simple assembly name (Jellyfin.Plugin.FileTransformation).
    /// </summary>
    /// <param name="assembly">The assembly to test.</param>
    /// <returns><c>true</c> if this is the File Transformation plugin assembly.</returns>
    internal static bool IsFileTransformationAssembly(Assembly assembly)
    {
        return string.Equals(
            assembly.GetName().Name,
            "Jellyfin.Plugin.FileTransformation",
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Attempts to register the script injection with the File Transformation plugin. This plugin intercepts file serving and transforms content on-the-fly, avoiding the need to write to the read-only filesystem in Docker containers.
    /// </summary>
    /// <returns>True if registration succeeded, false if the plugin is not available.</returns>
    private bool RegisterFileTransformation()
    {
        try
        {
            if (!TryVerifyFileTransformationAssembly(out var fileTransformationAssembly))
            {
                return false;
            }

            return BuildAndRegisterTransformation(fileTransformationAssembly!);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or TypeLoadException
                                   or FileLoadException
                                   or BadImageFormatException
                                   or TargetInvocationException
                                   or TargetException
                                   or TargetParameterCountException
                                   or MethodAccessException
                                   or MemberAccessException)
        {
            _logger.LogWarning(ex, "[Discovery Sidebar] Failed to register with File Transformation plugin");
            return false;
        }
    }

    /// <summary>
    ///     Locates the File Transformation assembly and verifies that it is loaded from within Jellyfin's plugins directory.
    /// </summary>
    /// <param name="fileTransformationAssembly">The verified assembly, when the method returns true.</param>
    /// <returns><c>true</c> if the assembly was found and its origin verified.</returns>
    private bool TryVerifyFileTransformationAssembly(out Assembly? fileTransformationAssembly)
    {
        fileTransformationAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => IsFileTransformationAssembly(x));

        if (fileTransformationAssembly == null)
        {
            return false;
        }

        // Defense-in-depth: verify the assembly is loaded from within Jellyfin's plugin directory. This does not replace strong-name/signature verification but prevents a rogue assembly placed outside the plugin directory from passing the name check.
        var assemblyLocation = fileTransformationAssembly.Location;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[Discovery Sidebar] FileTransformation assembly found at: {Location}", assemblyLocation);
        }

        // Fail CLOSED: if we cannot determine the assembly location or the plugins path, we cannot verify the origin, so we must NOT register (previously this skipped the check and registered anyway).
        if (string.IsNullOrWhiteSpace(assemblyLocation) || string.IsNullOrWhiteSpace(_applicationPaths.PluginsPath))
        {
            _logger.LogWarning(
                "[Discovery Sidebar] Cannot verify the FileTransformation assembly origin "
                + "(assembly location or plugins path unavailable). Skipping registration as a security precaution.");
            fileTransformationAssembly = null;
            return false;
        }

        // Resolve BOTH paths to their canonical physical form before comparing.
        string normalizedLocation;
        string normalizedPluginsPath;
        try
        {
            normalizedLocation = ResolveRealPath(assemblyLocation);
            normalizedPluginsPath = ResolveRealPath(_applicationPaths.PluginsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "[Discovery Sidebar] Could not resolve the physical path of the FileTransformation "
                + "assembly or the plugins directory. Skipping registration as a security precaution.");
            fileTransformationAssembly = null;
            return false;
        }

        var pluginsPathWithSep = normalizedPluginsPath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        // Path comparison must be OS-aware: Linux paths are case-sensitive, so an OrdinalIgnoreCase compare would treat /plugins and /PLUGINS as equal and could let a differently-cased outside path pass.
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedLocation.StartsWith(pluginsPathWithSep, pathComparison))
        {
            _logger.LogWarning(
                "[Discovery Sidebar] FileTransformation assembly is NOT in the Jellyfin plugins " +
                "directory (expected under '{PluginsPath}', found at '{Location}'). " +
                "Skipping registration as a security precaution.",
                normalizedPluginsPath,
                normalizedLocation);
            fileTransformationAssembly = null;
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Builds the Newtonsoft.Json transformation payload via reflection and registers it with the
    ///     File Transformation plugin.
    /// </summary>
    /// <param name="fileTransformationAssembly">The verified File Transformation assembly.</param>
    /// <returns><c>true</c> if the transformation was registered.</returns>
    private bool BuildAndRegisterTransformation(Assembly fileTransformationAssembly)
    {
        var pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterfaceType == null)
        {
            _logger.LogWarning("[Discovery Sidebar] FileTransformation assembly found but PluginInterface type missing");
            return false;
        }

        // The File Transformation plugin expects a Newtonsoft.Json JObject payload. We construct it via reflection to avoid adding a Newtonsoft.Json package dependency (it's available at runtime as a transitive dependency of Jellyfin).
        var newtonsoftAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.GetName().Name == "Newtonsoft.Json");

        if (newtonsoftAssembly == null)
        {
            _logger.LogWarning("[Discovery Sidebar] Newtonsoft.Json not found at runtime");
            return false;
        }

        var jObjectType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JObject");
        if (jObjectType == null)
        {
            return false;
        }

        var payload = Activator.CreateInstance(jObjectType);
        var jTokenType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JToken");
        if (jTokenType == null)
        {
            _logger.LogWarning("JToken type not found in Newtonsoft.Json assembly");
            return false;
        }

        var addMethod = jObjectType.GetMethod("Add", new Type[] { typeof(string), jTokenType });
        var jValueType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JValue");

        if (payload == null || addMethod == null || jValueType == null)
        {
            _logger.LogWarning("[Discovery Sidebar] FileTransformation reflection payload construction failed (payload={Payload}, addMethod={AddMethod}, jValueType={JValueType})", payload != null, addMethod != null, jValueType != null);
            return false;
        }

        object CreateJValue(string val) => Activator.CreateInstance(jValueType, new object[] { val })!;

        addMethod.Invoke(payload, new[] { "id", CreateJValue(Id.ToString()) });
        addMethod.Invoke(payload, new[] { "fileNamePattern", CreateJValue("index.html") });
        addMethod.Invoke(payload, new[] { "callbackAssembly", CreateJValue(GetType().Assembly.FullName ?? string.Empty) });
        addMethod.Invoke(payload, new[] { "callbackClass", CreateJValue(typeof(TransformationPatches).FullName ?? string.Empty) });
        addMethod.Invoke(payload, new[] { "callbackMethod", CreateJValue(nameof(TransformationPatches.IndexHtml)) });

        var registerMethod = pluginInterfaceType.GetMethod("RegisterTransformation");
        if (registerMethod == null)
        {
            _logger.LogWarning("[Discovery Sidebar] FileTransformation PluginInterface.RegisterTransformation method not found");
            return false;
        }

        registerMethod.Invoke(null, new[] { payload });
        return true;
    }

    /// <summary>
    ///     Resolves a filesystem path to its canonical physical form, following symlinks and junctions on the final component as well as every ancestor directory.
    /// </summary>
    /// <param name="path">The path to canonicalize.</param>
    /// <returns>The real, fully symlink-resolved absolute path.</returns>
    private static string ResolveRealPath(string path)
        => ResolveRealPathCore(path, RealLeafResolver);

    /// <summary>
    ///     Canonicalizes a path by resolving each ancestor directory and then following the final
    ///     component's link target. The traversal is bounded by both <see cref="MaxLinkHops"/> and a
    ///     visited set so that a symlink cycle terminates and returns the last resolved candidate
    ///     rather than recursing without end.
    /// </summary>
    /// <param name="path">The path to canonicalize.</param>
    /// <param name="resolveLeafLink">
    ///     Resolves a single component's link target to its final target's full path, or <c>null</c>
    ///     when the component is not a link. Injected so the bounded traversal is testable without
    ///     real symlinks.
    /// </param>
    /// <returns>The real, fully symlink-resolved absolute path.</returns>
    internal static string ResolveRealPathCore(string path, Func<string, string?> resolveLeafLink)
    {
        // Paths compare case-insensitively on Windows and case-sensitively elsewhere, so the visited
        // set must use the same comparer to detect a cycle correctly on either platform.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer);

        var full = Path.GetFullPath(path);
        var hops = 0;
        while (true)
        {
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent))
            {
                // Root component (e.g. "C:\" or "/"), nothing above it to resolve.
                return full;
            }

            // Canonicalize the parent directory chain first (recursively), then reattach this
            // component's name and resolve the component itself if it is a link.
            var realParent = ResolveRealPathCore(parent, resolveLeafLink);
            var candidate = Path.Combine(realParent, Path.GetFileName(full));

            var resolvedLeaf = resolveLeafLink(candidate);
            if (resolvedLeaf == null)
            {
                return candidate;
            }

            // Stop once we have followed too many hops or would revisit a component, returning the
            // last resolved candidate. This bounds a cycle without throwing.
            if (++hops > MaxLinkHops || !visited.Add(candidate))
            {
                return candidate;
            }

            // The link may point through further links; iterate rather than recurse.
            full = Path.GetFullPath(resolvedLeaf);
        }
    }

    /// <summary>
    ///     Attempts to remove the script injection from the File Transformation plugin. Best-effort: if the plugin is not installed or lacks the removal method, this is a no-op.
    /// </summary>
    private void UnregisterFileTransformation()
    {
        try
        {
            var fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => IsFileTransformationAssembly(x));

            if (fileTransformationAssembly == null)
            {
                return;
            }

            var pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterfaceType == null)
            {
                return;
            }

            // Static void RemoveTransformation(Guid id). Bind the Guid overload explicitly so we don't accidentally match a same-named method with a different signature, and pass the plugin Id as a Guid (not its string form).
            var removeMethod = pluginInterfaceType.GetMethod("RemoveTransformation", new[] { typeof(Guid) });
            if (removeMethod == null)
            {
                return;
            }

            removeMethod.Invoke(null, new object[] { Id });
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Discovery Sidebar] Removed transformation from File Transformation plugin");
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or TypeLoadException
                                   or FileLoadException
                                   or BadImageFormatException
                                   or TargetInvocationException
                                   or TargetException
                                   or TargetParameterCountException
                                   or MethodAccessException
                                   or MemberAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[Discovery Sidebar] Failed to remove transformation from File Transformation plugin (best-effort)");
            }
        }
    }

    /// <summary>
    ///     Adds or removes the discovery sidebar script tag from Jellyfin's index.html. When inject is true, any old version of the tag is replaced with the current one.
    /// </summary>
    /// <param name="inject">Whether to inject (true) or remove (false) the script tag.</param>
    /// <returns>
    ///     An <see cref="IndexHtmlUpdateResult"/> describing whether the update succeeded, failed to
    ///     write (read-only / missing file), or did not apply for a content reason.
    /// </returns>
    internal IndexHtmlUpdateResult UpdateIndexHtml(bool inject)
    {
        // Serialize the whole read-modify-write: the ctor and the startup hosted service can call this concurrently, and "read current content -> strip old tag -> insert current tag -> write only if changed" must be atomic so a second caller cannot inject a duplicate or clobber the first.
        lock (_indexHtmlLock)
        {
            try
            {
                // Sweep any orphaned atomic-write temp files from a prior hard-killed run before
                // writing again, so unique-named leftovers cannot accumulate across restarts.
                CleanupWebPathTempFiles();

                var indexPath = IndexHtmlPath;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[Discovery Sidebar] WebPath = {WebPath}", _applicationPaths.WebPath);
                    _logger.LogDebug("[Discovery Sidebar] IndexHtmlPath = {IndexPath}", indexPath);
                }

                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("[Discovery Sidebar] index.html NOT FOUND at {IndexPath}", indexPath);

                    // A missing index.html when injecting is a genuine "cannot inject via disk" case that File Transformation would resolve (it transforms the served response, not the file).
                    return inject ? IndexHtmlUpdateResult.WriteFailed : IndexHtmlUpdateResult.Success;
                }

                // CA1873: guard every LogDebug in this method consistently.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[Discovery Sidebar] index.html found, reading content...");
                }

                var originalContent = File.ReadAllText(indexPath);
                if (!TryBuildUpdatedIndexContent(originalContent, inject, out var content))
                {
                    return IndexHtmlUpdateResult.NotApplicable;
                }

                if (!string.Equals(content, originalContent, StringComparison.Ordinal))
                {
                    // Use AtomicFile so a transient sharing violation on the final File.Move (typical when Jellyfin's web server or an AV scanner briefly holds the file handle) gets a bounded retry with backoff.
                    AtomicFile.WriteAllText(indexPath, content);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("[Discovery Sidebar] index.html written successfully");
                    }
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[Discovery Sidebar] index.html already up to date; skipping write");
                }

                return IndexHtmlUpdateResult.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A write failure here is the read-only-web-directory case that motivates the File Transformation plugin.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "[Discovery Sidebar] Failed to update index.html on disk");
                }

                return IndexHtmlUpdateResult.WriteFailed;
            }
        }
    }

    /// <summary>
    ///     Strips any prior discovery script tag and, when is true, inserts the current tag before the closing &lt;/body&gt;.
    /// </summary>
    /// <param name="originalContent">The current index.html content.</param>
    /// <param name="inject">Whether to inject the script tag (as opposed to removing it).</param>
    /// <param name="content">The transformed content, when the method returns <c>true</c>.</param>
    /// <returns><c>false</c> when injecting and no <c>&lt;/body&gt;</c> tag was found; otherwise <c>true</c>.</returns>
    private bool TryBuildUpdatedIndexContent(string originalContent, bool inject, out string content)
    {
        content = originalContent;
        var scriptTag = DiscoveryScriptTag.Build(Version.ToString());

        // Remove any old versions of the script tag first
        content = DiscoveryScriptTag.RemovalRegex.Replace(content, string.Empty);

        if (inject)
        {
            var closingBodyTag = "</body>";
            var htmlCloseIndex = content.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            var searchBound = htmlCloseIndex > 0 ? htmlCloseIndex - 1 : content.Length - 1;
            var closingBodyIndex = content.LastIndexOf(closingBodyTag, searchBound, StringComparison.OrdinalIgnoreCase);
            if (closingBodyIndex >= 0)
            {
                content = content.Insert(closingBodyIndex, scriptTag + "\n");
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[Discovery Sidebar] Script tag injected successfully before </body>");
                }
            }
            else
            {
                _logger.LogWarning("[Discovery Sidebar] Could not find </body> tag in index.html");
                return false;
            }
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[Discovery Sidebar] Removing script tag from index.html");
        }

        return true;
    }

    /// <summary>
    ///     Deletes all persistent data files created by this plugin from the Jellyfin data directory.
    /// </summary>
    private void CleanupDataFiles()
    {
        try
        {
            var dataPath = _applicationPaths.DataPath;
            if (!Directory.Exists(dataPath))
            {
                return;
            }

            // Match all files created by this plugin: jellyfin-helper-* Only delete known extensions (.json data files and .tmp atomic-write leftovers) to avoid accidental deletion of unrelated files sharing the prefix.
            foreach (var file in Directory.GetFiles(dataPath, "jellyfin-helper-*"))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DeleteDataFile(file);
            }

            // Recommendation ML/state artifacts that don't match the prefix+extension glob above (unprefixed .json weights, and the .txt batch-generation counter).
            foreach (var name in UnprefixedDataFiles)
            {
                var file = Path.Combine(dataPath, name);
                if (File.Exists(file))
                {
                    DeleteDataFile(file);
                }
            }

            // Per-user recommendation model/state files (ml_weights_{id}.json / ensemble_state_{id}.json).
            // These share no jellyfin-helper- prefix and are not in the fixed list above, so they need their
            // own sweep or an uninstall would leave one pair per user behind. Matched on the exact id-suffixed
            // shape so only files this plugin writes are removed.
            DeletePerUserDataFiles(dataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best effort - if the data directory is inaccessible, nothing we can do.
        }
    }

    private void DeleteDataFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to clean up data file");
        }
    }

    // Deletes only the per-user recommendation files whose full name matches the id-suffixed shape this plugin
    // writes. A glob like ml_weights_*.json would also sweep up a file that merely shares the stem, so the name
    // is checked against the anchored pattern rather than a wildcard.
    // Deletes only the per-user recommendation files whose full name matches the id-suffixed shape this plugin
    // writes. A glob like ml_weights_*.json would also sweep up a file that merely shares the stem, so the name
    // is checked against the anchored pattern rather than a wildcard.
    private void DeletePerUserDataFiles(string dataPath)
    {
        foreach (var file in Directory.GetFiles(dataPath).Where(f => PerUserDataFilePattern().IsMatch(Path.GetFileName(f))))
        {
            DeleteDataFile(file);
        }
    }

    /// <summary>
    ///     Matches only the per-user recommendation model/state files this plugin writes
    ///     (<c>ml_weights_{id:N}.json</c> and <c>ensemble_state_{id:N}.json</c>, where the id is the 32-hex
    ///     user id). Anchored on the full name so uninstall cleanup deletes exactly these and never an
    ///     unrelated file that merely shares the stem (for example a user-made <c>ml_weights_backup.json</c>)
    ///     nor the unsuffixed global files.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"^(?:ml_weights|ensemble_state)_[0-9a-fA-F]{32}\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex PerUserDataFilePattern();

    /// <summary>
    ///     Removes stale atomic-write temp files left in the Jellyfin WebPath by UpdateIndexHtml.
    /// </summary>
    internal void CleanupWebPathTempFiles()
    {
        try
        {
            var webPath = _applicationPaths.WebPath;
            if (!Directory.Exists(webPath))
            {
                return;
            }

            // Only our own atomic-write leftovers: index.html.<something>.tmp.
            foreach (var file in Directory.GetFiles(webPath, "index.html.*.tmp"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort - file may be locked or permission-restricted.
                    // Skip and continue with the next file.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - if the web directory is inaccessible, nothing we can do.
        }
    }

    /// <summary>
    ///     Removes all recommendation playlist folders created by this plugin. Jellyfin stores playlists as subdirectories under {DataPath}/playlists/.
    /// </summary>
    private void CleanupRecommendationPlaylists()
    {
        try
        {
            var playlistsPath = Path.Combine(_applicationPaths.DataPath, "playlists");
            if (!Directory.Exists(playlistsPath))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(playlistsPath))
            {
                var folderName = Path.GetFileName(dir);
                if (!folderName.StartsWith(
                        RecommendationPlaylistService.PlaylistNamePrefix + " for ",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort - folder may be locked or permission-restricted.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - if the playlists directory is inaccessible, nothing we can do.
        }
    }
}