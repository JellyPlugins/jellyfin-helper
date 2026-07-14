using System;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Shared helper for the "try batch, fall back per-item" pattern that shows up in
///     <see cref="Recommendation.Engine.SimilarityComputer"/>,
///     <see cref="Recommendation.WatchHistory.WatchHistoryService"/> and
///     <see cref="Activity.UserActivityInsightsService"/>. Each of them wraps a Jellyfin 12+
///     batch API and needs to gracefully degrade to per-item calls when the batch fails.
///     Keeping the try/catch shape here means we can't accidentally forget to re-throw
///     <see cref="OperationCanceledException"/> at one of the call sites again (which is
///     exactly what happened in two of them before this got centralised).
/// </summary>
internal static class BatchFallbackHelper
{
    /// <summary>
    ///     Runs <paramref name="batchCall"/> and returns its result. If it throws a
    ///     non-fatal, non-cancellation exception, the callback fires (for logging) and
    ///     <paramref name="fallbackValue"/> comes back so the caller can switch to the
    ///     slower per-item path.
    ///     <para>
    ///         Cancellation is deliberately not caught – if someone cancelled us, they
    ///         want us to stop, not to slow-path through thousands of items. Same story
    ///         for OOM / stack overflow: no point pretending we can recover.
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
    /// <exception cref="OutOfMemoryException">Not caught – process is likely already unrecoverable.</exception>
    /// <exception cref="StackOverflowException">Not caught – process is already terminating by the time this would be reachable.</exception>
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
            // A Task-based batch call that awaits internally can surface cancellation
            // wrapped in AggregateException (e.g. Task.Wait / Task.Result semantics).
            // The naked catch above would miss that shape and let the outer
            // graceful-degradation branch silently swallow the cancel signal, which is
            // exactly the failure mode that Finding #37 flagged. Rethrow the innermost
            // OCE so the caller's cancellation token contract is preserved.
            throw agg.Flatten().InnerExceptions.OfType<OperationCanceledException>().First();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The whole reason this helper exists is that callers always get fallbackValue
            // back on non-cancellation failures. If onFailure itself throws (e.g. a
            // logging provider blew up), swallow it — a broken logger must not break
            // the graceful-degradation contract that all three call sites rely on.
            try
            {
                onFailure(ex);
            }
            catch (Exception callbackEx) when (callbackEx is not OperationCanceledException
                                                and not OutOfMemoryException
                                                and not StackOverflowException)
            {
                // Intentionally swallowed. There's nothing sensible we can do with an
                // exception thrown by the diagnostic callback itself. Cancellation is
                // deliberately excluded — a callback that observes cancellation must be
                // allowed to bubble the signal out of the graceful-degradation path.
            }

            return fallbackValue;
        }
    }

    /// <summary>
    ///     True if the aggregate (after flattening one level of nested AggregateExceptions)
    ///     contains at least one <see cref="OperationCanceledException"/>. Task-based batch
    ///     APIs surface cancellation this way; without unwrapping, the outer catch would
    ///     treat it like any other exception and drop the caller into the fallback path.
    /// </summary>
    private static bool ContainsOperationCanceled(AggregateException agg)
    {
        return agg.Flatten().InnerExceptions.Any(inner => inner is OperationCanceledException);
    }
}
