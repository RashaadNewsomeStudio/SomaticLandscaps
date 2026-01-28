using System;
using System.Collections;
using System.IO; 
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Klak.Hap;
using SomaticLandscapes.Async;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class ArtworkController : MonoBehaviour
{
    // MODULES
    private ArtworkConfigService _config;
    private MediaDiscoveryService _media;
    private StartupPanelsFlow _startup;
    private IdleAmbientLoop _idle;
    private ActiveFlow _active;
    private MusicFlow _music;

    // ASYNC
    private AsyncAssetManager _assetManager;
    private CancellationTokenSource _cts;
    private CancellationTokenSource _activeCts;
    private CancellationTokenSource _idleCts;
    
    // Expose controller lifetime token for proper cancellation hierarchy
    public CancellationToken LifetimeToken => _cts?.Token ?? CancellationToken.None;

    // -- SERIALIZED FIELDS --

    // Access Helper
    [Serializable]
    public class StreamingAssetRef
    {
        public string relativePath;
        #if UNITY_EDITOR
        public DefaultAsset file;
        public void SyncFromEditorObject() { /* Editor-only sync logic */ } 
        #endif
    }
    public static bool HasAnyValid(StreamingAssetRef[] arr) {
        if (arr == null) return false;
        foreach (var r in arr) if(r!=null && !string.IsNullOrEmpty(r.relativePath)) return true;
        return false;
    }
    public static string GetRel(StreamingAssetRef r) => r?.relativePath;

    public int _cfgW = 1920, _cfgH = 1080;

    [Header("BEHAVIOR")]
    public bool ignoreAmbientWhileActive = true; 
    public bool activeClickCancelsToAmbient = false; // Mapped from JSON 

    // STARTUP
    [Header("STARTUP UI")]
    public GameObject  introPanel;
    public CanvasGroup introGroup;
    public TMP_Text    introText1, introText2, introText3, introText4;
    public float       introLineStep=0.6f, introFadeIn=0.5f, introHold=0.6f, introFadeOut=0.5f;

    public GameObject  adjustPanel;
    public CanvasGroup adjustGroup;
    public TMP_Text    adjustText;
    public Image       adjustImage;
    public float       adjustScaleDuration=2.0f;
    public float       adjustFadeIn=0.35f, adjustFadeOut=0.35f;
    public float       adjustStartScale = 0.15f;
    public float       standbyDotStep = 0.25f;
    public string      standbyBaseText = "Standby";

    // HAP
    [Header("HAP RESOURCES")]
    public RenderTexture  idleRT_A, idleRT_B, activeRT;
    public RawImage       idleImgA, idleImgB, activeImg;
    public HapPlayer      idleA, idleB, activeHP;
    public CanvasGroup    idleGA, idleGB, activeGroup;

    // LISTS
    [Header("FILES")]
    public StreamingAssetRef[] idleShared, idleAList, idleBList, activeList;

    // CONFIG
    [Header("TIMING")]
    public float idleFade = 1.5f;
    public float idlePrepareLead = 0.6f;
    public float idleMinFade = 0.2f;
    public bool  crossfadeIdle = true;
    
    public float idleToActiveFadeOut = 2f;
    public float activeFadeIn = 2f, activeFadeOut = 2f;
    public bool  activeUseFixedWindow = true;
    public float activeFixedMidHoldSeconds = 22f, activeEndHoldSeconds = 0.15f;
    public float returnFade = 0.6f, returnBlackHold = 0.3f;
    public bool  randomizeIdleStartOnReturn = true;

    // Audio
    [Header("AUDIO")]
    public AudioSource musicSource;
    public bool        autoLoadMusicFromStreaming = true;
    public string      musicSubfolder = "Music";
    public AudioClip[] activeMusicClipsFallback;
    public float       musicVolume = 1f;
    public float       musicRampIn = 0.25f, musicFadeIn = 1.5f, musicRampOut = 0.6f;
    public bool        musicSyncWithActiveFade = true;

    // Robustness
    [Header("TUNING")]
    public float hapOpenTimeout = 3.0f;
    public int   hapMaxAttemptsPerPick = 3; 
    public float hapRetryDelay = 0.5f;       
    public int   hapQuarantineAfterFailures = 3;

    // UI
    [Header("TRIGGERS")]
    [HideInInspector] public ActiveTriggerButton activeTrigger;
    // public Button ambientButton; // Removed as requested

    // STATE
    public bool usingA = true, inIdle = true, activeRunning = false;
    public int lastIdleIndexA = -1, lastIdleIndexB = -1;
    public bool _idlePrimedFromReturn = false, _returnStartIsA = true, _returnStartWithA = true;
    
    // Internal (made public for ConfigService access)
    [HideInInspector] public RenderTexture _ownIdleA, _ownIdleB, _ownActive; 

    // External paths
    [HideInInspector] public string _externalAmbientPath;
    [HideInInspector] public string _externalActivePath;
    [HideInInspector] public string _externalAudioPath;

    // ==========================================
    // LIFECYCLE
    // ==========================================

    void Awake()
    {
        _cts = new CancellationTokenSource();
        _assetManager = AsyncAssetManager.Instance;
        if (!_assetManager) _assetManager = new GameObject("AsyncAssetManager").AddComponent<AsyncAssetManager>();

        // Get external paths from ControllerMain
        var cm = FindFirstObjectByType<ControllerMain>();
        if (cm != null)
        {
            _externalAmbientPath = cm.AmbientStreamPath;
            _externalActivePath  = cm.ActiveStreamPath;
            _externalAudioPath   = cm.AudioPath;
        }

        _config  = new ArtworkConfigService(this);
        _media   = new MediaDiscoveryService(this, _assetManager);
        _music   = new MusicFlow(this, _assetManager);
        _idle    = new IdleAmbientLoop(this, _assetManager);
        
        // ActiveFlow needs a way to get the ref to activeCts. 
        // We pass a lambda accessor.
        _active  = new ActiveFlow(this, _assetManager, _idle, _music, () => _activeCts);
        
        _startup = new StartupPanelsFlow(this);

        _config.TryLoadAndApply(true);      
        
        EnsureRTs();
        WirePlayersToRTs();
        
        if (idleGA)      idleGA.alpha = 0f;
        if (idleGB)      idleGB.alpha = 0f;
        if (activeGroup) activeGroup.alpha = 0f;

        // FIX: Force hide startup panels by default. 
        // If they are skipped by config, they must not be visible.
        if (introPanel)  introPanel.SetActive(false);
        if (adjustPanel) adjustPanel.SetActive(false);

        _media.AutoPopulateIfNeeded();  
        Application.runInBackground = true;
    }

    async void Start()
    {
        try
        {
            // Try to find trigger automatically if missing
            if (!activeTrigger) activeTrigger = FindFirstObjectByType<ActiveTriggerButton>();

            // Ensure AudioSource
            if (!musicSource) musicSource = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            musicSource.playOnAwake = false;
            musicSource.loop = true;
            musicSource.volume = musicVolume;
            
            ControllerMain.LogStep("ArtworkController Start logic beginning...");
            await InitializeAndStartAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            ControllerMain.LogInfo("ArtworkController startup cancelled (likely shutdown).");
        }
        catch (Exception ex)
        {
            ControllerMain.LogError($"ArtworkController CRITICAL START FAILURE: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Watchdog to ensure button doesn't stay stuck disabled
    void LateUpdate() 
    {
         // Legacy: _rtQueue.Drain() removed. 
         // AsyncAssetManager handles disposal natively in its LateUpdate.
        // If we have the wrapper script, check it
        if (activeTrigger && inIdle && !activeRunning)
        {
            // We can strictly enforce interactable here, 
            // OR blindly set it true if we suspect it might have been disabled.
             activeTrigger.SetInteractable(true);
        }
    }

    private async Task InitializeAndStartAsync(CancellationToken ct)
    {
        // FIX: Ensure config is loaded after ControllerMain is definitely ready (solves execution order race)
        _config.TryLoadAndApply(true);
        
        SanityCheckIds();
        
        ControllerMain.LogStep("Step 1: Discovering Media...");
        await _media.AutoDiscoverVideosAsync(ct);
        
        ControllerMain.LogStep("Step 2: Loading Music...");
        if (autoLoadMusicFromStreaming) await _music.LoadMusicAsync(ct);

        ControllerMain.LogStep($"Step 3: Starting Loop (Panels={_config.StartupPanelsEnabled})...");
        if (_config.StartupPanelsEnabled) await _startup.RunAsync(ct, c => StartIdleLoop(c));
        else _ = StartIdleLoop(ct);
    }

    public Task StartIdleLoop(CancellationToken externalCt)
    {
        StopIdleLoop();
        _idleCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _cts.Token);
        return _idle.StartIdleNowAsync(_idleCts.Token);
    }

    public void StopIdleLoop()
    {
        if (_idleCts != null) { _idleCts.Cancel(); _idleCts.Dispose(); _idleCts = null; }
    }

    private void SanityCheckIds()
    {
        if (idleA && idleB)
            ControllerMain.LogStep($"[Sanity] IdleA={idleA.GetInstanceID()} IdleB={idleB.GetInstanceID()} SAME={idleA==idleB}");
        else ControllerMain.LogError("[Sanity] One or more idle players are NULL!");
        
        if (idleGA && idleGB)
            ControllerMain.LogStep($"[Sanity] GroupA={idleGA.GetInstanceID()} GroupB={idleGB.GetInstanceID()} SAME={idleGA==idleGB}");
    }

    void OnDestroy()
    {
        _activeCts?.Cancel(); _activeCts?.Dispose();
        _cts?.Cancel(); _cts?.Dispose();
        _active?.Cleanup();
    }

    // ==========================================
    // API
    // ==========================================

    public void ReceiveActive(int value) => _active.ReceiveActive(value);
    public void SetActive(bool on)
    {
        if (on) _active.TriggerActive("External", ref _activeCts, _cts.Token);
        else    _active.RequestReturnToIdle(ref _activeCts, _cts.Token);
    }

    public void OnActiveButtonClicked() { 
        // Legacy: Logic moved to ActiveTriggerButton.cs
        // Kept empty or remove if no other references.
    }

    // ==========================================
    // HELPERS
    // ==========================================

    private void EnsureRTs() 
    {
        if (!idleRT_A) _ownIdleA = CreateRT("IdleA_RT");
        if (!idleRT_B) _ownIdleB = CreateRT("IdleB_RT");
        if (!activeRT) _ownActive = CreateRT("Active_RT");
        
        idleRT_A = idleRT_A ? idleRT_A : _ownIdleA;
        idleRT_B = idleRT_B ? idleRT_B : _ownIdleB;
        activeRT = activeRT ? activeRT : _ownActive;
        
        if (idleImgA) idleImgA.texture = idleRT_A;
        if (idleImgB) idleImgB.texture = idleRT_B;
        if (activeImg) activeImg.texture = activeRT;
    }

    private RenderTexture CreateRT(string name)
    {
        int w = Mathf.Max(16, _cfgW), h = Mathf.Max(16, _cfgH);
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) {
            name = name, wrapMode = TextureWrapMode.Clamp,
            useMipMap = false, antiAliasing = 1
        };
        rt.Create();
        return rt;
    }

    public void WirePlayersToRTs()
    {
        if (idleA)    { idleA.targetTexture = idleRT_A;    idleA.targetRenderer = null; }
        if (idleB)    { idleB.targetTexture = idleRT_B;    idleB.targetRenderer = null; }
        if (activeHP) { activeHP.targetTexture = activeRT; activeHP.targetRenderer = null; }
    }

    // Redirects for Modules using old methods
    public Task FadeOneAsync(CanvasGroup g, float from, float to, float dur, CancellationToken ct) 
        => g.FadeAlpha(from, to, dur, ct);
    public Task FadeTwoAsync(CanvasGroup a, CanvasGroup b, float toA, float toB, float dur, CancellationToken ct)
        => AsyncExtensions.Crossfade(a, b, toA, toB, dur, ct);
}