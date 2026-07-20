using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="ModelBindingLogFilter" />.
/// </summary>
/// <remarks>
///     <para>
///         These tests drive the filter through <see cref="IAsyncActionFilter.OnActionExecutionAsync" />
///         with hand-rolled <see cref="ActionExecutingContext" /> instances. That is deliberate —
///         previous versions of this test suite drove the equivalent logic through
///         <see cref="ConfigurationController.UpdateConfigurationAsync" /> directly, which bypassed the
///         MVC pipeline and therefore <em>never exercised the short-circuit path that actually fires
///         in production</em>. Testing the filter as a unit gives us confidence that the
///         <see cref="IPluginLogService" /> WARNING lands whenever ASP.NET Core's model binder rejects a
///         payload, without depending on a full <c>TestServer</c> or in-memory host.
///     </para>
///     <para>
///         Coupling contract: the filter must run <em>before</em> <c>[ApiController]</c>'s auto-400 —
///         see the <c>Order = int.MinValue</c> constant on the filter class. That ordering is verified
///         separately (see <c>Order_IsMinValue_SoRunsBeforeApiControllerAuto400</c>) so a future refactor
///         that quietly bumps the value fails loudly here instead of silently in prod.
///     </para>
/// </remarks>
public class ModelBindingLogFilterTests
{
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger<ModelBindingLogFilter>> _loggerMock = new();

    private ModelBindingLogFilter CreateFilter()
        => new(_pluginLogMock.Object, _loggerMock.Object);

    /// <summary>
    ///     Builds a minimal <see cref="ActionExecutingContext" /> that mirrors what MVC hands to a filter
    ///     during a real request. We supply only the fields the filter actually reads
    ///     (<see cref="ModelStateDictionary" /> + <see cref="ActionExecutingContext.ActionArguments" />)
    ///     so the tests stay decoupled from unrelated MVC plumbing.
    /// </summary>
    private static ActionExecutingContext CreateExecutingContext(
        Action<Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary>? seedModelState = null,
        IDictionary<string, object?>? actionArguments = null)
    {
        var httpContext = new DefaultHttpContext();
        var routeData = new RouteData();
        var actionDescriptor = new ControllerActionDescriptor();
        var actionContext = new ActionContext(httpContext, routeData, actionDescriptor);

        seedModelState?.Invoke(actionContext.ModelState);

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            actionArguments ?? new Dictionary<string, object?> { ["request"] = new object() },
            controller: new object());
    }

    [Fact]
    public async Task Invalid_ModelState_LogsWarning_ShortCircuitsWith400()
    {
        // Arrange: model-binder discovered an invalid field value on the incoming payload.
        var context = CreateExecutingContext(
            seedModelState: ms => ms.AddModelError("SeerrCleanupAgeDays", "The value 'null' is not valid."));

        var nextCalled = false;
        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));
        }

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: response short-circuited with 400 + the failing field name so the UI can render it.
        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        var body = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("SeerrCleanupAgeDays", body, StringComparison.Ordinal);
        Assert.Contains("Invalid request body", body, StringComparison.Ordinal);

        // Assert: WARNING recorded in the plugin log so admins see it in the Logs tab.
        _pluginLogMock.Verify(
            l => l.LogWarning(
                "API",
                It.Is<string>(m => m.Contains("model binding failed", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("SeerrCleanupAgeDays", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);

        // Assert: the action itself must NOT run when the filter rejects.
        Assert.False(nextCalled, "next() must not be invoked when ModelState is invalid.");
    }

    [Fact]
    public async Task Null_RequestArgument_LogsWarning_ShortCircuitsWith400()
    {
        // Arrange: model-binder handed us a `null` value for the [FromBody] parameter — this can
        // happen when the body deserialises to the JSON literal `null`.
        var context = CreateExecutingContext(
            actionArguments: new Dictionary<string, object?> { ["request"] = null });

        var nextCalled = false;
        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));
        }

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        var body = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("Request body is required", body, StringComparison.Ordinal);

        _pluginLogMock.Verify(
            l => l.LogWarning(
                "API",
                It.Is<string>(m => m.Contains("request body was null", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);

        Assert.False(nextCalled, "next() must not be invoked for a null request argument.");
    }

    [Fact]
    public async Task Valid_Request_CallsNext_DoesNotLog()
    {
        // Arrange: the happy path — clean ModelState, non-null argument.
        var context = CreateExecutingContext();

        var nextCalled = false;
        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));
        }

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: pipeline continued and NO diagnostic was written.
        Assert.True(nextCalled, "next() must be invoked when the payload is valid.");
        Assert.Null(context.Result);
        _pluginLogMock.Verify(
            l => l.LogWarning(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Never);
    }

    /// <summary>
    ///     Contract test: the filter MUST have <see cref="int.MinValue" /> as its execution order so it
    ///     runs before <c>[ApiController]</c>'s built-in <c>ModelStateInvalidFilter</c>
    ///     (which uses <c>Order = int.MinValue + 100</c>). Bumping this value would silently disable the
    ///     plugin-log diagnostic — the built-in filter would short-circuit first with its generic 400 and
    ///     ours would never fire. That regression is invisible at runtime (still returns 400) so we lock
    ///     the order value here.
    /// </summary>
    [Fact]
    public void Order_IsMinValue_SoRunsBeforeApiControllerAuto400()
    {
        var filter = CreateFilter();
        Assert.Equal(int.MinValue, filter.Order);
    }
}