using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Klak.Hap;
using SomaticLandscapes.Async;

public class IdleAmbientLoop
{
    private readonly ArtworkController _ctrl;
    private readonly AsyncAssetManager _assetManager;
    private readonly MediaDiscoveryService _media;

    // State specific to idle loop logic
    private List<int>         _cycleIdleA = new List<int>(), _cycleIdleB = new List<int>(), _cycleIdleShared = new List<int>();
    private HashSet<int>      _badIdleA = new HashSet<int>(), _badIdleB = new HashSet<int>(), _badIdleShared = new HashSet<int>();
    
    private int _lastAttemptedIdleA = -1, _lastAttemptedIdleB = -1;
    private int _failureCountIdleA = 0, _failureCountIdleB = 0;
    private Dictionary<int, int> _quarantineIdleA = new Dictionary<int, int>(), _quarantineIdleB = new Dictionary<int, int>(), _quarantineIdleShared = new Dictionary<int, int>();

    private bool _isPreparingA;
    private bool _isPreparingB;

    public IdleAmbientLoop(ArtworkController ctrl, AsyncAssetManager assetManager)
    {
        _ctrl = ctrl;
        _assetManager = assetManager;
        _media = new MediaDiscoveryService(ctrl, assetManager); 
    }

    // FIX: Track background tasks to prevent overlap with Active trigger
    private CancellationTokenSource _prepA;
    private CancellationTokenSource _prepB;
    private Task _prepTaskA = Task.CompletedTask;
    private Task _prepTaskB = Task.CompletedTask;
    private volatile bool _stopIdleWork;

    private Task StartPrepareAsync(HapPlayer hp, bool forA, CancellationToken idleCt)
    {
        // creates a linked token to allow specific cancellation of this prepare
        var cts = CancellationTokenSource.CreateLinkedTokenSource(idleCt);
        if (forA)
        {
            _prepA?.Cancel(); _prepA?.Dispose();
            _prepA = cts;
            _prepTaskA = PrepareIdleForSideAsync(hp, forA, cts.Token);
            return _prepTaskA;
        }
        else
        {
            _prepB?.Cancel(); _prepB?.Dispose();
            _prepB = cts;
            _prepTaskB = PrepareIdleForSideAsync(hp, forA, cts.Token);
            return _prepTaskB;
        }
    }

    // FIX: Owned idle loop task for clean shutdown
    private CancellationTokenSource _idleCts;
    private Task _idleTask = Task.CompletedTask;

    public async Task StopIdleLoopAsync(CancellationToken ct)
    {
        _stopIdleWork = true;
        
        try { _idleCts?.Cancel(); } catch { }
        
        var task = _idleTask;
        if (task == null || task.IsCompleted) return;

        ControllerMain.LogStep("Waiting for IdleLoopAsync to exit...");
        var done = await Task.WhenAny(task, Task.Delay(2000, ct));
        if (done != task)
            ControllerMain.LogWarn("StopIdleLoopAsync: idle loop did not finish in time.");
        else
            ControllerMain.LogStep("IdleLoopAsync exited cleanly.");
    }

    public Task StartIdleLoopOwnedAsync(CancellationToken globalCt)
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();

        _idleCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
        _idleTask = IdleLoopAsync(_idleCts.Token);
        return _idleTask;
    }

    public async Task StopIdleLoopOwnedAsync(CancellationToken ct)
    {
        ControllerMain.LogStep("StopIdleLoopOwnedAsync: Cancelling idle loop...");
        _idleCts?.Cancel();
        var done = await Task.WhenAny(_idleTask, Task.Delay(1500, ct));
        if (done != _idleTask)
            ControllerMain.LogWarn("StopIdleLoopOwnedAsync: idle loop did not exit within timeout.");
        else
            ControllerMain.LogStep("StopIdleLoopOwnedAsync: Idle loop stopped.");
    }

    public async Task StopIdleWorkAsync(CancellationToken ct)
    {
        _stopIdleWork = true;

        // Stop idle loop first so it can't spawn new prepares
        await StopIdleLoopAsync(ct);

        _prepA?.Cancel();
        _prepB?.Cancel();

        // Await ongoing prepares to remove overlap.
        // Use timeout to avoid hanging if a task gets stuck
        var all = Task.WhenAll(_prepTaskA, _prepTaskB);
        var done = await Task.WhenAny(all, Task.Delay(1500, ct));
        if (done != all)
        {
            ControllerMain.LogWarn("StopIdleWorkAsync: prepare tasks did not finish within timeout.");
        }
    }

    public async Task StartIdleNowAsync(CancellationToken ct)
    {
        // Stop any existing loop first
        await StopIdleLoopAsync(CancellationToken.None);

        if (_ctrl.activeButton) _ctrl.activeButton.interactable = true;

        _ctrl.inIdle = true;
        _ctrl.usingA = true;
        _stopIdleWork = false;

        // CRITICAL FIX: Load videos SEQUENTIALLY to prevent VRAM exhaustion
        
        // Load idleA first
        await PrepareIdleForSideAsync(_ctrl.idleA, true, ct);
        
        // Yield to keep Windows responsive
        await Task.Yield();
        
        // THEN load idleB (standby video) - sequential
        await PrepareIdleForSideAsync(_ctrl.idleB, false, ct);

        if (_ctrl.idleGA) _ctrl.idleGA.alpha = 1f;
        if (_ctrl.idleGB) _ctrl.idleGB.alpha = 0f;

        // OWNED loop token (Fix A)
        _idleTask = StartIdleLoopOwnedAsync(ct);

        ControllerMain.LogStep("Idle loop started (owned task).");
    }

    public async Task IdleLoopAsync(CancellationToken ct)
    {
        if (!HasIdlePaths(true) && !HasIdlePaths(false))
        { Debug.LogWarning("[ArtworkController] No idle files assigned."); return; }

        if (_ctrl._idlePrimedFromReturn)
        {
            _ctrl.usingA = _ctrl._returnStartIsA;
            if (_ctrl.usingA) { if (_ctrl.idleGA) _ctrl.idleGA.alpha = 1f; if (_ctrl.idleGB) _ctrl.idleGB.alpha = 0f; }
            else              { if (_ctrl.idleGA) _ctrl.idleGA.alpha = 0f; if (_ctrl.idleGB) _ctrl.idleGB.alpha = 1f; }
            _ctrl._idlePrimedFromReturn = false;
        }

        _ctrl.inIdle = true;

        if (!_ctrl.crossfadeIdle)
        {
            await PrepareIdleForSideAsync(_ctrl.idleA, true, ct);
            if (_ctrl.idleGA) _ctrl.idleGA.alpha = 1f;
            if (_ctrl.idleGB) _ctrl.idleGB.alpha = 0f;
            return;
        }

        while (_ctrl.inIdle && !ct.IsCancellationRequested)
        {
            // STOP CHECK 1
            if (_stopIdleWork) break;

            var active = _ctrl.usingA ? _ctrl.idleA : _ctrl.idleB;
            var standby = _ctrl.usingA ? _ctrl.idleB : _ctrl.idleA;
            var gAct = _ctrl.usingA ? _ctrl.idleGA : _ctrl.idleGB;
            var gStd = _ctrl.usingA ? _ctrl.idleGB : _ctrl.idleGA;

            float len = ValidDuration(active);
            float targetFade = Mathf.Clamp(_ctrl.idleFade, _ctrl.idleMinFade, len * 0.9f);
            float tPrepare   = Mathf.Max(0f, len - (targetFade + _ctrl.idlePrepareLead));
            float tFadeStart = Mathf.Max(0f, len - targetFade);

            // Wait until prepare time (Manual Polling for Stability)
            ControllerMain.LogStep($"Idle loop: Waiting for prepare (t={tPrepare:F2})...");
            while (true)
            {
                if (ct.IsCancellationRequested) break;
                if (!_ctrl.inIdle || _ctrl.activeRunning) break;
                
                // Safe property access
                if (active == null) break;
                if (active.time >= tPrepare) break;

                await Task.Delay(33, ct); // Poll ~30fps
            }
            if (ct.IsCancellationRequested) break;

            // STOP CHECK PRE-PREPARE
            ControllerMain.LogStep($"Idle loop: Wait finished. inIdle={_ctrl.inIdle}, activeRunning={_ctrl.activeRunning}");
            if (_stopIdleWork || _ctrl.activeRunning || !_ctrl.inIdle) break;

            // Prepare next video (AWAITED - Serialized)
            // Ensures standby is ready before we even think about fading
            await StartPrepareAsync(standby, !_ctrl.usingA, ct);

            // STOP CHECK POST-PREPARE
            if (_stopIdleWork || _ctrl.activeRunning || !_ctrl.inIdle) break;

            if (gStd) gStd.alpha = 0f;

            // Wait until fade start
            // Wait until fade start
            // Wait until fade start
            try 
            {
                while (true)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!_ctrl.inIdle || _ctrl.activeRunning) break;
                    
                    if (active == null) break;
                    if (active.time >= tFadeStart) break;

                    await Task.Delay(33, ct);
                }
            }
            catch (OperationCanceledException) { break; }

            // STOP CHECK 2
            if (_stopIdleWork) break;

            float remaining = Mathf.Max(0.01f, len - active.time);
            float fadeDur = Mathf.Min(targetFade, remaining);

            float t = 0f, a0 = gAct ? gAct.alpha : 0f, b0 = gStd ? gStd.alpha : 0f;
            while (t < fadeDur)
            {
                // STOP INSIDE FADE
                if (_stopIdleWork || _ctrl.activeRunning || !_ctrl.inIdle) break;

                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / fadeDur);
                if (gAct) gAct.alpha = Mathf.Lerp(a0, 0f, k);
                if (gStd) gStd.alpha = Mathf.Lerp(b0, 1f, k);
                await Task.Yield();
            }

            if (gAct) gAct.alpha = 0f;
            if (gStd) gStd.alpha = 1f;

            _ctrl.usingA = !_ctrl.usingA;
            if (!_stopIdleWork) ControllerMain.LogStep("Idle crossfade step.");
        }
    }

    public async Task PrepareIdleForSideAsync(HapPlayer hp, bool forA, CancellationToken ct, bool force = false)
    {
        // FIX 4: Early exit if active mode is running 
        if (!force && (!_ctrl.inIdle || _ctrl.activeRunning))
        {
            ControllerMain.LogStep(forA ? "Idle prepare A skipped (not idle)" : "Idle prepare B skipped (not idle)");
            return;
        }

        if (forA) { if (_isPreparingA) return; _isPreparingA = true; }
        else      { if (_isPreparingB) return; _isPreparingB = true; }

        try
        {
            if (hp == null) return;

            // FIX 1: Get or reuse last attempted index
            int attemptIdx = forA ? _lastAttemptedIdleA : _lastAttemptedIdleB;
            int failureCount = forA ? _failureCountIdleA : _failureCountIdleB;
            var quarantine = forA ? _quarantineIdleA : _quarantineIdleB;
            ArtworkController.StreamingAssetRef[] pool;
            int idx;
            HashSet<int> badSet;

            // FIX 1: Retry same file with delay if previous attempt failed
            int maxAttempts = Mathf.Max(1, _ctrl.hapMaxAttemptsPerPick);
            for (int tries = 0; tries < maxAttempts && !ct.IsCancellationRequested; tries++)
            {
                // CRITICAL: Check if active mode started (abort immediately if so)
                if (!force && (_ctrl.activeRunning || !_ctrl.inIdle))
                {
                    ControllerMain.LogStep(forA ? "Idle prepare A aborted (active started)" : "Idle prepare B aborted (active started)");
                    return;
                }

                // Pick new file only if no previous attempt or previous succeeded
                if (attemptIdx < 0 || failureCount >= maxAttempts)
                {
                    var picked = PickIdlePath(forA, out pool, out idx, out badSet);
                    if (!picked || idx < 0 || idx >= pool.Length)
                    {
                            ControllerMain.LogWarn(forA ? "Idle prepare A: No valid files" : "Idle prepare B: No valid files");
                            return;
                    }
                    if (quarantine.ContainsKey(idx))
                    {
                        ControllerMain.LogWarn($"Idle prepare {(forA ? "A" : "B")}: File idx={idx} is quarantined");
                        badSet.Add(idx);
                        continue;
                    }
                    attemptIdx = idx;
                    failureCount = 0;
                }
                else
                {
                    PickIdlePath(forA, out pool, out _, out badSet);
                    idx = attemptIdx;
                }

                if (idx < 0 || idx >= pool.Length) { return; }

                string rel = ArtworkController.GetRel(pool[idx]);
                if (string.IsNullOrEmpty(rel)) { return; }

                ControllerMain.LogStep($"Idle prepare {(forA ? "A" : "B")}: '{Path.GetFileName(rel)}' (attempt {tries + 1}/{maxAttempts}, failures so far={failureCount})");

                // CRITICAL: Final check before touching HAP player
                if (!force && (_ctrl.activeRunning || !_ctrl.inIdle))
                {
                    ControllerMain.LogStep(forA ? "Idle prepare A aborted before load (active started)" : "Idle prepare B aborted before load (active started)");
                    return;
                }

                // --- ASYNC LOAD ---
                string fullPath = _media.ResolveLoadPath(rel, false);
                
                // UNIFIED CALL: Use shared loader to handle RT assignment safely
                bool ok = await _assetManager.LoadAndAssignVideoAsync(
                    fullPath, 
                    hp, 
                    _ctrl._cfgW, 
                    _ctrl._cfgH, 
                    forA ? "IdleA" : "IdleB",
                    forA ? _ctrl.idleRT_A : _ctrl.idleRT_B, // Priority: Manual RT
                    forA ? _ctrl.idleImgA : _ctrl.idleImgB, // Target UI
                    true, // loop
                    ct,
                    (rt) => {
                        // Callback to update controller state
                        if (forA) _ctrl.idleRT_A = rt;
                        else      _ctrl.idleRT_B = rt;
                    }
                );
                
                // CRITICAL: Check again after async load completes
                if (!force && (_ctrl.activeRunning || !_ctrl.inIdle || _stopIdleWork))
                {
                    ControllerMain.LogStep(forA ? "Idle prepare A aborted after load (active started)" : "Idle prepare B aborted after load (active started)");
                    return;
                }

                if (ok)
                {
                    if (forA) { _lastAttemptedIdleA = -1; _failureCountIdleA = 0; }
                    else { _lastAttemptedIdleB = -1; _failureCountIdleB = 0; }
                    return;
                }

                // Failed
                failureCount++;
                if (forA) { _lastAttemptedIdleA = attemptIdx; _failureCountIdleA = failureCount; }
                else { _lastAttemptedIdleB = attemptIdx; _failureCountIdleB = failureCount; }

                // FIX 1: Quarantine
                if (failureCount >= _ctrl.hapQuarantineAfterFailures)
                {
                    quarantine[idx] = failureCount;
                    badSet.Add(idx);
                    ControllerMain.LogWarn($"Idle prepare {(forA ? "A" : "B")}: QUARANTINED '{Path.GetFileName(rel)}' after {failureCount} failures");
                    attemptIdx = -1; 
                    failureCount = 0;
                    if (forA) { _lastAttemptedIdleA = -1; _failureCountIdleA = 0; }
                    else { _lastAttemptedIdleB = -1; _failureCountIdleB = 0; }
                    continue; 
                }

                // FIX 1: Delay
                ControllerMain.LogWarn($"Idle prepare {(forA ? "A" : "B")}: Failed, retrying same file after {_ctrl.hapRetryDelay:0.00}s delay");
                await AsyncExtensions.WaitForSeconds(_ctrl.hapRetryDelay, ct);
            }

            ControllerMain.LogError($"Idle prepare {(forA ? "A" : "B")}: FAILED after {maxAttempts} attempts");
        }
        finally
        {
            if (forA) _isPreparingA = false; else _isPreparingB = false;
        }
    }

    private bool HasIdlePaths(bool forA)
    {
        var list = forA ? _ctrl.idleAList : _ctrl.idleBList;
        if (ArtworkController.HasAnyValid(list)) return true;
        return ArtworkController.HasAnyValid(_ctrl.idleShared);
    }

    private bool PickIdlePath(bool forA, out ArtworkController.StreamingAssetRef[] pool, out int idx, out HashSet<int> badSet)
    {
        pool = forA
            ? (ArtworkController.HasAnyValid(_ctrl.idleAList) ? _ctrl.idleAList : _ctrl.idleShared)
            : (ArtworkController.HasAnyValid(_ctrl.idleBList) ? _ctrl.idleBList : _ctrl.idleShared);

        if (pool == null || pool.Length == 0) { idx = -1; badSet = null; return false; }

        List<int> cycle = (pool == _ctrl.idleShared) ? _cycleIdleShared : (forA ? _cycleIdleA : _cycleIdleB);
        badSet = (pool == _ctrl.idleShared) ? _badIdleShared : (forA ? _badIdleA : _badIdleB);

        if (badSet.Count >= pool.Length) badSet.Clear();

        int lastUsed = forA ? _ctrl.lastIdleIndexA : _ctrl.lastIdleIndexB;

        int newLast;
        idx = CyclePicker.NextFromCycleFiltered(cycle, pool.Length, lastUsed, badSet, out newLast);

        if (pool == _ctrl.idleShared) _cycleIdleShared = cycle;
        else if (forA) { _cycleIdleA = cycle; _ctrl.lastIdleIndexA = newLast; }
        else           { _cycleIdleB = cycle; _ctrl.lastIdleIndexB = newLast; }

        return idx >= 0;
    }

    private float ValidDuration(HapPlayer hp)
    {
        if (hp == null || !hp.isValid) return 5f;
        var d = (float)hp.streamDuration;
        return Mathf.Max(0.2f, d);
    }
}
