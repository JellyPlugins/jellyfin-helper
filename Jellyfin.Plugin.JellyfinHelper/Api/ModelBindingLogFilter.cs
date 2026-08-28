using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Action filter that surfaces ASP.NET Core model-binding failures into the plugin log <em>before</em> [ApiController]'s automatic 400 short-circuits the request.
/// </summary>
/// <remarks>
///     [ApiController]'s ModelStateInvalidFilter auto-returns 400 for invalid ModelState (or null body) <em>before</em> the action runs, so a hand-written if (!ModelState.IsValid) in the action is dead code: the 400 goes out but no IPluginLogService entry is written.
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
    ///     Gets the filter execution order. Set to MinValue so we run before the built-in ModelStateInvalidFilter registered by [ApiController].
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

        // ASP.NET Core normally rejects null-body 400s before the action is invoked, but the deserialiser can also hand us a null argument when the body is present but shaped like the top-level `null` JSON literal.
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