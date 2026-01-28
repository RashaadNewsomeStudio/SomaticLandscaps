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

    // Concurrency control
    private readonly SemaphoreSlim _activeSequenceLock = new SemaphoreSlim(1, 1);
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
            // Allow re-triggering / restarting even if already running
            // Allow re-triggering / restarting even if already running
            ControllerMain.LogInfo("[Active] >>> TRIGGERED (OSC/Button) <<<");
            _ctrl.SetActive(true);
        }
        else if (value == 0)
        {
            // Force return to idle
            ControllerMain.LogStep("ReceiveActive(0) => AMBIENT");
            _ctrl.SetActive(false);
        }
    }

    public void TriggerActive(string sourceTag, ref CancellationTokenSource activeCts, CancellationToken globalCt)
    {
        // IMMEDIATE STATE UPDATE to prevent OSC race conditions
        _ctrl.activeRunning = true;
        _ctrl.inIdle = false;
        _ctrl._returnStartWithA = _ctrl.usingA;
        
        if (activeCts != null) { activeCts.Cancel(); activeCts.Dispose(); }
        
        _activeSessionId++;
        int currentSession = _activeSessionId;
        
        activeCts = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(globalCt, activeCts.Token);
        
        _ = StartActiveSequenceSerializedAsync(sourceTag, currentSession, linked.Token);
        ControllerMain.LogStep($"Active sequence triggered (source={sourceTag}, session={currentSession}).");
    }

    private async Task StartActiveSequenceSerializedAsync(string sourceTag, int sessionId, CancellationToken ct)
    {
        await _activeSequenceLock.WaitAsync(ct);
        try
        {
            if (sessionId != _activeSessionId) return;
            
            await _idle.StopIdleWorkAsync(ct);
            await _idle.StopIdleLoopOwnedAsync(ct);

            await ActiveSequenceAsync(sourceTag, sessionId, ct);
        }
        catch (OperationCanceledException) { /* Expected on cancel */ }
        catch (Exception ex)
        {
            ControllerMain.LogError($"Active sequence error: {ex.Message}");
        }
        finally
        {
            _activeSequenceLock.Release();
        }
    }

    public void RequestReturnToIdle(ref CancellationTokenSource activeCts, CancellationToken globalCt)
    {
        if (!_ctrl.activeRunning) return;
        ControllerMain.LogStep("RequestReturnToIdle -> Returning to Ambient.");
        
        if (activeCts != null) { activeCts.Cancel(); activeCts.Dispose(); activeCts = null; }
        _ = ForceReturnToIdleAsync(globalCt);
    }

    private async Task PauseIdlePlayersAsync(CancellationToken ct)
    {
        await AsyncExtensions.RunOnMainThread(() =>
        {
            try { if (_ctrl.idleA) _ctrl.idleA.speed = 0f; } catch {}
            try { if (_ctrl.idleB) _ctrl.idleB.speed = 0f; } catch {}
        }, ct);
    }

    private async Task ActiveSequenceAsync(string sourceTag, int sessionId, CancellationToken ct)
    {
        // Fade out idle
        float idleOut = Mathf.Max(0.01f, _ctrl.idleToActiveFadeOut);
        await _ctrl.FadeTwoAsync(_ctrl.idleGA, _ctrl.idleGB, 0f, 0f, idleOut, ct);

        // Pause idle players to reduce GPU decoder pressure
        await PauseIdlePlayersAsync(ct);

        await ReleaseStandbyPlayerAsync(ct);

        if (_ctrl.activeGroup) _ctrl.activeGroup.alpha = 0f;

        if (sessionId != _activeSessionId) return;

        // Load active video
        if (!await OpenValidActiveAsync(_ctrl.activeHP, ct))
        {
            await CleanupAndReturnToIdleAsync(sessionId, true, ct); // Treat as forced return on failure
            return;
        }

        // Playback
        _ = _music.StartMusicAfterDelayAsync(_ctrl.musicRampIn, ct);

        float fin = Mathf.Max(0.05f, _ctrl.activeFadeIn);
        await _ctrl.FadeOneAsync(_ctrl.activeGroup, 0f, 1f, fin, ct);

        if (sessionId != _activeSessionId) return;

        float dur  = Mathf.Max(0.2f, (float)_ctrl.activeHP.streamDuration);
        float fout = Mathf.Max(0.05f, _ctrl.activeFadeOut);

        float fadeOutStart = _ctrl.activeUseFixedWindow
            ? Mathf.Min(fin + Mathf.Max(0f, _ctrl.activeFixedMidHoldSeconds), Mathf.Max(0f, dur - fout))
            : Mathf.Max(0f, Mathf.Max(dur - fout, fin <= dur ? fin : dur * 0.5f));

        try
        {
            await AsyncExtensions.WaitUntil(() => _ctrl.activeHP != null && _ctrl.activeHP.time >= fadeOutStart, ct);
        }
        catch (OperationCanceledException) {}

        if (sessionId != _activeSessionId) return;

        await CleanupAndReturnToIdleAsync(sessionId, false, ct);
    }

    private async Task ForceReturnToIdleAsync(CancellationToken ct)
    {
        await CleanupAndReturnToIdleAsync(_activeSessionId, true, ct);
    }

    private async Task CleanupAndReturnToIdleAsync(int sessionId, bool isForced, CancellationToken ct)
    {
        try
        {
            // 1. Fade out Active & Music
            var musicTask = _music.RampDownToZeroAsync(Mathf.Max(0.01f, _ctrl.musicRampOut), ct);
            
            float fout = Mathf.Max(0.05f, _ctrl.activeFadeOut);
            if (_ctrl.activeGroup && _ctrl.activeGroup.alpha > 0f)
                await _ctrl.FadeOneAsync(_ctrl.activeGroup, _ctrl.activeGroup.alpha, 0f, fout, ct);
            
            await musicTask;

            if (!_ctrl.activeUseFixedWindow && !isForced && _ctrl.activeEndHoldSeconds > 0f)
                await AsyncExtensions.WaitForSeconds(_ctrl.activeEndHoldSeconds, ct);

            if (!isForced && sessionId != _activeSessionId) return;

            // 2. Release Active Player safely
            if (_ctrl.activeHP != null)
            {
                await _assetManager.ReleaseVideoAsync(_ctrl.activeHP, ct, () => 
                {
                    if (_ctrl.activeImg != null) _ctrl.activeImg.texture = null;
                });
            }

            // 3. Prepare FRESH Idle Content (CRITICAL: Load BEFORE fading in)
            // This ensures we fade in fresh content, not stale old idle videos
            _ctrl.usingA = _ctrl._returnStartWithA;
            
            // Ensure both idle players are at 0 alpha while we load fresh content
            await AsyncExtensions.RunOnMainThread(() =>
            {
                if (_ctrl.idleGA) _ctrl.idleGA.alpha = 0f;
                if (_ctrl.idleGB) _ctrl.idleGB.alpha = 0f;
            }, ct);

            // Load fresh content on the primary player (what we'll fade in)
            var primaryPlayer = _ctrl.usingA ? _ctrl.idleA : _ctrl.idleB;
            try 
            { 
                await _idle.PrepareIdleForSideAsync(primaryPlayer, _ctrl.usingA, ct, force: true);
                ControllerMain.LogInfo($"[Return] Prepared fresh idle for {(_ctrl.usingA ? "A" : "B")}");
            }
            catch (Exception ex) 
            { 
                ControllerMain.LogError($"Error prepping idle return: {ex.Message}"); 
            }

            // Load the standby player in parallel (so it's ready for the loop)
            var standbyPlayer = _ctrl.usingA ? _ctrl.idleB : _ctrl.idleA;
            _ = _idle.PrepareIdleForSideAsync(standbyPlayer, !_ctrl.usingA, ct, force: true);

            // 4. Black hold BEFORE fade in
            if (_ctrl.returnBlackHold > 0f) 
                await AsyncExtensions.WaitForSecondsRealtime(_ctrl.returnBlackHold, ct);
            
            // 5. Fade in the FRESH idle content (not old stale content)
            var gStart = _ctrl.usingA ? _ctrl.idleGA : _ctrl.idleGB;
            if (gStart) 
                await _ctrl.FadeOneAsync(gStart, 0f, 1f, Mathf.Max(0.05f, _ctrl.returnFade), ct);

            // 6. Start Idle Loop (it will use the already-prepared content)
            if (sessionId == _activeSessionId)
            {
                _ctrl.activeRunning = false;
                _ctrl.inIdle = true;
                if (_ctrl.activeTrigger) _ctrl.activeTrigger.SetInteractable(true);
                ControllerMain.LogInfo($"State reset (Session {sessionId}): inIdle=true, activeRunning=false");
            }
            
            // Signal that idle is already primed (skip re-load in StartIdleNowAsync)
            _ctrl._returnStartIsA = _ctrl.usingA;
            _ctrl._idlePrimedFromReturn = true;
            
            // Start the loop (it will see _idlePrimedFromReturn and skip re-prep)
            _ = _idle.StartIdleLoopOwnedAsync(ct);
            
            ControllerMain.LogStep(isForced ? "Forced return to idle complete." : $"Active complete (session={sessionId}). Returned to idle.");
        }
        catch (OperationCanceledException)
        {
            ControllerMain.LogStep("CleanupAndReturnToIdle cancelled (new session started?).");
        }
        catch (Exception ex)
        {
            ControllerMain.LogError($"CleanupAndReturnToIdle CRASHED: {ex.Message}\n{ex.StackTrace}");
            // Attempt to restart idle anyway if we crashed
             _ = _idle.StartIdleNowAsync(ct);
        }
        finally
        {
            // Only reset state if we are the LATEST session. 
            if (sessionId == _activeSessionId)
            {
                _ctrl.activeRunning = false;
                _ctrl.inIdle = true;
                if (_ctrl.activeTrigger) _ctrl.activeTrigger.SetInteractable(true);
                ControllerMain.LogInfo($"State reset (Session {sessionId}): inIdle=true, activeRunning=false");
            }
            ControllerMain.LogInfo($"Cleanup finished for Session {sessionId}. Current is {_activeSessionId}.");
        }
    }

    private async Task ReleaseStandbyPlayerAsync(CancellationToken ct)
    {
        HapPlayer standbyHp = _ctrl.usingA ? _ctrl.idleB : _ctrl.idleA;
        RawImage standbyImg = _ctrl.usingA ? _ctrl.idleImgB : _ctrl.idleImgA;
        
        if (standbyHp != null)
        {
            await AsyncExtensions.RunOnMainThread(() =>
            {
                if (standbyImg) standbyImg.texture = null;
                standbyHp.targetTexture = null;
            }, ct);
            
            await AsyncExtensions.WaitForEndOfFrame(ct);
            await AsyncExtensions.WaitFrames(1, ct);
            await _assetManager.ReleaseVideoAsync(standbyHp, ct);
        }
    }

    private async Task<bool> OpenValidActiveAsync(HapPlayer hp, CancellationToken ct)
    {
        if (hp == null || _ctrl.activeList == null || _ctrl.activeList.Length == 0) return false;

        int tries = 0;
        while (tries++ < Mathf.Max(1, _ctrl.hapMaxAttemptsPerPick) && !ct.IsCancellationRequested)
        {
            int idx = CyclePicker.NextFromCycleFiltered(_cycleActive, _ctrl.activeList.Length, _lastActiveIndex, _badActive, out int newLast);
            _lastActiveIndex = newLast;

            if (idx >= 0 && idx < _ctrl.activeList.Length)
            {
                string rel = ArtworkController.GetRel(_ctrl.activeList[idx]);
                string fullPath = _media.ResolveLoadPath(rel, true);
                
                if (await _assetManager.LoadAndAssignVideoAsync(fullPath, hp, _ctrl._cfgW, _ctrl._cfgH, "Active", _ctrl.activeRT, _ctrl.activeImg, false, ct, (rt) => _ctrl.activeRT = rt))
                    return true;
                
                _badActive.Add(idx);
            }
            await AsyncExtensions.WaitForSeconds(0.2f, ct);
        }
        return false;
    }

    public void Cleanup() => _activeSequenceLock?.Dispose();
}
