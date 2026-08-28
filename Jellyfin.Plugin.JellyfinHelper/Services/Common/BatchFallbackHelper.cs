using System;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Shared "try batch, fall back per-item" pattern used by SimilarityComputer, WatchHistoryService and UserActivityInsightsService, each wrapping a Jellyfin 12+ batch API that must degrade to per-item calls on failure.
/// </summary>
internal static class BatchFallbackHelper
{
    /// <summary>
    ///     Runs batchCall and returns its result. On a non-fatal, non-cancellation exception, onFailure fires and fallbackValue is returned so the caller can take the per-item path.
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
            // A Task-based batch call awaiting internally can surface cancellation wrapped in AggregateException (Task.Wait / Task.Result).
            throw agg.Flatten().InnerExceptions.OfType<OperationCanceledException>().First();
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Callers must always get fallbackValue back on non-cancellation failures. If onFailure itself throws (e.g.
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
                // Async loggers can wrap cancellation in AggregateException. Unwrap and rethrow the inner OCE to preserve the cancellation contract.
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
    ///     True if the aggregate (flattened) contains at least one OperationCanceledException.
    /// </summary>
    private static bool ContainsOperationCanceled(AggregateException agg)
    {
        return agg.Flatten().InnerExceptions.Any(inner => inner is OperationCanceledException);
    }
}
