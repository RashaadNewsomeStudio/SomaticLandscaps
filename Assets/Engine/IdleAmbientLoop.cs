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

    private List<int> _cycleIdleA = new List<int>(), _cycleIdleB = new List<int>(), _cycleIdleShared = new List<int>();
    private HashSet<int> _badIdleA = new HashSet<int>(), _badIdleB = new HashSet<int>(), _badIdleShared = new HashSet<int>();
    private int _lastAttemptedIdleA = -1, _lastAttemptedIdleB = -1;
    private int _failureCountIdleA = 0, _failureCountIdleB = 0;
    private Dictionary<int, int> _quarantineIdleA = new Dictionary<int, int>(), _quarantineIdleB = new Dictionary<int, int>(), _quarantineIdleShared = new Dictionary<int, int>();

    private bool _isPreparingA, _isPreparingB;
    private volatile bool _stopIdleWork;

    private CancellationTokenSource _prepA, _prepB, _idleCts;
    private CancellationToken _idleRootToken = CancellationToken.None; // CRITICAL: Root lifetime token (not loop token)
    private Task _prepTaskA = Task.CompletedTask, _prepTaskB = Task.CompletedTask, _idleTask = Task.CompletedTask;
    private string _loadedPathA, _loadedPathB;

    public IdleAmbientLoop(ArtworkController ctrl, AsyncAssetManager assetManager)
    {
        _ctrl = ctrl;
        _assetManager = assetManager;
        _media = new MediaDiscoveryService(ctrl, assetManager); 
    }



    public async Task StartIdleNowAsync(CancellationToken ct)
    {
        await StopIdleLoopAsync(CancellationToken.None);
        
        if (_ctrl.activeTrigger) _ctrl.activeTrigger.SetInteractable(true);
        _ctrl.inIdle = _ctrl.usingA = true;
        _stopIdleWork = false;

        await PrepareIdleForSideAsync(_ctrl.idleA, true, ct);
        await Task.Yield();
        await PrepareIdleForSideAsync(_ctrl.idleB, false, ct);

        SetAlphas(1f, 0f);
        string initialName = !string.IsNullOrEmpty(_loadedPathA) ? Path.GetFileName(_loadedPathA) : "Loading...";
        ControllerMain.LogInfo($"[Idle] LIVE: '{initialName}' (Initial)");
        
        _idleTask = StartIdleLoopOwnedAsync(ct);
        ControllerMain.LogStep("Idle loop started.");
    }

    public Task StartIdleLoopOwnedAsync(CancellationToken globalCt)
    {
        // CRITICAL: Store root token FIRST before cancelling old loop token
        // This allows crash recovery to restart using the root token, not the cancelled loop token
        _idleRootToken = globalCt;
        
        _idleCts?.Cancel(); _idleCts?.Dispose();
        _idleCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
        _idleTask = IdleLoopAsync(_idleCts.Token);
        return _idleTask;
    }

    public async Task StopIdleLoopAsync(CancellationToken ct)
    {
        _stopIdleWork = true;
        try { _idleCts?.Cancel(); } catch { }
        await AwaitTaskSafely(_idleTask, 2000, ct, "IdleLoop");
    }

    public async Task StopIdleLoopOwnedAsync(CancellationToken ct)
    {
        _idleCts?.Cancel();
        await AwaitTaskSafely(_idleTask, 1500, ct, "IdleLoopOwned");
    }

    public async Task StopIdleWorkAsync(CancellationToken ct)
    {
        _stopIdleWork = true;
        await StopIdleLoopAsync(ct);
        _prepA?.Cancel(); _prepB?.Cancel();
        await AwaitTaskSafely(Task.WhenAll(_prepTaskA, _prepTaskB), 1500, ct, "PrepareTasks");
    }

    private async Task AwaitTaskSafely(Task t, int ms, CancellationToken ct, string context)
    {
        if (t == null || t.IsCompleted) return;
        if (await Task.WhenAny(t, Task.Delay(ms, ct)) != t)
            ControllerMain.LogWarn($"{context} did not finish in time.");
    }



    private void PausePlayer(HapPlayer hp)
    {
        if (!hp) return;
        try { hp.speed = 0f; } catch {}
    }

    private void StartPlayerFromZero(HapPlayer hp)
    {
        if (!hp) return;
        try { hp.time = 0f; hp.speed = 1f; } catch {}
    }

    public async Task IdleLoopAsync(CancellationToken ct)
    {
        try
        {
            _stopIdleWork = false;
            if (!HasIdlePaths(true) && !HasIdlePaths(false)) return;

            if (_ctrl._idlePrimedFromReturn)
            {
                _ctrl.usingA = _ctrl._returnStartIsA;
                SetAlphas(_ctrl.usingA ? 1f : 0f, _ctrl.usingA ? 0f : 1f);
                _ctrl._idlePrimedFromReturn = false;
                ControllerMain.LogInfo("[Idle] Using pre-loaded content from return-to-idle");
            }
            else if (!_ctrl.crossfadeIdle)
            {
                await PrepareIdleForSideAsync(_ctrl.idleA, true, ct);
                SetAlphas(1f, 0f);
            }

            while (!ShouldStop() && !ct.IsCancellationRequested)
            {
                var active  = _ctrl.usingA ? _ctrl.idleA : _ctrl.idleB;
                var standby = _ctrl.usingA ? _ctrl.idleB : _ctrl.idleA;
                
                float len = ValidDuration(active);
                float targetFade = _ctrl.crossfadeIdle ? Mathf.Clamp(_ctrl.idleFade, _ctrl.idleMinFade, len * 0.9f) : 0f;
                float tPrepare   = Mathf.Max(0f, len - (targetFade + _ctrl.idlePrepareLead));
                float tFadeStart = Mathf.Max(0f, len - targetFade);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                 ControllerMain.LogInfo(
                  $"[IdleTiming] usingA={_ctrl.usingA} len={len:F2} time={(active!=null?(float)active.time:-1f):F2} " +
                  $"tPrepare={tPrepare:F2} tFadeStart={tFadeStart:F2} fade={targetFade:F2}");
#endif

                float startWait = Time.realtimeSinceStartup;
                float waitMax = len + 1.5f;
                await AsyncExtensions.WaitUntil(() => 
                {
                    float elapsed = Time.realtimeSinceStartup - startWait;
                    return ShouldStop() 
                        || (active != null && active.isValid && active.time >= tPrepare) 
                        || elapsed > waitMax;
                }, ct);
                if (ShouldStop()) break;

                await StartPrepareAsync(standby, !_ctrl.usingA, ct);
                if (!_ctrl.crossfadeIdle)
                {
                    PausePlayer(standby);
                }

                if (ShouldStop()) break;

                if (_ctrl.usingA) { if (_ctrl.idleGB) _ctrl.idleGB.alpha = 0f; } 
                else              { if (_ctrl.idleGA) _ctrl.idleGA.alpha = 0f; }
                await AsyncExtensions.WaitUntil(() => 
                {
                    float elapsed = Time.realtimeSinceStartup - startWait;
                    return ShouldStop() 
                           || (active != null && active.isValid && active.time >= tFadeStart) 
                           || elapsed > waitMax;
                }, ct);
                if (ShouldStop()) break;
                float rem = Mathf.Max(0.01f, len - (active != null ? (float)active.time : 0f));
                float dur = Mathf.Min(targetFade, rem);
                
                if (_ctrl.crossfadeIdle)
                {
                    await _ctrl.FadeTwoAsync(_ctrl.usingA ? _ctrl.idleGA : _ctrl.idleGB, _ctrl.usingA ? _ctrl.idleGB : _ctrl.idleGA, 0f, 1f, dur, ct);
                }
                else
                {
                    PausePlayer(active);
                    StartPlayerFromZero(standby);
                    SetAlphas(_ctrl.usingA ? 0f : 1f, _ctrl.usingA ? 1f : 0f);
                }

                _ctrl.usingA = !_ctrl.usingA;
                if (!_ctrl.crossfadeIdle) SetAlphas(_ctrl.usingA ? 1f : 0f, _ctrl.usingA ? 0f : 1f);
                string nameA = !string.IsNullOrEmpty(_loadedPathA) ? Path.GetFileName(_loadedPathA) : "Loading...";
                string nameB = !string.IsNullOrEmpty(_loadedPathB) ? Path.GetFileName(_loadedPathB) : "Loading...";
                
                string currentName = _ctrl.usingA ? nameA : nameB;
                ControllerMain.LogInfo($"[Idle] LIVE: '{currentName}'");
                ControllerMain.LogStep("Idle crossfade complete.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ControllerMain.LogError($"[Idle] Loop crashed: {ex.Message}\n{ex.StackTrace}");

            // CRITICAL: Use _idleRootToken (not ct) to avoid self-cancellation
            if (!ShouldStop() && !_idleRootToken.IsCancellationRequested)
            {
                // GUARD: Prevent dual loops if crash happens during active transition
                if (_idleTask != null && !_idleTask.IsCompleted)
                {
                    ControllerMain.LogWarn("[Idle] Recovery deferred - previous loop still running");
                    return;
                }
                
                await AsyncExtensions.WaitForSecondsRealtime(0.5f, _idleRootToken);
                _ = StartIdleLoopOwnedAsync(_idleRootToken);
            }
        }
    }

    private bool ShouldStop() => _stopIdleWork || !_ctrl.inIdle || _ctrl.activeRunning;
    private void SetAlphas(float a, float b) { if (_ctrl.idleGA) _ctrl.idleGA.alpha = a; if (_ctrl.idleGB) _ctrl.idleGB.alpha = b; }
    private float ValidDuration(HapPlayer hp) => (hp != null && hp.isValid) ? Mathf.Max(0.2f, (float)hp.streamDuration) : 5f;



    private Task StartPrepareAsync(HapPlayer hp, bool forA, CancellationToken idleCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(idleCt);
        if (forA) { _prepA?.Cancel(); _prepA = cts; return _prepTaskA = PrepareIdleForSideAsync(hp, true, cts.Token); }
        else      { _prepB?.Cancel(); _prepB = cts; return _prepTaskB = PrepareIdleForSideAsync(hp, false, cts.Token); }
    }

    public async Task PrepareIdleForSideAsync(HapPlayer hp, bool forA, CancellationToken ct, bool force = false)
    {
        if (!force && ShouldStop()) return;
        if (forA ? _isPreparingA : _isPreparingB) return;
        if (forA) _isPreparingA = true; else _isPreparingB = true;

        try
        {
            if (hp == null) return;

            int maxAttempts = Mathf.Max(1, _ctrl.hapMaxAttemptsPerPick);
            for (int tries = 0; tries < maxAttempts; tries++)
            {
                if ((!force && ShouldStop()) || ct.IsCancellationRequested) return;
                if (!TryPickOrRetry(forA, maxAttempts, out var pool, out int idx, out var badSet))
                {
                    ControllerMain.LogWarn($"Idle prepare {(forA ? "A" : "B")}: No valid files or selection failed");
                    return;
                }

                string rel = ArtworkController.GetRel(pool[idx]);
                ControllerMain.LogInfo($"[Idle] NEXT: '{Path.GetFileName(rel)}' (Loading...)");

                if (!force && ShouldStop()) return;
                string fullPath = _media.ResolveLoadPath(rel, false);
                bool ok = await _assetManager.LoadAndAssignVideoAsync(
                    fullPath, hp, _ctrl._cfgW, _ctrl._cfgH, forA ? "IdleA" : "IdleB",
                    forA ? _ctrl.idleRT_A : _ctrl.idleRT_B, 
                    forA ? _ctrl.idleImgA : _ctrl.idleImgB, 
                    true, ct, 
                    rt => { if (forA) _ctrl.idleRT_A = rt; else _ctrl.idleRT_B = rt; });

                if (ok)
                {
                    if (forA) _loadedPathA = fullPath; else _loadedPathB = fullPath;
                    ResetFailureState(forA);
                    return;
                }
                HandleFailure(forA, idx, badSet, pool.Length, rel);
                await AsyncExtensions.WaitForSeconds(_ctrl.hapRetryDelay, ct);
            }
            ControllerMain.LogError($"Idle prepare {(forA ? "A" : "B")}: Failed after {maxAttempts} attempts");
        }
        finally
        {
            if (forA) _isPreparingA = false; else _isPreparingB = false;
        }
    }

    private bool TryPickOrRetry(bool forA, int maxAttempts, out ArtworkController.StreamingAssetRef[] pool, out int idx, out HashSet<int> badSet)
    {
        int attemptIdx = forA ? _lastAttemptedIdleA : _lastAttemptedIdleB;
        int failures = forA ? _failureCountIdleA : _failureCountIdleB;
        var quarantine = forA ? _quarantineIdleA : _quarantineIdleB;
        pool = forA ? (_ctrl.idleAList?.Length>0 ? _ctrl.idleAList : _ctrl.idleShared) : (_ctrl.idleBList?.Length>0 ? _ctrl.idleBList : _ctrl.idleShared);
        if (pool == null || pool.Length == 0) { 
            ControllerMain.LogWarn($"Idle Pool {(forA?"A":"B")} is empty/null!");
            idx = -1; badSet = null; return false; 
        }
        
        var cycle = (pool == _ctrl.idleShared) ? _cycleIdleShared : (forA ? _cycleIdleA : _cycleIdleB);
        badSet = (pool == _ctrl.idleShared) ? _badIdleShared : (forA ? _badIdleA : _badIdleB);
        if (attemptIdx < 0 || failures >= maxAttempts)
        {
             int newLast;
             idx = CyclePicker.NextFromCycleFiltered(cycle, pool.Length, forA ? _ctrl.lastIdleIndexA : _ctrl.lastIdleIndexB, badSet, out newLast);
             if (pool == _ctrl.idleShared) { _cycleIdleShared = cycle; if (forA) _ctrl.lastIdleIndexA = newLast; else _ctrl.lastIdleIndexB = newLast; }
             else if (forA) { _cycleIdleA = cycle; _ctrl.lastIdleIndexA = newLast; }
             else           { _cycleIdleB = cycle; _ctrl.lastIdleIndexB = newLast; }

             if (idx < 0) return false;
             if (quarantine.ContainsKey(idx)) {
                 badSet.Add(idx); 
                 return false;
             }
             if (forA) { _lastAttemptedIdleA = idx; _failureCountIdleA = 0; }
             else      { _lastAttemptedIdleB = idx; _failureCountIdleB = 0; }
             
             ControllerMain.LogInfo($"[IdlePick] Side={(forA?"A":"B")} PoolSize={pool.Length} PickedIdx={idx} PrevIdx={(forA?_ctrl.lastIdleIndexA:_ctrl.lastIdleIndexB)}");
        }
        else
        {
             ControllerMain.LogInfo($"[IdlePick] Side={(forA?"A":"B")} RETRYING Idx={idx} Failures={failures}");
        }
        return true;
    }

    private void HandleFailure(bool forA, int idx, HashSet<int> badSet, int poolSize, string relName)
    {
        int fails = (forA ? ++_failureCountIdleA : ++_failureCountIdleB);
        
        if (fails >= _ctrl.hapQuarantineAfterFailures)
        {
            var q = forA ? _quarantineIdleA : _quarantineIdleB;
            q[idx] = fails;
            badSet.Add(idx);
            ControllerMain.LogWarn($"Quarantined '{Path.GetFileName(relName)}' after {fails} failures");
            if (forA) { _lastAttemptedIdleA = -1; _failureCountIdleA = 0; }
            else      { _lastAttemptedIdleB = -1; _failureCountIdleB = 0; }
        }
    }

    private void ResetFailureState(bool forA)
    {
        if (forA) { _lastAttemptedIdleA = -1; _failureCountIdleA = 0; }
        else      { _lastAttemptedIdleB = -1; _failureCountIdleB = 0; }
    }

    private bool HasIdlePaths(bool forA)
    {
        var list = forA ? _ctrl.idleAList : _ctrl.idleBList;
        return ArtworkController.HasAnyValid(list) || ArtworkController.HasAnyValid(_ctrl.idleShared);
    }
}
