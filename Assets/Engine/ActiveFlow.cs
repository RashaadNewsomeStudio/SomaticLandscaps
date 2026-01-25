using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Klak.Hap;
using SomaticLandscapes.Async;

public class ActiveFlow
{
    private readonly ArtworkController _ctrl;
    private readonly AsyncAssetManager _assetManager;
    private readonly IdleAmbientLoop _idle;
    private readonly MusicFlow _music;
    private readonly Func<CancellationTokenSource> _getActiveCts;
    private readonly MediaDiscoveryService _media;

    private List<int> _cycleActive = new List<int>();
    private HashSet<int> _badActive = new HashSet<int>();
    private int _lastActiveIndex = -1;

    // CONCURRENCY FIX: Prevent multiple ActiveSequenceAsync from running simultaneously
    // This fixes the 3rd-trigger crash where multiple tasks access the same HapPlayer
    private readonly SemaphoreSlim _activeSequenceLock = new SemaphoreSlim(1, 1);
    private Task _currentActiveTask = Task.CompletedTask;
    
    // PRODUCTION FIX: Session token to invalidate stale continuations
    // Prevents late tasks from accessing closed/reopened players
    private int _activeSessionId = 0;

    public ActiveFlow(ArtworkController ctrl, AsyncAssetManager assetManager, IdleAmbientLoop idle, MusicFlow music, Func<CancellationTokenSource> getActiveCts)
    {
        _ctrl = ctrl;
        _assetManager = assetManager;
        _idle = idle;
        _music = music;
        _getActiveCts = getActiveCts;
        _media = new MediaDiscoveryService(ctrl, assetManager); 
    }

    public void ReceiveActive(int value)
    {
        if (value == 1)
        {
            if (_ctrl.inIdle && !_ctrl.activeRunning)
            {
                ControllerMain.LogStep("ReceiveActive(1) => ACTIVE (from idle)");
                _ctrl.SetActive(true);
            }
            else
            {
                ControllerMain.LogStep("ReceiveActive(1) ignored — already active or transitioning.");
            }
        }
        else if (value == 0)
        {
            if (_ctrl.activeRunning)
            {
                if (_ctrl.ignoreAmbientWhileActive)
                {
                    ControllerMain.LogStep("ReceiveActive(0) ignored — IgnoreAmbientWhileActive=true (non-interruptible Active).");
                    return;
                }

                ControllerMain.LogStep("ReceiveActive(0) => AMBIENT (return)");
                _ctrl.SetActive(false);
            }
            else
            {
                ControllerMain.LogStep("ReceiveActive(0) ignored — already ambient.");
            }
        }
        else
        {
            ControllerMain.LogStep($"ReceiveActive({value}) ignored (unsupported value).");
        }
    }

    public void TriggerActive(string sourceTag, ref CancellationTokenSource activeCts, CancellationToken globalCt)
    {
        _ctrl._returnStartWithA = _ctrl.usingA;
        
        // STABILITY REVERT: Do not cancel Idle Loop aggressively. 
        // Interupting HAP loading triggers Native Access Violations.
        // usage of _ctrl.inIdle = false (later) will stop the loop gracefully.
        
        // CONCURRENCY FIX: Properly manage CTS lifecycle
        if (activeCts != null) 
        { 
            activeCts.Cancel();
            activeCts.Dispose();
        }
        
        // PRODUCTION FIX: Increment session ID to invalidate any stale continuations
        // Any late tasks from previous sessions will detect mismatch and abort
        _activeSessionId++;
        int currentSession = _activeSessionId;
        
        // Create new CTS for this trigger
        activeCts = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(globalCt, activeCts.Token);
        
        // Start new sequence with proper serialization to prevent 3rd-trigger crash
        // This ensures only ONE ActiveSequenceAsync runs at a time
        _currentActiveTask = StartActiveSequenceSerializedAsync(sourceTag, currentSession, linked.Token);
        ControllerMain.LogStep($"Active sequence triggered (source={sourceTag}, session={currentSession}).");
    }

    /// <summary>
    /// Serializes active sequence execution to prevent concurrent access to shared HapPlayer.
    /// Fixes the 3rd-trigger crash caused by fire-and-forget async pattern.
    /// Pattern: Concurrency in C# Cookbook - Chapter 12: Synchronization
    /// PRODUCTION: Session token prevents stale continuations from accessing closed players
    /// </summary>
    private async Task StartActiveSequenceSerializedAsync(string sourceTag, int sessionId, CancellationToken ct)
    {
        // Wait for previous active sequence to complete cleanup
        // This is the KEY fix: prevents multiple tasks from accessing activeHP simultaneously
        await _activeSequenceLock.WaitAsync(ct);
        
        try
        {
            // PRODUCTION: Verify session is still valid (not superseded by newer trigger)
            if (sessionId != _activeSessionId)
            {
                ControllerMain.LogStep($"Active sequence aborted (stale session {sessionId}, current is {_activeSessionId}).");
                return;
            }
            
            // Trigger cleanup of idle background tasks (waits for them to finish)
            // This prevents the "3rd trigger crash" by removing overlap between Idle Prepare and Active Open
            await _idle.StopIdleWorkAsync(ct);
            // FIX A: Stop idle loop owner (Critical for crossfade safety)
            await _idle.StopIdleLoopOwnedAsync(ct);

            // Run the actual sequence (will release lock when done)
            await ActiveSequenceAsync(sourceTag, sessionId, ct);
        }
        catch (OperationCanceledException)
        {
            ControllerMain.LogStep($"Active sequence cancelled (session={sessionId}).");
        }
        catch (Exception ex)
        {
            ControllerMain.LogError($"Active sequence error (session={sessionId}): {ex.Message}");
        }
        finally
        {
            // CRITICAL: Always release the lock to allow next trigger
            // Without this, 2nd trigger would deadlock forever
            _activeSequenceLock.Release();
        }
    }

    public void RequestReturnToIdle(ref CancellationTokenSource activeCts, CancellationToken globalCt)
    {
        if (!_ctrl.activeRunning)
        {
            ControllerMain.LogStep("SetActive(false) ignored — already ambient.");
            return;
        }
        ControllerMain.LogStep("SetActive(false) → returning to Ambient.");
        StartForceReturnToIdle(ref activeCts, globalCt);
    }

    private void StartForceReturnToIdle(ref CancellationTokenSource activeCts, CancellationToken globalCt)
    {
        // Cancel running active sequence
        if (activeCts != null) { activeCts.Cancel(); activeCts.Dispose(); activeCts = null; }
        
        _ = ForceReturnToIdleAsync(globalCt);
    }

    private async Task ActiveSequenceAsync(string sourceTag, int sessionId, CancellationToken ct)
    {
        _ctrl.activeRunning = true;
        _ctrl.inIdle = false;

        // Fade out idle
        float idleOut = Mathf.Max(0.01f, _ctrl.idleToActiveFadeOut);
        await _ctrl.FadeTwoAsync(_ctrl.idleGA, _ctrl.idleGB, 0f, 0f, idleOut, ct);

        if (_ctrl.activeGroup) _ctrl.activeGroup.alpha = 0f;

        // FIX B: RESOURCE CAP (Max 2 Players)
        // Crash analysis shows 3 players (IdleA + IdleB + Active) causes native instability.
        // We MUST close the standby idle player before opening Active.
        HapPlayer standbyHp = _ctrl.usingA ? _ctrl.idleB : _ctrl.idleA;
        RawImage standbyImg = _ctrl.usingA ? _ctrl.idleImgB : _ctrl.idleImgA;
        
        if (standbyHp != null)
        {
            ControllerMain.LogStep($"Active Trigger: Releasing standby idle player ({standbyHp.name}) to free resources.");
            
            // 1. Detach UI
            await AsyncExtensions.RunOnMainThread(() =>
            {
                if (standbyImg) standbyImg.texture = null;
                standbyHp.targetTexture = null;
            }, ct);
            
            // 2. GPU Barrier
            await AsyncExtensions.WaitForEndOfFrame(ct);
            await AsyncExtensions.WaitFrames(1, ct);
            
            // 3. Close & Release
            await _assetManager.ReleaseVideoAsync(standbyHp, ct);
        }

        if (_ctrl.activeGroup) _ctrl.activeGroup.alpha = 0f;

        // PRODUCTION: Verify session before player access
        if (sessionId != _activeSessionId)
        {
            ControllerMain.LogStep($"Active sequence aborted before load (stale session {sessionId}).");
            return;
        }

        // Load active video async
        bool opened = await OpenValidActiveAsync(_ctrl.activeHP, ct);

        if (!opened)
        {
            await _ctrl.FadeTwoAsync(_ctrl.idleGA, _ctrl.idleGB, 1f, 0f, _ctrl.returnFade, ct);
            _ctrl.activeRunning = false;
            _ctrl.inIdle = true;
            _ = _idle.IdleLoopAsync(ct);
            ControllerMain.LogStep("Active open failed; returning to idle.");
            return;
        }

        _ctrl.StartCoroutine(_music.Co_StartMusicAfterDelay(_ctrl.musicRampIn));

        float fin = Mathf.Max(0.05f, _ctrl.activeFadeIn);
        ControllerMain.LogStep($"Active fade-in start (dur={fin:0.00}s, session={sessionId})");
        await _ctrl.FadeOneAsync(_ctrl.activeGroup, 0f, 1f, fin, ct);

        // PRODUCTION: Check session after await
        if (sessionId != _activeSessionId) return;

        float dur  = Mathf.Max(0.2f, (float)_ctrl.activeHP.streamDuration);
        float fout = Mathf.Max(0.05f, _ctrl.activeFadeOut);

        float fadeOutStart = _ctrl.activeUseFixedWindow
            ? Mathf.Min(fin + Mathf.Max(0f, _ctrl.activeFixedMidHoldSeconds), Mathf.Max(0f, dur - fout))
            : Mathf.Max(0f, Mathf.Max(dur - fout, fin <= dur ? fin : dur * 0.5f));

        ControllerMain.LogStep($"Active fade-out start scheduled at t={fadeOutStart:0.00}s (dur={fout:0.00}s, total={dur:0.00}s)");

        try
        {
            await AsyncExtensions.WaitUntil(() => _ctrl.activeHP != null && _ctrl.activeHP.time >= fadeOutStart, ct);
        }
        catch (OperationCanceledException) {}

        // PRODUCTION: Check session after long wait
        if (sessionId != _activeSessionId)
        {
            ControllerMain.LogStep($"Active sequence aborted after playback (stale session {sessionId}).");
            return;
        }

        _ctrl.StartCoroutine(_music.RampDownToZero(Mathf.Max(0.01f, _ctrl.musicRampOut)));

        await _ctrl.FadeOneAsync(_ctrl.activeGroup, 1f, 0f, fout, ct);

        if (!_ctrl.activeUseFixedWindow && _ctrl.activeEndHoldSeconds > 0f)
            await AsyncExtensions.WaitForSeconds(_ctrl.activeEndHoldSeconds, ct);

        _ctrl.usingA = _ctrl._returnStartWithA;

        var gStart = _ctrl.usingA ? _ctrl.idleGA : _ctrl.idleGB;

        if (_ctrl.randomizeIdleStartOnReturn)
        {
            // Memory management note: Unity 6 has incremental GC enabled by default.
            // Manual GC.Collect() causes 100-500ms frame hitches and is counterproductive.
            // Removed aggressive GC calls - let Unity's automatic GC handle cleanup.
            
            var hp = _ctrl.usingA ? _ctrl.idleA : _ctrl.idleB;
            // Load required idle side async 
            await _idle.PrepareIdleForSideAsync(hp, _ctrl.usingA, ct, force: true);
        }

        // PRODUCTION: Final session check before player Close
        if (sessionId != _activeSessionId)
        {
            ControllerMain.LogStep($"Active sequence aborted before cleanup (stale session {sessionId}).");
            return;
        }

        // NATIVE CRASH FIX: Use manager's safe release which includes GPU barriers
        // We pass a callback to detach the RawImage (consumer) inside the safety zone
        if (_ctrl.activeHP != null)
        {
            try 
            { 
                await _assetManager.ReleaseVideoAsync(_ctrl.activeHP, ct, () => 
                {
                    if (_ctrl.activeImg != null) _ctrl.activeImg.texture = null;
                });
                
                ControllerMain.LogStep($"Active player released safely (GPU-synchronized, session={sessionId}).");
            }
            catch (Exception ex) 
            { 
                ControllerMain.LogWarn($"Error releasing active player: {ex.Message}"); 
            }
        }

        if (_ctrl.returnBlackHold > 0f) await AsyncExtensions.WaitForSecondsRealtime(_ctrl.returnBlackHold, ct);
        if (gStart) await _ctrl.FadeOneAsync(gStart, 0f, 1f, Mathf.Max(0.05f, _ctrl.returnFade), ct);

        _ctrl._returnStartIsA = _ctrl.usingA;
        _ctrl._idlePrimedFromReturn = true;

        _ctrl.activeRunning = false;
        _ctrl.inIdle = true;
        _ = _idle.IdleLoopAsync(ct);

        ControllerMain.LogStep($"Returned to idle (session={sessionId}).");
    }

    private async Task ForceReturnToIdleAsync(CancellationToken ct)
    {
        _ctrl.StartCoroutine(_music.RampDownToZero(Mathf.Max(0.01f, _ctrl.musicRampOut)));

        float fout = Mathf.Max(0.05f, _ctrl.activeFadeOut);
        float currentAlpha = _ctrl.activeGroup ? _ctrl.activeGroup.alpha : 0f;

        if (_ctrl.activeGroup) await _ctrl.FadeOneAsync(_ctrl.activeGroup, currentAlpha, 0f, fout, ct);

        _ctrl.usingA = _ctrl._returnStartWithA;

        var gStart = _ctrl.usingA ? _ctrl.idleGA : _ctrl.idleGB;

        // NATIVE CRASH FIX: Safe release with consumer detach
        if (_ctrl.activeHP != null)
        {
            try 
            { 
                await _assetManager.ReleaseVideoAsync(_ctrl.activeHP, ct, () => 
                {
                    if (_ctrl.activeImg != null) _ctrl.activeImg.texture = null;
                });
                
                ControllerMain.LogStep("Active player released safely (forced return, GPU-synchronized).");
            }
            catch (Exception ex) 
            { 
                ControllerMain.LogWarn($"Error releasing active player: {ex.Message}"); 
            }
        }

        if (_ctrl.returnBlackHold > 0f) await AsyncExtensions.WaitForSecondsRealtime(_ctrl.returnBlackHold, ct);
        if (gStart) await _ctrl.FadeOneAsync(gStart, 0f, 1f, Mathf.Max(0.05f, _ctrl.returnFade), ct);

        _ctrl._returnStartIsA = _ctrl.usingA;
        _ctrl._idlePrimedFromReturn = true;

        _ctrl.activeRunning = false;
        _ctrl.inIdle = true;
        _ = _idle.IdleLoopAsync(ct);

        ControllerMain.LogStep("External cancel: returned to idle.");
    }

    private async Task<bool> OpenValidActiveAsync(HapPlayer hp, CancellationToken ct)
    {
        if (hp == null || _ctrl.activeList == null || _ctrl.activeList.Length == 0) return false;

        int tries = 0;
        while (tries++ < Mathf.Max(1, _ctrl.hapMaxAttemptsPerPick) && !ct.IsCancellationRequested)
        {
            int newLast;
            int idx = CyclePicker.NextFromCycleFiltered(_cycleActive, _ctrl.activeList.Length, _lastActiveIndex, _badActive, out newLast);
            _lastActiveIndex = newLast;

            if (idx >= 0 && idx < _ctrl.activeList.Length)
            {
                string rel = ArtworkController.GetRel(_ctrl.activeList[idx]);
                if (!string.IsNullOrEmpty(rel))
                {
                    string file = Path.GetFileName(rel);
                    ControllerMain.LogStep($"Active pick attempt: '{file}'");

                    string fullPath = _media.ResolveLoadPath(rel, true);

                    // UNIFIED CALL: Use shared loader
                    bool ok = await _assetManager.LoadAndAssignVideoAsync(
                        fullPath, 
                        hp, 
                        _ctrl._cfgW, 
                        _ctrl._cfgH, 
                        "Active",
                        _ctrl.activeRT,    // Priority: Manual RT
                        _ctrl.activeImg,   // Target UI
                        false,             // loop (Active is oneshot)
                        ct,
                        (rt) => _ctrl.activeRT = rt
                    );
                    
                    if (ok) return true;

                    ControllerMain.LogWarn($"Active open failed: {file}");
                    _badActive.Add(idx);
                }
            }
            
            await AsyncExtensions.WaitForSeconds(0.2f, ct);
        }

        ControllerMain.LogError("Active open GAVE UP after multiple tries.");
        return false;
    }

    private bool HasActivePaths() => ArtworkController.HasAnyValid(_ctrl.activeList);

    /// <summary>
    /// Cleanup method to prevent resource leaks. Call this when ActiveFlow is no longer needed.
    /// </summary>
    public void Cleanup()
    {
        _activeSequenceLock?.Dispose();
    }
}
