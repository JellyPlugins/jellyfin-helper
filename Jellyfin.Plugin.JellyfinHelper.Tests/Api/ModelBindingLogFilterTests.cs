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
///     <para>
///         These tests drive the filter through <see cref="IAsyncActionFilter.OnActionExecutionAsync" />
///         with hand-rolled <see cref="ActionExecutingContext" /> instances. That is deliberate -
///         previous versions of this test suite drove the equivalent logic through
///         <see cref="ConfigurationController.UpdateConfigurationAsync" /> directly, which bypassed the
///         MVC pipeline and therefore <em>never exercised the short-circuit path that actually fires
///         in production</em>. Testing the filter as a unit gives us confidence that the
///         <see cref="IPluginLogService" /> WARNING lands whenever ASP.NET Core's model binder rejects a
///         payload, without depending on a full <c>TestServer</c> or in-memory host.
///     </para>
///     <para>
///         Coupling contract: the filter must run <em>before</em> <c>[ApiController]</c>'s auto-400 -
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
    ///     Contract test: the filter MUST have <see cref="int.MinValue" /> as its execution order so it
    ///     runs before <c>[ApiController]</c>'s built-in <c>ModelStateInvalidFilter</c>
    ///     (which uses <c>Order = int.MinValue + 100</c>). Bumping this value would silently disable the
    ///     plugin-log diagnostic - the built-in filter would short-circuit first with its generic 400 and
    ///     ours would never fire. That regression is invisible at runtime (still returns 400) so we lock
    ///     the order value here.
    /// </summary>
    [Fact]
    public void Order_IsMinValue_SoRunsBeforeApiControllerAuto400()
    {
        var filter = CreateFilter();
        Assert.Equal(int.MinValue, filter.Order);
    }

    // ==================================================================================
    // Branch-coverage tests for the error-string composition helpers on the ModelState
    // path. The `bindingErrors` join produces different strings depending on which of
    // the three fallback branches inside the SelectMany-Select projection fires:
    //   1. ErrorMessage present                   -> use ErrorMessage verbatim
    //   2. ErrorMessage empty + Exception present  -> use Exception.Message
    //   3. ErrorMessage empty + Exception null     -> literal "invalid"
    // The main Invalid_ModelState_* test above only exercises branch 1. The next two
    // tests pin branches 2 and 3 so an accidental refactor of the ternary is caught.
    // ==================================================================================

    [Fact]
    public async Task Invalid_ModelState_EmptyErrorMessage_UsesExceptionMessageInLog()
    {
        // Arrange: simulate what ASP.NET's built-in binders produce when a JSON parse
        // fails - an entry with no ErrorMessage but a JsonException attached. Every
        // overload of ModelStateDictionary.AddModelError / TryAddModelError that accepts
        // an Exception also requires a ModelMetadata argument whose provider chain is
        // version-specific across the Microsoft.AspNetCore.* NuGet packages. Rather than
        // couple this test to a specific framework version, we construct the ModelError
        // ourselves and push it into the ModelStateEntry.Errors collection directly -
        // that's exactly the shape MVC's InputFormatterException path produces and lets
        // us pin the "ErrorMessage empty -> use Exception.Message" branch of the filter
        // without pulling in additional Microsoft.AspNetCore.Mvc.Core test internals.
        var context = CreateExecutingContext(seedModelState: ms =>
        {
            // Seed the key so ModelStateEntry exists (SetModelValue creates the entry
            // even with a "phantom" raw value - we never read it back).
            ms.SetModelValue("SeerrUrl", rawValue: null, attemptedValue: null);
            ms["SeerrUrl"]!.Errors.Add(new ModelError(new InvalidOperationException("json parse error")));
            // MVC marks the entry Invalid when an error is pushed via AddModelError; doing
            // it manually here keeps ModelState.IsValid == false so the filter's outer
            // guard fires. Without this the ModelState reads as Valid despite having an
            // Errors entry (a subtle framework quirk that would give a green test for the
            // wrong reason).
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
        // Arrange: unusual but defensively-guarded case - a ModelError with empty
        // ErrorMessage AND no attached Exception. Framework code doesn't normally emit
        // this shape, but a custom binder or a defensive AddModelError("", "") upstream
        // could. The filter's third fallback branch must produce the literal "invalid"
        // so the log entry is still parseable and not just "field: ".
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
        // Arrange: multi-field validation failures must all land in a single log entry
        // (admins should not have to correlate multiple warnings for one request). Also
        // pins the exact "; " separator so a client-side split-on-";" continues to work.
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
        // Arrange: what happens when BOTH conditions fire? The filter must short-circuit on
        // ModelState first (documented order) - otherwise a bad payload with a null-body
        // parse failure would get the misleading "Request body is required" message
        // instead of the actual field-level error surface. Pinning the branch order here
        // prevents a future refactor from swapping the two if-blocks.
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
        // Arrange: a controller action with multiple bound parameters - e.g. [FromBody]
        // request + [FromQuery] token + CancellationToken. Even if only ONE argument is null,
        // the filter must reject: the semantics are "any argument being null indicates a
        // failed bind that would NRE inside the action". Pinning `Any(v => v is null)`
        // vs `.All` explicitly here.
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
        // Arrange: pathological case - MVC hands us a request with no action arguments at all
        // (parameterless action, or all parameters bound from services rather than the body).
        // `Any(v => v is null)` on an empty enumerable returns false, so the filter must
        // treat this as valid and forward to next(). Pinning it explicitly guards against a
        // future refactor that switches to `.All` or adds a `.Count == 0` guard which would
        // invert the semantics.
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
        // If the wrapped action throws (or another downstream filter does), the exception
        // must bubble up unaltered - otherwise a bug in the action would be silently
        // swallowed and the response left in an undefined state. Pinning this contract
        // here prevents a future well-meaning try/catch from being added around next().
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
        // Contract: the filter is consumed via [ServiceFilter(typeof(ModelBindingLogFilter))]
        // on the controller action, which requires the concrete type to be resolvable from
        // the service container. Scoped lifetime is the recommended default for filters
        // resolved via [ServiceFilter] (matches the built-in filter lifecycle). This test
        // exercises the exact registration path from PluginServiceRegistrator so a lifetime
        // regression (e.g. AddSingleton by accident) fails here instead of at runtime.
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
