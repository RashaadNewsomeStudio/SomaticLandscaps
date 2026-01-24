using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SomaticLandscapes.Async
{
    /// <summary>
    /// Unity-specific async utilities and extension methods
    /// </summary>
    public static class AsyncExtensions
    {
        /// <summary>
        /// Async wait for specified seconds (respects Time.timeScale)
        /// </summary>
        public static async Task WaitForSeconds(float seconds, CancellationToken ct = default)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                ct.ThrowIfCancellationRequested();  // Check BEFORE accessing Time.deltaTime
                await Task.Yield();
                elapsed += Time.deltaTime;
            }
        }

        /// <summary>
        /// Async wait for specified seconds (ignores Time.timeScale)
        /// </summary>
        public static async Task WaitForSecondsRealtime(float seconds, CancellationToken ct = default)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                ct.ThrowIfCancellationRequested();  // Check BEFORE accessing Time.unscaledDeltaTime
                await Task.Yield();
                elapsed += Time.unscaledDeltaTime;
            }
        }

        /// <summary>
        /// Async wait until predicate returns true (production-hardened for cancellation safety)
        /// </summary>
        /// <remarks>
        /// CRITICAL: Checks cancellation BEFORE evaluating predicate to prevent race conditions.
        /// Wraps predicate in try-catch for graceful handling of null references during teardown.
        /// </remarks>
        public static async Task WaitUntil(Func<bool> predicate, CancellationToken ct = default, int maxWaitMs = 30000)
        {
            var startTime = DateTime.UtcNow;
            
            while (true)
            {
                // CRITICAL: Check cancellation FIRST before touching any potentially-disposed objects
                ct.ThrowIfCancellationRequested();
                
                // Timeout check
                if ((DateTime.UtcNow - startTime).TotalMilliseconds > maxWaitMs)
                    throw new TimeoutException($"WaitUntil timed out after {maxWaitMs}ms");
                
                // Safe predicate evaluation with defensive exception handling
                bool predicateResult;
                try
                {
                    predicateResult = predicate();
                }
                catch (NullReferenceException ex)
                {
                    // Graceful degradation: If objects are disposed during teardown, treat as false
                    Debug.LogWarning($"[AsyncExtensions] WaitUntil predicate threw NullReferenceException (likely during cancellation): {ex.Message}");
                    predicateResult = false;
                }
                catch (Exception ex)
                {
                    // Log and re-throw unexpected exceptions for proper error handling
                    Debug.LogError($"[AsyncExtensions] WaitUntil predicate threw unexpected exception: {ex}");
                    throw;
                }
                
                if (predicateResult)
                    break;
                
                await Task.Yield();
            }
        }

        /// <summary>
        /// Async wait while predicate returns true
        /// </summary>
        public static async Task WaitWhile(Func<bool> predicate, CancellationToken ct = default, int maxWaitMs = 30000)
        {
            await WaitUntil(() => !predicate(), ct, maxWaitMs);
        }

        /// <summary>
        /// Convert Unity AsyncOperation to Task
        /// </summary>
        public static async Task<T> ToTask<T>(this T operation, CancellationToken ct = default) where T : AsyncOperation
        {
            while (!operation.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            return operation;
        }

        /// <summary>
        /// Execute action on main Unity thread
        /// </summary>
        public static async Task RunOnMainThread(Action action, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<bool>();
            
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            
            await tcs.Task;
        }

        /// <summary>
        /// Execute function on main Unity thread and return result
        /// </summary>
        public static async Task<T> RunOnMainThread<T>(Func<T> func, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<T>();
            
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = func();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            
            await tcs.Task;
            return tcs.Task.Result;
        }
    }
}
