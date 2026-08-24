using System;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Shared "try batch, fall back per-item" pattern used by
///     <see cref="Recommendation.Engine.SimilarityComputer"/>,
///     <see cref="Recommendation.WatchHistory.WatchHistoryService"/> and
///     <see cref="Activity.UserActivityInsightsService"/>, each wrapping a Jellyfin 12+
///     batch API that must degrade to per-item calls on failure. Centralising the
///     try/catch guarantees <see cref="OperationCanceledException"/> is always re-thrown
///     (two call sites forgot to before this was extracted).
/// </summary>
internal static class BatchFallbackHelper
{
    /// <summary>
    ///     Runs <paramref name="batchCall"/> and returns its result. On a non-fatal,
    ///     non-cancellation exception, <paramref name="onFailure"/> fires and
    ///     <paramref name="fallbackValue"/> is returned so the caller can take the per-item path.
    ///     <para>
    ///         Cancellation is not caught: a cancel means stop, not slow-path through
    ///         thousands of items. Same for OOM / stack overflow, which are unrecoverable.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">Return type of the batch call.</typeparam>
    /// <param name="batchCall">The batch operation to run.</param>
    /// <param name="fallbackValue">What to return when the batch fails non-fatally.</param>
    /// <param name="onFailure">Diagnostic callback (typically a <c>LogWarning</c>).</param>
    /// <returns>The batch result on success, otherwise <paramref name="fallbackValue"/>.</returns>
    /// <exception cref="ArgumentNullException">
    ///     If <paramref name="batchCall"/> or <paramref name="onFailure"/> is null.
    /// </exception>
    /// <exception cref="OperationCanceledException">Rethrown as-is (cancellation is a stop signal, not a degradable error).</exception>
    /// <exception cref="OutOfMemoryException">Not caught: process is likely already unrecoverable.</exception>
    /// <exception cref="StackOverflowException">Not caught: process is already terminating by the time this would be reachable.</exception>
    internal static T TryRunBatch<T>(
        Func<T> batchCall,
        T fallbackValue,
        Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(batchCall);
        ArgumentNullException.ThrowIfNull(onFailure);

        try
        {
            return batchCall();
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate, not fall through to the slow path.
            throw;
        }
        catch (AggregateException agg) when (ContainsOperationCanceled(agg))
        {
            // A Task-based batch call awaiting internally can surface cancellation wrapped
            // in AggregateException (Task.Wait / Task.Result). The naked catch above misses
            // that shape, letting graceful degradation swallow the cancel. Rethrow the inner
            // OCE to preserve the caller's cancellation contract.
            // Known limitation: with multiple OCEs, the first is rethrown and its
            // CancellationToken may differ from the caller's.
            throw agg.Flatten().InnerExceptions.OfType<OperationCanceledException>().First();
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Callers must always get fallbackValue back on non-cancellation failures. If
            // onFailure itself throws (e.g. a broken logger), swallow it so it can't break
            // the graceful-degradation contract all three call sites rely on.
            try
            {
                onFailure(ex);
            }
            catch (OperationCanceledException)
            {
                // Callback observed cancellation - must bubble out of the graceful-degradation path.
                throw;
            }
            catch (AggregateException agg) when (ContainsOperationCanceled(agg))
            {
                // Async loggers can wrap cancellation in AggregateException. Unwrap and
                // rethrow the inner OCE to preserve the cancellation contract.
                // Known limitation: with multiple OCEs, the first is rethrown and its
                // CancellationToken may differ from the caller's.
                throw agg.Flatten().InnerExceptions.OfType<OperationCanceledException>().First();
            }
            catch (Exception callbackEx) when (!callbackEx.IsFatal())
            {
                // Intentionally swallowed. There's nothing sensible we can do with an
                // exception thrown by the diagnostic callback itself.
            }

            return fallbackValue;
        }
    }

    /// <summary>
    ///     True if the aggregate (flattened) contains at least one
    ///     <see cref="OperationCanceledException"/>. Task-based batch APIs surface
    ///     cancellation this way; without unwrapping, the outer catch drops the caller
    ///     into the fallback path.
    /// </summary>
    private static bool ContainsOperationCanceled(AggregateException agg)
    {
        return agg.Flatten().InnerExceptions.Any(inner => inner is OperationCanceledException);
    }
}
