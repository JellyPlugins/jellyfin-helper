using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;

/// <summary>
///     Injects the Discovery sidebar script tag into jellyfin-web's index.html at request time.
/// </summary>
/// <remarks>
///     Jellyfin serves index.html as a static file without a script hook, and the web directory is
///     read-only in most deployments. Running ahead of the static-file handler keeps injection
///     self-contained: nothing is written to disk and no second plugin is required. The rewrite is
///     additive, idempotent, and falls back to the original response on any failure.
/// </remarks>
public sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    private const string LogSource = "Discovery";

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private readonly IPluginConfigurationService _configurationService;
    private readonly IPluginLogService _pluginLog;
    private int _loggedOnce;
    private int _failureLogged;
    private int _missingBodyLogged;
    private int _configUnavailableLogged;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="configurationService">Reads the Discovery feature flag that gates injection.</param>
    /// <param name="pluginLog">Plugin log for user-visible startup and failure entries.</param>
    public ScriptInjectionStartupFilter(
        ILogger<ScriptInjectionStartupFilter> logger,
        IPluginConfigurationService configurationService,
        IPluginLogService pluginLog)
    {
        _logger = logger;
        _configurationService = configurationService;
        _pluginLog = pluginLog;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Registered before the rest of the pipeline so the request can be normalized before
            // static-file handling, which is what makes the response readable and rewritable.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    /// <summary>
    ///     Rewrites the web shell response when it belongs to the Discovery feature.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="next">The remainder of the pipeline.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    internal async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!IsIndexRequest(context.Request) || !IsDiscoveryEnabled())
        {
            await next().ConfigureAwait(false);
            return;
        }

        // A compressed or partial response cannot be rewritten safely, so ask the static handler
        // for the complete, plain-text document instead.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            // A downstream failure is not ours to swallow, and the real response has not started yet.
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        if (!IsRewritable(context.Response))
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var html = await ReadBodyAsync(buffer).ConfigureAwait(false);
        var body = InjectScript(html);
        var bytes = Encoding.UTF8.GetBytes(body);

        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        // The body changed, so validators from the static handler no longer describe it.
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes.AsMemory(), context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<string> ReadBodyAsync(Stream buffer)
    {
        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Matches /web, /web/ and /web/index.html, also when hosted under a base-url prefix.
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRewritable(HttpResponse response)
    {
        return response.StatusCode == StatusCodes.Status200OK
            && (response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && response.Headers.ContentEncoding.Count == 0;
    }

    private bool IsDiscoveryEnabled()
    {
        if (!_configurationService.IsInitialized)
        {
            return false;
        }

        try
        {
            return _configurationService.GetConfiguration().DiscoveryUserAccessEnabled;
        }
        catch (InvalidOperationException ex)
        {
            if (Interlocked.Exchange(ref _configUnavailableLogged, 1) == 0)
            {
                _pluginLog.LogWarning(LogSource, $"Discovery configuration unavailable, skipping script injection: {ex.Message}", ex, _logger);
            }

            return false;
        }
    }

    private string InjectScript(string html)
    {
        try
        {
            if (IndexHtmlTransformer.ContainsScriptTag(html))
            {
                return html;
            }

            if (!IndexHtmlTransformer.TryInject(html, DiscoveryScriptTag.Build(_configurationService.PluginVersion), out var injected))
            {
                if (Interlocked.Exchange(ref _missingBodyLogged, 1) == 0)
                {
                    _pluginLog.LogDebug(LogSource, "Script injection skipped: index.html has no closing body tag.", _logger);
                }

                return html;
            }

            if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
            {
                _pluginLog.LogInfo(LogSource, "Script injection active (request-time middleware).", _logger);
            }

            return injected;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Never break the web shell over an injection failure. Log the first failure at
            // warning level so a persistent problem is visible without flooding the plugin log.
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
            {
                _pluginLog.LogWarning(LogSource, $"Script injection failed, serving the original index.html: {ex.Message}", ex, _logger);
            }
            else
            {
                _logger.LogDebug(ex, "[Discovery Sidebar] Script injection failed again, serving the original index.html");
            }

            return html;
        }
    }
}
