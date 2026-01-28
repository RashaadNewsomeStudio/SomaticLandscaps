using System;
using System.IO;
using UnityEngine;

    [Serializable]
    public class ArtworkConfigData
    {
        public int   ResolutionWidth  = 1920;
        public int   ResolutionHeight = 1080;

        public float IdleFadeDuration = 1.5f;
        public float IdlePrepareLead  = 0.6f;
        public float IdleMinFade      = 0.2f;
        public bool  CrossfadeIdle    = true;

        public float ActiveFadeIn  = 2.0f;
        public float ActiveFadeOut = 2.0f;
        public bool  ActiveUseFixedWindow      = true;
        public float ActiveFixedMidHoldSeconds = 22.0f;
        public float ActiveEndHoldSeconds      = 0.15f;

        public float ReturnFade      = 0.6f;
        public float ReturnBlackHold = 0.3f;
        public bool  RandomizeIdleStartOnReturn = true;

        public float MusicVolume             = 1.0f;
        public float MusicRampIn             = 0.25f;
        public float MusicFadeIn             = 0.5f;
        public float MusicRampOut            = 0.6f;
        public bool  MusicSyncWithActiveFade = true;

        public int   PanelToggleKey = (int)KeyCode.F1;

        // Flags from JSON
        public bool IgnoreAmbientWhileActive   = false;
        public bool ActiveClickCancelsToAmbient = false;
        
        // OSC Control: Block "0 ambient" commands from forcing return to idle
        public bool OscBlockReturnToIdle = false;

        // Music loader controls
        public bool   AutoLoadMusicFromStreaming = true;
        public string MusicSubfolder = "Music";

        // NEW: enable/disable startup panels (intro + screen adjustment)
        public bool EnableStartupPanels = true;
        
        // Safety: Prevent rapid toggling
        public float TriggerCooldown = 2.0f;
    }

    public class ArtworkConfigService
    {
        private readonly ArtworkController _ctrl;

        public bool StartupPanelsEnabled { get; private set; } = true;

        public ArtworkConfigService(ArtworkController ctrl)
        {
            _ctrl = ctrl;
        }

        public void TryLoadAndApply(bool force = false)
        {
            if (TryLoadArtworkConfig(out var cfg)) 
            {
                ApplyArtworkConfig(cfg, force);
            }
        }

        private bool TryLoadArtworkConfig(out ArtworkConfigData cfg)
        {
            cfg = null;
            string artworkConfigPath = ControllerMain.PathInConfig("ArtworkConfig.json");
            try
            {
                if (File.Exists(artworkConfigPath))
                {
                    string json = File.ReadAllText(artworkConfigPath, new System.Text.UTF8Encoding(false));
                    cfg = JsonUtility.FromJson<ArtworkConfigData>(json);
                    if (cfg != null)
                    {
                        ControllerMain.LogInfo($"[ArtworkController] Loaded ArtworkConfig: {artworkConfigPath}");
                        return true;
                    }
                }
                else
                {
                    ControllerMain.LogWarn($"[ArtworkController] No ArtworkConfig.json at {artworkConfigPath}");
                }
            }
            catch (Exception ex)
            {
                ControllerMain.LogWarn($"[ArtworkController] Failed to load ArtworkConfig: {ex.Message}");
            }
            return false;
        }

        private void ApplyArtworkConfig(ArtworkConfigData c, bool force)
        {
            if (c == null) return;

            // FIX 4: ProFix3 Safety Guard
            // Block hot-reloading if system is running or native video ops are busy
            // UNLESS force is true (startup)
            bool isBusy = SomaticLandscapes.Async.AsyncAssetManager.IsBusy;
            if (!force && Application.isPlaying && (_ctrl.inIdle || _ctrl.activeRunning || isBusy))
            {
                ControllerMain.LogWarn($"[ArtworkConfig] BLOCKED: Cannot apply config while system is running (Idle={_ctrl.inIdle} Active={_ctrl.activeRunning} Busy={isBusy}). STOP system first.");
                return;
            }

            // Update local config vars (if we decide to keep them in controller for now, we set them here)
            // But better to just set the controller's public properties if they exist, or fields if they are internal.
            // Since we are "Smart Splitting", we need to update the Orchestrator's state.
            
            _ctrl._cfgW = Mathf.Max(16, c.ResolutionWidth);
            _ctrl._cfgH = Mathf.Max(16, c.ResolutionHeight);

            // Update or create finalOutputRT if resolution changed
            if (_ctrl.finalOutputRT == null || 
                _ctrl.finalOutputRT.width != _ctrl._cfgW || 
                _ctrl.finalOutputRT.height != _ctrl._cfgH)
            {
                // Release old RT if it exists
                if (_ctrl.finalOutputRT != null)
                {
                    _ctrl.finalOutputRT.Release();
                    UnityEngine.Object.Destroy(_ctrl.finalOutputRT);
                }

                // Create new finalOutputRT with updated resolution
                _ctrl.finalOutputRT = new RenderTexture(_ctrl._cfgW, _ctrl._cfgH, 24, RenderTextureFormat.ARGB32)
                {
                    name = "FinalOutput_SpoutRT",
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    antiAliasing = 1
                };
                _ctrl.finalOutputRT.Create();
                Debug.Log($"[ArtworkConfig] Updated FinalOutputRT to {_ctrl._cfgW}x{_ctrl._cfgH}");

                // Re-initialize Spout bridge with new RT
                var spouter = UnityEngine.Object.FindFirstObjectByType<ForceSpoutTexture>();
                if (spouter != null)
                {
                    spouter.Initialize(_ctrl.finalOutputRT);
                }
            }

            RecreateRTIfMismatch(ref _ctrl.idleRT_A, ref _ctrl._ownIdleA, "IdleA_RT", _ctrl._cfgW, _ctrl._cfgH);
            RecreateRTIfMismatch(ref _ctrl.idleRT_B, ref _ctrl._ownIdleB, "IdleB_RT", _ctrl._cfgW, _ctrl._cfgH);
            RecreateRTIfMismatch(ref _ctrl.activeRT,  ref _ctrl._ownActive, "Active_RT", _ctrl._cfgW, _ctrl._cfgH);
            
            // Re-wire players since RTs might have changed
            _ctrl.WirePlayersToRTs();

            _ctrl.idleFade                   = Mathf.Max(0.01f, c.IdleFadeDuration);
            _ctrl.idlePrepareLead            = Mathf.Max(0f,     c.IdlePrepareLead);
            _ctrl.idleMinFade                = Mathf.Max(0.01f,  c.IdleMinFade);
            _ctrl.crossfadeIdle              = c.CrossfadeIdle;

            _ctrl.activeFadeIn               = Mathf.Max(0.05f,  c.ActiveFadeIn);
            _ctrl.activeFadeOut              = Mathf.Max(0.05f,  c.ActiveFadeOut);
            _ctrl.activeUseFixedWindow       = c.ActiveUseFixedWindow;
            _ctrl.activeFixedMidHoldSeconds  = Mathf.Max(0f,     c.ActiveFixedMidHoldSeconds);
            _ctrl.activeEndHoldSeconds       = Mathf.Max(0f,     c.ActiveEndHoldSeconds);

            _ctrl.returnFade                 = Mathf.Max(0.05f,  c.ReturnFade);
            _ctrl.returnBlackHold            = Mathf.Max(0f,     c.ReturnBlackHold);
            _ctrl.randomizeIdleStartOnReturn = c.RandomizeIdleStartOnReturn;

            _ctrl.musicVolume                = Mathf.Clamp01(c.MusicVolume);
            _ctrl.musicRampIn                = Mathf.Max(0f,     c.MusicRampIn);
            _ctrl.musicFadeIn                = Mathf.Max(0f,     c.MusicFadeIn);
            _ctrl.musicRampOut               = Mathf.Max(0f,     c.MusicRampOut);
            _ctrl.musicSyncWithActiveFade    = c.MusicSyncWithActiveFade;

            // _ctrl.panelToggleKey             = (KeyCode)c.PanelToggleKey; // REMOVED - TuningPanel deleted

            // Apply behavior toggle from JSON
            _ctrl.ignoreAmbientWhileActive   = c.IgnoreAmbientWhileActive;
            _ctrl.activeClickCancelsToAmbient = c.ActiveClickCancelsToAmbient;
            
            // OSC Control: Block return-to-idle commands
            var oscController = UnityEngine.Object.FindFirstObjectByType<ArtworkModeController>();
            if (oscController != null)
            {
                oscController.blockReturnToIdle = c.OscBlockReturnToIdle;
                if (c.OscBlockReturnToIdle)
                    ControllerMain.LogInfo("[ArtworkConfig] OSC return-to-idle (0) commands BLOCKED");
            }

            // Loader config
            _ctrl.autoLoadMusicFromStreaming = c.AutoLoadMusicFromStreaming;
            if (!string.IsNullOrEmpty(c.MusicSubfolder))
                 _ctrl.musicSubfolder = c.MusicSubfolder;

            // Apply Cooldown
            _ctrl.triggerCooldown = Mathf.Max(0.5f, c.TriggerCooldown);

            // NEW: startup panels flag
            StartupPanelsEnabled = c.EnableStartupPanels;
            ControllerMain.LogInfo($"[ArtworkConfig] Parsed 'EnableStartupPanels': {StartupPanelsEnabled}");
            // Also update controller field if needed (though we can just use this property)

            ControllerMain.LogStep("[ArtworkController] Applied ArtworkConfig.json");
        }

        private void RecreateRTIfMismatch(ref RenderTexture rt, ref RenderTexture owned, string name, int w, int h)
        {
            var target = rt ? rt : owned;
            if (!target || target.width != w || target.height != h)
            {
                if (owned) 
                { 
                    // Use AsyncAssetManager instead of direct Destroy or Queue
                    // But we can try Release first
                    try { owned.Release(); } catch {} 
                    
                    // Unified Disposal (PR-03)
                    SomaticLandscapes.Async.AsyncAssetManager.Instance?.ScheduleDestroy(owned);
                    owned = null; 
                }

                var newRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
                {
                    name = name,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    antiAliasing = 1
                };
                newRT.Create();
                owned = newRT;
                rt = rt ? rt : owned;

                // Sync the RawImages if they exist
                 if (name.StartsWith("IdleA", StringComparison.Ordinal)) 
                 { 
                     _ctrl.idleRT_A = owned; 
                     if (_ctrl.idleImgA) _ctrl.idleImgA.texture = _ctrl.idleRT_A; 
                 }
                 else if (name.StartsWith("IdleB", StringComparison.Ordinal)) 
                 { 
                     _ctrl.idleRT_B = owned; 
                     if (_ctrl.idleImgB) _ctrl.idleImgB.texture = _ctrl.idleRT_B; 
                 }
                 else 
                 { 
                     _ctrl.activeRT = owned; 
                     if (_ctrl.activeImg) _ctrl.activeImg.texture = _ctrl.activeRT; 
                 }
            }
        }
    }
