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
        public static async Task WaitForSeconds(float seconds, CancellationToken ct = default)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                elapsed += Time.deltaTime;
            }
        }

        public static async Task WaitForSecondsRealtime(float seconds, CancellationToken ct = default)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                elapsed += Time.unscaledDeltaTime;
            }
        }

        public static async Task WaitUntil(Func<bool> predicate, CancellationToken ct = default, int maxWaitMs = -1)
        {
            var startTime = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (maxWaitMs > 0 && (DateTime.UtcNow - startTime).TotalMilliseconds > maxWaitMs)
                    throw new TimeoutException($"WaitUntil timed out after {maxWaitMs}ms");

                bool result;
                try { result = predicate(); }
                catch { result = false; }
                
                if (result) break;
                await Task.Yield();
            }
        }

        public static async Task WaitFrames(int frameCount, CancellationToken ct = default)
        {
            int target = Time.frameCount + frameCount;
            while (Time.frameCount < target)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        public static async Task WaitWhile(Func<bool> predicate, CancellationToken ct = default, int maxWaitMs = 30000)
        {
            await WaitUntil(() => !predicate(), ct, maxWaitMs);
        }

        public static async Task<T> ToTask<T>(this T operation, CancellationToken ct = default) where T : AsyncOperation
        {
            while (!operation.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            return operation;
        }

        public static async Task RunOnMainThread(Action action, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<bool>();
            GlobalMainThreadDispatcher.Enqueue(() =>
            {
                try { 
                    ct.ThrowIfCancellationRequested(); 
                    action(); 
                    tcs.SetResult(true); 
                } catch (Exception ex) { tcs.SetException(ex); }
            });
            await tcs.Task;
        }

        public static async Task<T> RunOnMainThread<T>(Func<T> func, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<T>();
            GlobalMainThreadDispatcher.Enqueue(() =>
            {
                try { 
                    ct.ThrowIfCancellationRequested(); 
                    tcs.SetResult(func()); 
                } catch (Exception ex) { tcs.SetException(ex); }
            });
            await tcs.Task;
            return tcs.Task.Result;
        }

        public static async Task WaitForEndOfFrame(CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<bool>();
            if (GlobalMainThreadDispatcher.Instance == null) { await WaitFrames(1, ct); return; }
            GlobalMainThreadDispatcher.Enqueue(() => GlobalMainThreadDispatcher.Instance.StartCoroutine(CoWaitForEndOfFrame(tcs, ct)));
            await tcs.Task;
        }

        private static System.Collections.IEnumerator CoWaitForEndOfFrame(TaskCompletionSource<bool> tcs, CancellationToken ct)
        {
            yield return new WaitForEndOfFrame();
            if (ct.IsCancellationRequested) tcs.SetCanceled();
            else tcs.SetResult(true);
        }

        // =========================================================================================
        // FADE EXTENSIONS
        // =========================================================================================

        public static async Task FadeAlpha(this CanvasGroup g, float from, float to, float duration, CancellationToken ct)
        {
            if (!g) return;
            duration = Mathf.Max(0.01f, duration);
            float t = 0f;
            g.alpha = from;
            try 
            {
                while (t < duration)
                {
                    ct.ThrowIfCancellationRequested();
                    t += Time.deltaTime;
                    if (g) g.alpha = Mathf.Lerp(from, to, t / duration);
                    await Task.Yield();
                }
                if (g) g.alpha = to;
            }
            catch (OperationCanceledException) 
            {
                // Expected if faded out mid-way
            }
        }

        public static async Task Crossfade(CanvasGroup a, CanvasGroup b, float toA, float toB, float duration, CancellationToken ct)
        {
            duration = Mathf.Max(0.01f, duration);
            float t = 0f;
            float startA = a ? a.alpha : 0f;
            float startB = b ? b.alpha : 0f;
            
            while (t < duration)
            {
                ct.ThrowIfCancellationRequested();
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / duration);
                if (a) a.alpha = Mathf.Lerp(startA, toA, k);
                if (b) b.alpha = Mathf.Lerp(startB, toB, k);
                await Task.Yield();
            }
            if (a) a.alpha = toA;
            if (b) b.alpha = toB;
        }
    }
}
