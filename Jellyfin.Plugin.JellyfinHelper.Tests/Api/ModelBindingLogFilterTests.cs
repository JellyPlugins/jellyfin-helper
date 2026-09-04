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
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="ModelBindingLogFilter" />.
/// </summary>
/// <remarks>
///     These tests drive the filter through OnActionExecutionAsync with hand-rolled ActionExecutingContext instances.
/// </remarks>
public class ModelBindingLogFilterTests
{
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger<ModelBindingLogFilter>> _loggerMock = new();

    private ModelBindingLogFilter CreateFilter()
        => new(_pluginLogMock.Object, _loggerMock.Object);

    /// <summary>
    ///     Builds a minimal ActionExecutingContext that mirrors what MVC hands to a filter during a real request.
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
        // Arrange: model-binder handed us a `null` value for the [FromBody] parameter - this can
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
        // Arrange: the happy path - clean ModelState, non-null argument.
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
    ///     Contract test: the filter must order below the built-in ModelStateInvalidFilter (which runs at -2000) so it fires first and can log the binding failure before the automatic 400 is written.
    /// </summary>
    [Fact]
    public void Order_RunsBeforeApiControllerAuto400()
    {
        var filter = CreateFilter();
        Assert.True(filter.Order < -2000, $"Filter order {filter.Order} must be below the built-in ModelStateInvalidFilter order of -2000.");
    }

    // Branch-coverage tests for the error-string composition helpers on the ModelState path. The `bindingErrors` join produces different strings depending on which of the three fallback branches inside the SelectMany-Select projection fires: 1.

    [Fact]
    public async Task Invalid_ModelState_EmptyErrorMessage_UsesExceptionMessageInLog()
    {
        // Simulate JSON parse failure with empty ErrorMessage but attached exception.
        // Construct ModelError directly to avoid coupling to framework version.
        var context = CreateExecutingContext(seedModelState: ms =>
        {
            // Seed the key so ModelStateEntry exists (SetModelValue creates the entry
            // even with a "phantom" raw value - we never read it back).
            ms.SetModelValue("SeerrUrl", rawValue: null, attemptedValue: null);
            ms["SeerrUrl"]!.Errors.Add(new ModelError(new InvalidOperationException("json parse error")));
            // Keep IsValid false so the outer guard fires. Directly adding an error otherwise leaves IsValid true.
            ms["SeerrUrl"]!.ValidationState = ModelValidationState.Invalid;
        });

        var nextCalled = false;
        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));
        }

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: warning body carries the *exception* message (branch 2), not the empty ErrorMessage.
        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        var body = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("SeerrUrl", body, StringComparison.Ordinal);
        Assert.Contains("json parse error", body, StringComparison.Ordinal);

        _pluginLogMock.Verify(
            l => l.LogWarning(
                "API",
                It.Is<string>(m => m.Contains("json parse error", StringComparison.Ordinal)
                                   && m.Contains("SeerrUrl", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invalid_ModelState_EmptyErrorAndNoException_UsesInvalidFallback()
    {
        // Arrange: unusual but defensively-guarded case - a ModelError with empty ErrorMessage AND no attached Exception.
        var context = CreateExecutingContext(seedModelState: ms => ms.AddModelError("TrashRetentionDays", string.Empty));

        var nextCalled = false;
        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));
        }

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: the "invalid" fallback landed in both the response and the log.
        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        var body = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("TrashRetentionDays: invalid", body, StringComparison.Ordinal);

        _pluginLogMock.Verify(
            l => l.LogWarning(
                "API",
                It.Is<string>(m => m.Contains("TrashRetentionDays: invalid", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invalid_ModelState_MultipleErrors_AllJoinedWithSemicolon()
    {
        // Arrange: multi-field validation failures must all land in a single log entry (admins should not have to correlate multiple warnings for one request).
        var context = CreateExecutingContext(seedModelState: ms =>
        {
            ms.AddModelError("SeerrCleanupAgeDays", "The value 'null' is not valid.");
            ms.AddModelError("OrphanMinAgeDays", "Must be non-negative.");
        });

        Task<ActionExecutedContext> Next()
            => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: both keys, both messages, and the "; " separator are present.
        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        var body = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("SeerrCleanupAgeDays", body, StringComparison.Ordinal);
        Assert.Contains("OrphanMinAgeDays", body, StringComparison.Ordinal);
        Assert.Contains("; ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelState_TakesPrecedenceOverNullArgument()
    {
        // Arrange: what happens when BOTH conditions fire? The filter must short-circuit on ModelState first (documented order) - otherwise a bad payload with a null-body parse failure would get the misleading "Request body is required" message instead of the actual field-level error.
        var context = CreateExecutingContext(
            seedModelState: ms => ms.AddModelError("SeerrCleanupAgeDays", "The value 'null' is not valid."),
            actionArguments: new Dictionary<string, object?> { ["request"] = null });

        Task<ActionExecutedContext> Next()
            => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: the ModelState message wins, not the null-body message.
        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        var body = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("SeerrCleanupAgeDays", body, StringComparison.Ordinal);
        Assert.Contains("Invalid request body", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Request body is required", body, StringComparison.Ordinal);

        // Exactly ONE warning must land - not both diagnostics.
        _pluginLogMock.Verify(
            l => l.LogWarning(
                "API",
                It.IsAny<string>(),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task Multiple_ActionArguments_OneNull_StillShortCircuits()
    {
        // Arrange: a controller action with multiple bound parameters - e.g. [FromBody] request + [FromQuery] token + CancellationToken.
        var context = CreateExecutingContext(actionArguments: new Dictionary<string, object?>
        {
            ["request"] = new object(),       // ok
            ["token"] = null,                   // null -> must trigger
            ["cancellationToken"] = new object() // ok
        });

        Task<ActionExecutedContext> Next()
            => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: filter short-circuits even though other args are non-null.
        Assert.IsType<BadRequestObjectResult>(context.Result);
        _pluginLogMock.Verify(
            l => l.LogWarning(
                "API",
                It.Is<string>(m => m.Contains("request body was null", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task Empty_ActionArguments_TreatedAsValid()
    {
        // Arrange: pathological case - MVC hands us a request with no action arguments at all (parameterless action, or all parameters bound from services rather than the body).
        var context = CreateExecutingContext(actionArguments: new Dictionary<string, object?>());

        var nextCalled = false;
        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), controller: new object()));
        }

        // Act
        await CreateFilter().OnActionExecutionAsync(context, Next);

        // Assert: pipeline continued; no diagnostic emitted.
        Assert.True(nextCalled);
        Assert.Null(context.Result);
        _pluginLogMock.Verify(
            l => l.LogWarning(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Never);
    }

    [Fact]
    public async Task Valid_Request_Propagates_NextException()
    {
        // Contract: the filter is a diagnostic gate, NOT a global exception handler.
        var context = CreateExecutingContext();

        var thrown = new InvalidOperationException("action blew up");
        Task<ActionExecutedContext> Next() => throw thrown;

        // Act + Assert
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateFilter().OnActionExecutionAsync(context, Next));
        Assert.Same(thrown, caught);

        // The filter must NOT have written a diagnostic for a downstream exception -
        // that would confuse admins into thinking the payload was rejected.
        _pluginLogMock.Verify(
            l => l.LogWarning(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Never);
    }

    [Fact]
    public void Filter_IsRegistrable_AsScoped_ViaDi()
    {
        // Contract: the filter is consumed via [ServiceFilter(typeof(ModelBindingLogFilter))] on the controller action, which requires the concrete type to be resolvable from the service container.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddScoped<ModelBindingLogFilter>();
        services.AddSingleton(_pluginLogMock.Object);
        services.AddSingleton(_loggerMock.Object);

        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(services);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetService(typeof(ModelBindingLogFilter));
        Assert.NotNull(resolved);
        Assert.IsType<ModelBindingLogFilter>(resolved);
    }
}
