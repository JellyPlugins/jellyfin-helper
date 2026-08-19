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
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    ///     Serializes the read-modify-write in <see cref="UpdateIndexHtml"/>. Injection runs from
    ///     both the constructor and the startup hosted service (and could overlap under real
    ///     parallelism), so without this lock two threads could both read the pre-injection
    ///     content, both insert the tag, and race the write - risking a duplicated tag or a lost
    ///     update. The lock makes "check whether our tag is already present, and only write if it
    ///     changed" a single atomic section.
    /// </summary>
    private readonly object _indexHtmlLock = new();

    /// <summary>
    ///     Data files this plugin persists to <see cref="IApplicationPaths.DataPath"/> that do
    ///     <b>not</b> follow the <c>jellyfin-helper-*.json</c> naming convention and therefore
    ///     would not be matched by the prefix glob in <see cref="CleanupDataFiles"/>. Listed by
    ///     exact name so uninstall removes them without widening the glob (which would risk
    ///     deleting unrelated files):
    ///     <list type="bullet">
    ///         <item><c>ml_weights.json</c> - learned scoring weights (<see cref="PluginServiceRegistrator"/>)</item>
    ///         <item><c>neural_weights.json</c> - neural scoring weights (<see cref="PluginServiceRegistrator"/>)</item>
    ///         <item><c>ensemble_state.json</c> - ensemble alpha/state (<see cref="PluginServiceRegistrator"/>)</item>
    ///         <item><c>jellyfin-helper-batch-generation.txt</c> - batch-generation counter (has the
    ///         prefix but a <c>.txt</c> extension the glob's extension guard excludes)</item>
    ///     </list>
    /// </summary>
    private static readonly string[] UnprefixedDataFiles =
    [
        "ml_weights.json",
        "neural_weights.json",
        "ensemble_state.json",
        "jellyfin-helper-batch-generation.txt",
    ];

    /// <summary>
    ///     Guards the "install File Transformation" warning so it is emitted at most once per
    ///     server start, even though <see cref="InjectScript"/> runs both from the constructor and
    ///     again from the startup hosted service (and could be retried). <c>0</c> = not yet warned,
    ///     <c>1</c> = already warned; flipped atomically via <see cref="Interlocked.Exchange(ref int, int)"/>.
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
    ///     Outcome of a fallback <see cref="UpdateIndexHtml"/> attempt, so callers can react to a
    ///     genuine write failure (e.g. a read-only web directory) without re-inspecting the file.
    /// </summary>
    internal enum IndexHtmlUpdateResult
    {
        /// <summary>
        ///     The desired state was achieved: the tag was injected/removed and persisted, or the
        ///     file already matched the desired content so no write was needed.
        /// </summary>
        Success,

        /// <summary>
        ///     The file could not be modified for a reason that installing File Transformation would
        ///     resolve - most importantly the web directory being read-only (the write threw
        ///     <see cref="UnauthorizedAccessException"/>/<see cref="IOException"/>), but also a
        ///     missing <c>index.html</c> that we cannot create on a read-only image.
        /// </summary>
        WriteFailed,

        /// <summary>
        ///     Injection did not apply for a content/layout reason (no <c>&lt;/body&gt;</c> to anchor
        ///     to). This is not a permissions problem, so suggesting File Transformation would not
        ///     help; the existing warning already describes it.
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
    ///     Surfaces any config values that were clamped during XML deserialization as a single
    ///     warning line per affected property. Fixes the previous silent-clamp behaviour where a
    ///     hand-edited value outside the accepted range would be quietly narrowed with no
    ///     feedback to the operator.
    /// </summary>
    private void ReportClampedConfigValues()
    {
        // BasePlugin<T>.Configuration is lazily materialised - in the real host it is populated
        // before this ctor runs, but tests spin up a bare Plugin instance without a serializer
        // wiring, so Configuration may still be null here. Skip silently in that case.
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

        // Normalize alpha range BEFORE draining reports so any Min > Max swap is included
        // in this drain rather than being silently discarded (PluginServiceRegistrator calls
        // NormalizeAlphaRange during DI build, after the constructor drain already ran).
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
    ///     Registers with the File Transformation plugin when present (on-the-fly response
    ///     rewriting, survives web-asset updates, works on read-only web dirs) AND always writes
    ///     the disk fallback as a belt-and-suspenders guarantee.
    ///     <para>
    ///         Why always write the disk fallback rather than skipping it when File Transformation
    ///         registered: "registered" does not prove the transformation is actually being applied
    ///         to the served response (the plugin could be a stale/incompatible build, or the
    ///         registration could silently no-op). Relying on registration alone is exactly what
    ///         made the sidebar silently absent on a fresh server. The two paths are safe together -
    ///         <see cref="TransformationPatches.IndexHtml"/> strips any existing tag via
    ///         <see cref="DiscoveryScriptTag.RemovalRegex"/> before inserting, so a disk-injected tag
    ///         is de-duplicated in the served output; and <see cref="UpdateIndexHtml"/> is idempotent
    ///         (skips the write when the file already carries the current tag). The only case the disk
    ///         write cannot cover - a read-only web dir - is precisely where File Transformation is
    ///         needed, and we surface that as one actionable warning.
    ///     </para>
    /// </summary>
    internal void InjectScript()
    {
        var registered = RegisterFileTransformation();
        if (registered && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[Discovery Sidebar] Registered with File Transformation plugin (on-the-fly rewriting active)");
        }

        // Always attempt the disk fallback too. It is idempotent (no write when the tag is already
        // present) and de-duplicated by the File Transformation callback, so writing it while a
        // transformation is also registered is harmless - but it guarantees the sidebar appears even
        // when File Transformation is absent, or registered-but-not-actually-applying.
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
            // The disk fallback could not write (read-only web dir - the common case on Jellyfin 12
            // / Docker) AND File Transformation is not available to rewrite the response instead.
            // Emit ONE actionable warning per server start (not a raw stack trace, not on every
            // re-injection attempt) so the admin knows exactly what to do.
            _logger.LogWarning(
                "[Discovery Sidebar] Could not inject the sidebar script into index.html (the Jellyfin web directory appears to be read-only) and the File Transformation plugin is not installed. Install the 'File Transformation' plugin so the Discovery sidebar can be injected without writing to disk.");
        }
    }

    /// <summary>
    ///     Determines whether a loaded assembly is the File Transformation plugin, matching on its
    ///     exact simple assembly name (<c>Jellyfin.Plugin.FileTransformation</c>).
    ///     <para>
    ///         This is a precise, positive identity check - not a loose substring scan of the full
    ///         assembly name - so an unrelated assembly that merely happens to contain the text
    ///         ".FileTransformation" somewhere (including this plugin's own
    ///         <c>...Services.FileTransformation</c> namespace, which is a namespace, not an assembly
    ///         name) can never be mistaken for the File Transformation plugin. Getting this wrong in
    ///         either direction is harmless to correctness now that the disk fallback always runs
    ///         (see <see cref="InjectScript"/>), but a precise check keeps the registration path and
    ///         its logging honest.
    ///     </para>
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
    ///     Attempts to register the script injection with the File Transformation plugin.
    ///     This plugin intercepts file serving and transforms content on-the-fly,
    ///     avoiding the need to write to the read-only filesystem in Docker containers.
    /// </summary>
    /// <returns>True if registration succeeded, false if the plugin is not available.</returns>
    private bool RegisterFileTransformation()
    {
        try
        {
            var fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => IsFileTransformationAssembly(x));

            if (fileTransformationAssembly == null)
            {
                return false;
            }

            // Defense-in-depth: verify the assembly is loaded from within Jellyfin's plugin
            // directory. This does not replace strong-name/signature verification but prevents
            // a rogue assembly placed outside the plugin directory from passing the name check.
            var assemblyLocation = fileTransformationAssembly.Location;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[Discovery Sidebar] FileTransformation assembly found at: {Location}", assemblyLocation);
            }

            if (!string.IsNullOrEmpty(assemblyLocation) && !string.IsNullOrEmpty(_applicationPaths.PluginsPath))
            {
                var normalizedLocation = Path.GetFullPath(assemblyLocation);
                var normalizedPluginsPath = Path.GetFullPath(_applicationPaths.PluginsPath);
                var pluginsPathWithSep = normalizedPluginsPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                if (!normalizedLocation.StartsWith(pluginsPathWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "[Discovery Sidebar] FileTransformation assembly is NOT in the Jellyfin plugins " +
                        "directory (expected under '{PluginsPath}', found at '{Location}'). " +
                        "Skipping registration as a security precaution.",
                        normalizedPluginsPath,
                        normalizedLocation);
                    return false;
                }
            }

            var pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterfaceType == null)
            {
                _logger.LogWarning("[Discovery Sidebar] FileTransformation assembly found but PluginInterface type missing");
                return false;
            }

            // The File Transformation plugin expects a Newtonsoft.Json JObject payload.
            // We construct it via reflection to avoid adding a Newtonsoft.Json package dependency
            // (it's available at runtime as a transitive dependency of Jellyfin).
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
    ///     Attempts to remove the script injection from the File Transformation plugin.
    ///     Best-effort: if the plugin is not installed or lacks the removal method, this is a no-op.
    ///     Called during <see cref="OnUninstalling"/> to clean up the registered transformation.
    ///     <para>
    ///         Targets the v12 API <c>PluginInterface.RemoveTransformation(Guid)</c>. Earlier
    ///         plugin builds exposed <c>UnregisterTransformation(string)</c>; that name is
    ///         intentionally not probed since this plugin supports the v12 File Transformation
    ///         API only (the runtime ABI is Jellyfin 12 / net10).
    ///     </para>
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

            // v12 removal API: static void RemoveTransformation(Guid id). Bind the Guid overload
            // explicitly so we don't accidentally match a same-named method with a different
            // signature, and pass the plugin Id as a Guid (not its string form).
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
    ///     Adds or removes the discovery sidebar script tag from Jellyfin's index.html.
    ///     When <paramref name="inject"/> is true, any old version of the tag is replaced with the current one.
    ///     When false, the tag is removed entirely (used during uninstall).
    /// </summary>
    /// <param name="inject">Whether to inject (true) or remove (false) the script tag.</param>
    /// <returns>
    ///     An <see cref="IndexHtmlUpdateResult"/> describing whether the update succeeded, failed to
    ///     write (read-only / missing file), or did not apply for a content reason.
    /// </returns>
    internal IndexHtmlUpdateResult UpdateIndexHtml(bool inject)
    {
        // Serialize the whole read-modify-write: the ctor and the startup hosted service can call
        // this concurrently, and "read current content → strip old tag → insert current tag → write
        // only if changed" must be atomic so a second caller cannot inject a duplicate or clobber
        // the first write. When our tag is already present the content is unchanged and no write
        // happens (idempotent), so repeated calls are cheap and safe.
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

                    // A missing index.html when injecting is a genuine "cannot inject via disk" case that
                    // File Transformation would resolve (it transforms the served response, not the file).
                    // When removing (uninstall cleanup), a missing file is already the desired end state.
                    return inject ? IndexHtmlUpdateResult.WriteFailed : IndexHtmlUpdateResult.Success;
                }

                // CA1873: guard every LogDebug in this method consistently.
                // These particular calls use constant messages (no expensive argument evaluation),
                // so the runtime win is negligible - the value of the guard here is _consistency_:
                // it prevents future maintainers from adding a parameterized LogDebug to this
                // block and accidentally regressing the CA1873 pattern the class opted into.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[Discovery Sidebar] index.html found, reading content...");
                }

                var originalContent = File.ReadAllText(indexPath);
                var content = originalContent;
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
                        return IndexHtmlUpdateResult.NotApplicable;
                    }
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[Discovery Sidebar] Removing script tag from index.html");
                }

                if (!string.Equals(content, originalContent, StringComparison.Ordinal))
                {
                    // Use AtomicFile so a transient sharing violation on the final File.Move
                    // (typical when Jellyfin's web server or an AV scanner briefly holds the
                    // file handle) gets a bounded retry with backoff. AtomicFile also handles
                    // temp-file cleanup internally, so no finally block is required here.
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
                // A write failure here is the read-only-web-directory case that motivates the
                // File Transformation plugin. Log at debug (the actionable guidance is emitted once,
                // higher up in InjectScript) and report the failure to the caller.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "[Discovery Sidebar] Failed to update index.html on disk");
                }

                return IndexHtmlUpdateResult.WriteFailed;
            }
        }
    }

    /// <summary>
    ///     Deletes all persistent data files created by this plugin from the Jellyfin data directory.
    ///     Most plugin data files follow the naming convention <c>jellyfin-helper-*.json</c>:
    ///     <list type="bullet">
    ///         <item><c>jellyfin-helper-statistics-latest.json</c> - media statistics cache</item>
    ///         <item><c>jellyfin-helper-recommendations-latest.json</c> - recommendation results cache</item>
    ///         <item><c>jellyfin-helper-useractivity-latest.json</c> - user activity insights cache</item>
    ///         <item><c>jellyfin-helper-growth-timeline.json</c> - library growth timeline data</item>
    ///         <item><c>jellyfin-helper-growth-baseline.json</c> - library growth baseline snapshot</item>
    ///     </list>
    ///     Also removes any leftover <c>.tmp</c> files from atomic write operations, plus the
    ///     recommendation ML/state artifacts that predate the naming convention (see
    ///     <see cref="UnprefixedDataFiles"/>).
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

            // Match all files created by this plugin: jellyfin-helper-*
            // Only delete known extensions (.json data files and .tmp atomic-write leftovers)
            // to avoid accidental deletion of unrelated files sharing the prefix.
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

            // Recommendation ML/state artifacts that don't match the prefix+extension glob above
            // (unprefixed .json weights, and the .txt batch-generation counter). Deleted by exact
            // name so we never widen the glob and catch unrelated files.
            foreach (var name in UnprefixedDataFiles)
            {
                var file = Path.Combine(dataPath, name);
                if (File.Exists(file))
                {
                    DeleteDataFile(file);
                }
            }
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

    /// <summary>
    ///     Removes stale atomic-write temp files left in the Jellyfin <c>WebPath</c> by
    ///     <see cref="UpdateIndexHtml"/>. <see cref="AtomicFile.WriteAllText"/> writes to a
    ///     uniquely-named <c>index.html.&lt;guid&gt;.tmp</c> before renaming it over the target;
    ///     it cleans that temp file up in-process on failure, but a hard process kill
    ///     (OOM / container SIGKILL) between the write and the rename orphans it. Because the
    ///     name is unique per attempt, such orphans would otherwise accumulate forever -
    ///     <see cref="CleanupDataFiles"/> only sweeps <c>DataPath</c>, never <c>WebPath</c>.
    ///     Swept on uninstall and at the start of each <see cref="UpdateIndexHtml"/> run so
    ///     leftovers cannot build up across crashes/restarts.
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

            // Only our own atomic-write leftovers: index.html.<something>.tmp. The middle
            // segment is a GUID in practice, but the glob stays permissive while the
            // "index.html." prefix + ".tmp" suffix keep it scoped to this plugin's writes
            // and away from Jellyfin's real index.html.
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
    ///     Removes all recommendation playlist folders created by this plugin.
    ///     Jellyfin stores playlists as subdirectories under <c>{DataPath}/playlists/</c>.
    ///     Managed playlists are identified by the
    ///     <see cref="RecommendationPlaylistService.PlaylistNamePrefix"/> folder name prefix.
    ///     This is a best-effort filesystem cleanup - the Jellyfin library database may still
    ///     reference these playlists until the next library scan, at which point the stale
    ///     entries will be removed automatically.
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