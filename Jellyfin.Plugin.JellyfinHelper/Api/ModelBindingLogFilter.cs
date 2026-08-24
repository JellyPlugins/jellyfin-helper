using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// Action filter that surfaces ASP.NET Core model-binding failures into the plugin log
/// <em>before</em> <c>[ApiController]</c>'s automatic 400 short-circuits the request.
/// </summary>
/// <remarks>
/// <para>
/// <c>[ApiController]</c>'s <c>ModelStateInvalidFilter</c> auto-returns 400 for invalid
/// <c>ModelState</c> (or null body) <em>before</em> the action runs, so a hand-written
/// <c>if (!ModelState.IsValid)</c> in the action is dead code: the 400 goes out but no
/// <see cref="IPluginLogService"/> entry is written.
/// </para>
/// <para>
/// This filter closes the gap as an <see cref="IAsyncActionFilter"/> firing after model binding but
/// before the auto-400 filter (explicit action filters run before the built-in one). On invalid
/// <c>ModelState</c> it logs a WARNING and short-circuits with a body mirroring the field-level
/// errors the frontend used to parse.
/// </para>
/// <para>
/// Scoped so it attaches via <c>[ServiceFilter(typeof(...))]</c> per action. Do NOT register
/// globally - other Jellyfin controllers have their own error contracts and must not be rewritten.
/// </para>
/// </remarks>
public sealed class ModelBindingLogFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<ModelBindingLogFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelBindingLogFilter"/> class.
    /// </summary>
    /// <param name="pluginLog">Plugin log service - WARNING entries land here so admins see them in the Logs tab.</param>
    /// <param name="logger">Fallback logger for structured host logging.</param>
    public ModelBindingLogFilter(IPluginLogService pluginLog, ILogger<ModelBindingLogFilter> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    /// Gets the filter execution order. Set to <see cref="int.MinValue"/> so we run before the
    /// built-in <c>ModelStateInvalidFilter</c> registered by <c>[ApiController]</c>. That filter uses
    /// <c>Order = int.MinValue + 100</c>, so any value strictly smaller wins and gets the chance to
    /// short-circuit the response ourselves (which is what we need to log the diagnostic before the
    /// generic 400 goes out).
    /// </summary>
    public int Order => int.MinValue;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!context.ModelState.IsValid)
        {
            var bindingErrors = string.Join(
                "; ",
                context.ModelState
                    .Where(kvp => kvp.Value?.Errors.Count > 0)
                    .SelectMany(kvp =>
                        kvp.Value!.Errors.Select(e =>
                            $"{kvp.Key}: {(string.IsNullOrEmpty(e.ErrorMessage) ? e.Exception?.Message ?? "invalid" : e.ErrorMessage)}")));

            _pluginLog.LogWarning(
                "API",
                $"Configuration model binding failed: {bindingErrors}",
                logger: _logger);

            context.Result = new BadRequestObjectResult(new { message = $"Invalid request body: {bindingErrors}" });
            return;
        }

        // ASP.NET Core normally rejects null-body 400s before the action is invoked, but the
        // deserialiser can also hand us a null argument when the body is present but shaped
        // like the top-level `null` JSON literal. Guarding here keeps the log entry consistent
        // with the ModelState path above.
        if (context.ActionArguments.Values.Any(v => v is null))
        {
            _pluginLog.LogWarning(
                "API",
                "Configuration update rejected: request body was null.",
                logger: _logger);

            context.Result = new BadRequestObjectResult(new { message = "Request body is required." });
            return;
        }

        await next().ConfigureAwait(false);
    }
}