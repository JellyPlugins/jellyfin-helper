using System;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Unit tests for <see cref="BatchFallbackHelper.TryRunBatch{T}"/>.
///     The behavioural contract this class enforces is what the three batch call sites
///     in <c>SimilarityComputer</c>, <c>WatchHistoryService</c> and
///     <c>UserActivityInsightsService</c> silently rely on — so we lock it down here.
/// </summary>
public sealed class BatchFallbackHelperTests
{
    // === Happy path ===

    [Fact]
    public void TryRunBatch_Success_ReturnsBatchResultAndSkipsCallback()
    {
        var callbackCalls = 0;

        var result = BatchFallbackHelper.TryRunBatch(
            batchCall: () => "batch-ok",
            fallbackValue: "fallback",
            onFailure: _ => callbackCalls++);

        Assert.Equal("batch-ok", result);
        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public void TryRunBatch_Success_WithNullResult_ReturnsNullNotFallback()
    {
        // If the batch legitimately returns null (e.g. Jellyfin API contract), we must
        // pass that through — NOT confuse it with a failure and return the fallback value.
        var result = BatchFallbackHelper.TryRunBatch<string?>(
            batchCall: () => null,
            fallbackValue: "should-not-see-this",
            onFailure: _ => Assert.Fail("onFailure must not be invoked on a successful null return."));

        Assert.Null(result);
    }

    // === Non-fatal exceptions -> fallback ===

    [Fact]
    public void TryRunBatch_InvalidOperationException_ReturnsFallbackAndInvokesCallback()
    {
        var thrown = new InvalidOperationException("db died");
        Exception? captured = null;

        var result = BatchFallbackHelper.TryRunBatch<string?>(
            batchCall: () => throw thrown,
            fallbackValue: "fallback",
            onFailure: ex => captured = ex);

        Assert.Equal("fallback", result);
        Assert.Same(thrown, captured);
    }

    [Fact]
    public void TryRunBatch_ArbitraryRuntimeException_ReturnsFallback()
    {
        // Anything that isn't cancellation / OOM / stack overflow should degrade gracefully.
        var result = BatchFallbackHelper.TryRunBatch<int?>(
            batchCall: () => throw new ApplicationException("boom"),
            fallbackValue: -1,
            onFailure: _ => { });

        Assert.Equal(-1, result);
    }

    [Fact]
    public void TryRunBatch_NullFallback_IsReturnedOnFailure()
    {
        // Nullable T with an explicit null fallback is the shape all three call sites use.
        var result = BatchFallbackHelper.TryRunBatch<object?>(
            batchCall: () => throw new InvalidOperationException(),
            fallbackValue: null,
            onFailure: _ => { });

        Assert.Null(result);
    }

    // === Cancellation MUST propagate ===

    [Fact]
    public void TryRunBatch_OperationCanceledException_PropagatesWithoutCallback()
    {
        // This is the invariant that was silently broken in two of three call sites before
        // BatchFallbackHelper existed. If this test ever fails, the whole reason this class
        // exists is gone.
        var callbackCalls = 0;

        Assert.Throws<OperationCanceledException>(() =>
            BatchFallbackHelper.TryRunBatch<string?>(
                batchCall: () => throw new OperationCanceledException(),
                fallbackValue: "should-never-return-this",
                onFailure: _ => callbackCalls++));

        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public void TryRunBatch_TaskCanceledException_PropagatesLikeOperationCanceled()
    {
        // TaskCanceledException derives from OperationCanceledException — same treatment.
        var callbackCalls = 0;

        Assert.Throws<System.Threading.Tasks.TaskCanceledException>(() =>
            BatchFallbackHelper.TryRunBatch<string?>(
                batchCall: () => throw new System.Threading.Tasks.TaskCanceledException(),
                fallbackValue: null,
                onFailure: _ => callbackCalls++));

        Assert.Equal(0, callbackCalls);
    }

    // === Fatal exceptions MUST propagate ===

    [Fact]
    public void TryRunBatch_OutOfMemoryException_PropagatesWithoutCallback()
    {
        var callbackCalls = 0;

        Assert.Throws<OutOfMemoryException>(() =>
            BatchFallbackHelper.TryRunBatch<string?>(
                batchCall: () => throw new OutOfMemoryException(),
                fallbackValue: "should-never-return-this",
                onFailure: _ => callbackCalls++));

        Assert.Equal(0, callbackCalls);
    }

    // Note: We do NOT unit-test StackOverflowException. It cannot be reliably caught /
    // rethrown in userspace on modern .NET runtimes — the process is dead by then. The
    // filter is documentation of intent and CLR-level protection, nothing more.

    // === Argument validation ===

    [Fact]
    public void TryRunBatch_NullBatchCall_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            BatchFallbackHelper.TryRunBatch<string?>(
                batchCall: null!,
                fallbackValue: null,
                onFailure: _ => { }));

        Assert.Equal("batchCall", ex.ParamName);
    }

    [Fact]
    public void TryRunBatch_NullOnFailure_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            BatchFallbackHelper.TryRunBatch<string?>(
                batchCall: () => "irrelevant",
                fallbackValue: null,
                onFailure: null!));

        Assert.Equal("onFailure", ex.ParamName);
    }

    // === Fallback and callback ordering ===

    [Fact]
    public void TryRunBatch_CallbackIsInvokedBeforeReturn_AndOnlyOnce()
    {
        // The three call sites rely on the callback logging BEFORE the caller sees the
        // fallback value, so if anything downstream logs "using fallback" it appears
        // in the right chronological order. Verify with a side-effect counter.
        var order = new System.Collections.Generic.List<string>();

        var result = BatchFallbackHelper.TryRunBatch<string>(
            batchCall: () =>
            {
                order.Add("batch");
                throw new InvalidOperationException();
            },
            fallbackValue: "fallback",
            onFailure: _ => order.Add("callback"));

        Assert.Equal("fallback", result);
        Assert.Equal(new[] { "batch", "callback" }, order);
    }
}