using System;
using System.Collections;
using System.IO;  // Added System.IO
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
    // ==========================================
    // MODULES (Plain C# Classes)
    // ==========================================
    private RtDisposalQueue _rtQueue;
    private ArtworkConfigService _config;
    private MediaDiscoveryService _media;
    private StartupPanelsFlow _startup;
    private IdleAmbientLoop _idle;
    private ActiveFlow _active;
    private MusicFlow _music;
    // _tuning removed - TuningPanelBinder deleted

    // ==========================================
    // ASYNC MANAGEMENT
    // ==========================================
    private AsyncAssetManager _assetManager;
    private CancellationTokenSource _cts;
    private CancellationTokenSource _activeCts;
    private CancellationTokenSource _idleCts; // Added for explicit idle loop control

    // ==========================================
    // SERIALIZED FIELDS (Preserved for compatibility)
    // ==========================================
    
    // Internal fields for modules to access (exposed via internal or public)
    // We keep them public/private as is, but create accessors if needed or just let modules access public.
    // Ideally update them to be internal if in same assembly, or public.
    // For this refactor, we assume modules are in the same assembly or sub-namespace and can access public members.
    
    // -------------------------
    //  StreamingAssets wrapper (Access helper)
    // -------------------------
    [Serializable]
    public class StreamingAssetRef
    {
        [Tooltip("Relative path under StreamingAssets (auto-filled in Editor when you drop a file).")]
        public string relativePath;
    #if UNITY_EDITOR
        [Tooltip("Drag a file from under Assets/StreamingAssets/... (Editor only).")]
        public DefaultAsset file;
        public void SyncFromEditorObject()
        {
            if (file == null) return;
            var assetPath   = AssetDatabase.GetAssetPath(file).Replace('\\', '/');
            var saRootAbs   = Application.streamingAssetsPath.Replace('\\', '/');
            var projectRoot = Application.dataPath.Replace("Assets", "");
            string assetAbs;
            try {
                 assetAbs = Path.GetFullPath(Path.Combine(projectRoot, assetPath)).Replace('\\', '/');
            } catch { return; }

            if (!assetAbs.StartsWith(saRootAbs, StringComparison.Ordinal))
            {
                Debug.LogWarning($"[ArtworkController] {assetPath} is not under StreamingAssets.");
                return;
            }
            
            // Simple string op to avoid ambiguity
            var rel = assetAbs.Substring(saRootAbs.Length);
            // Trim leading slashes manually
            while (rel.Length > 0 && (rel[0] == '/' || rel[0] == '\\')) {
                rel = rel.Substring(1);
            }
            relativePath = rel;
        }
    #endif
    }

    public static bool HasAnyValid(StreamingAssetRef[] arr)
    {
        if (arr == null || arr.Length == 0) return false;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] != null && !string.IsNullOrEmpty(arr[i].relativePath))
                return true;
        return false;
    }

    public static string GetRel(StreamingAssetRef r) => r == null ? null : r.relativePath;

    // -------------------------
    //  Config State
    // -------------------------
    public int _cfgW = 1920, _cfgH = 1080;

    [Header("BEHAVIOR")]
    [SerializeField] public bool ignoreAmbientWhileActive = true; 

    // -------------------------
    //  Startup Panels
    // -------------------------
    [Header("STARTUP • Panel 1: Intro (optional)")]
    public GameObject  introPanel;
    public CanvasGroup introGroup;
    public Image       introBackground;
    public TMP_Text    introText1, introText2, introText3, introText4;
    [Min(0f)] public float introLineStep = 0.6f, introFadeIn = 0.5f, introHold = 0.6f, introFadeOut = 0.5f;

    [Header("STARTUP • Panel 2: Screen Adjustment (optional)")]
    public GameObject  adjustPanel;
    public CanvasGroup adjustGroup;
    public TMP_Text    adjustText;
    public Image       adjustImage;
    [Min(0.01f)] public float adjustScaleDuration = 2.0f;
    [Min(0f)]    public float adjustFadeIn = 0.35f, adjustFadeOut = 0.35f;
    [Range(0.01f, 1.0f)] public float adjustStartScale = 0.15f;
    [Min(0.05f)] public float standbyDotStep = 0.25f;
    public string      standbyBaseText = "Standby";

    // -------------------------
    //  HAP
    // -------------------------
    [Header("HAP • Path Mode")]
    public HapPlayer.PathMode pathMode = HapPlayer.PathMode.StreamingAssets;

    [Header("HAP • RenderTextures (auto if empty)")]
    public RenderTexture  idleRT_A, idleRT_B, activeRT;

    [Header("HAP • RawImages (Targets)")]
    public RawImage       idleImgA, idleImgB, activeImg;

    [Header("IDLE PLAYERS (A/B) • HAP")]
    public HapPlayer      idleA, idleB;
    public CanvasGroup    idleGA, idleGB;

    [Header("ACTIVE VIDEO (overlay RawImage) • HAP")]
    public HapPlayer      activeHP;
    public CanvasGroup    activeGroup;

    // -------------------------
    //  Media lists
    // -------------------------
    [Header("IDLE FILES")]
    public StreamingAssetRef[] idleShared;
    public StreamingAssetRef[] idleAList;
    public StreamingAssetRef[] idleBList;

    [Header("ACTIVE FILES")]
    public StreamingAssetRef[] activeList;

    // -------------------------
    //  Timing
    // -------------------------
    [Header("IDLE CROSSFADE TIMING")]
    [Min(0.01f)] public float  idleFade = 1.5f;
    [Min(0f)]    public float  idlePrepareLead = 0.6f;
    [Min(0.01f)] public float  idleMinFade = 0.2f;
    public bool                crossfadeIdle = true;

    [Header("ACTIVE SEQUENCE TIMING")]
    [Min(0.05f)] public float  idleToActiveFadeOut = 2f;
    [Min(0.05f)] public float  activeFadeIn = 2f, activeFadeOut = 2f;
    public bool                activeUseFixedWindow = true;
    [Min(0f)]    public float  activeFixedMidHoldSeconds = 22f, activeEndHoldSeconds = 0.15f;

    [Header("RETURN TO IDLE")]
    [Min(0.05f)] public float  returnFade = 0.6f;
    [Min(0f)]    public float  returnBlackHold = 0.3f;
    public bool                randomizeIdleStartOnReturn = true;

    // -------------------------
    //  Audio
    // -------------------------
    [Header("AUDIO")]
    public AudioSource         musicSource;
    public bool                autoLoadMusicFromStreaming = true;
    public string              musicSubfolder = "Music";
    public AudioClip[]         activeMusicClipsFallback;
    [Range(0f, 1f)] public float musicVolume = 1f;
    [Min(0f)] public float     musicRampIn  = 0.25f;
    [Min(0f)] public float     musicFadeIn  = 1.5f;
    [Min(0f)] public float     musicRampOut = 0.6f;
    public bool                musicSyncWithActiveFade = true;

    // -------------------------
    //  Robustness
    // -------------------------
    [Header("ROBUSTNESS")]
    [Min(0.05f)] public float  hapOpenTimeout = 3.0f;
    [Min(0.0f)]  public float  hapMinPlayheadAdvance = 0.02f;
    [Min(0.05f)] public float  hapPlayheadGuard = 0.6f;
    [Min(1)]     public int    hapMaxAttemptsPerPick = 3; 
    [Min(0.1f)]  public float  hapRetryDelay = 0.5f;       
    [Min(2)]     public int    hapQuarantineAfterFailures = 3;

    // -------------------------
    //  UI / Control
    // -------------------------
    [Header("Trigger (optional)")]
    public Button             activeButton;
    public Button             ambientButton;

    // (Runtime Tuning Panel removed - not used)

    // -------------------------
    //  Runtime State
    // -------------------------
    // Made public/internal for modules to access
    public bool              usingA = true, inIdle = true, activeRunning = false;
    public int               lastIdleIndexA = -1, lastIdleIndexB = -1; // Shared state
    public bool              _idlePrimedFromReturn = false, _returnStartIsA = true, _returnStartWithA = true;
    
    // Internal RT tracking
    public RenderTexture     _ownIdleA, _ownIdleB, _ownActive;
    
    // External paths
    public string _externalAmbientPath;
    public string _externalActivePath;
    public string _externalAudioPath;

    // ==========================================
    // LIFECYCLE
    // ==========================================

    void Awake()
    {
        _rtQueue = new RtDisposalQueue(this);
        _cts = new CancellationTokenSource();

        _assetManager = AsyncAssetManager.Instance;
        if (_assetManager == null)
        {
            var go = new GameObject("AsyncAssetManager");
            _assetManager = go.AddComponent<AsyncAssetManager>();
            DontDestroyOnLoad(go);
        }

        // Get external paths from ControllerMain
        var cm = FindFirstObjectByType<ControllerMain>();
        if (cm != null)
        {
            _externalAmbientPath = cm.AmbientStreamPath;
            _externalActivePath  = cm.ActiveStreamPath;
            _externalAudioPath   = cm.AudioPath;
        }

        // --- CONSTRUCT MODULES ---
        _config  = new ArtworkConfigService(this, _rtQueue);
        _media   = new MediaDiscoveryService(this, _assetManager);
        _music   = new MusicFlow(this, _assetManager);
        _idle    = new IdleAmbientLoop(this, _assetManager);
        // ActiveFlow needs a way to get the ref to activeCts, we pass a lambda or just manage it in Controller
        _active  = new ActiveFlow(this, _assetManager, _idle, _music, () => _activeCts);
        _startup = new StartupPanelsFlow(this);
        // _tuning  = new TuningPanelBinder(this); // DISABLED: Not used

        // --- INIT ---
        _config.TryLoadAndApply();      
        
        // This is now redundant essentially as ApplyArtworkConfig calls RecreateRT, 
        // but we kept EnsureRTs to be safe or if config fails.
        EnsureRTs(); 
        WirePlayersToRTs();
        PrimeCanvasGroups();

        _media.AutoPopulateIfNeeded();  

        Application.runInBackground = true;
    }

    void Start()
    {
        if (_music != null) EnsureMusicSourceConfigured(); // Helper method on Controller
        // _tuning.InitTuningPanel(); // REMOVED - TuningPanelBinder deleted
        
        // Buttons
        if (activeButton)
            activeButton.onClick.AddListener(() => OnActiveButtonClicked());

        if (ambientButton)
            ambientButton.onClick.AddListener(() => 
            {
                if (activeRunning && ignoreAmbientWhileActive) return;
                SetActive(false);
            });

        _ = InitializeAndStartAsync(_cts.Token);
    }

    private async Task InitializeAndStartAsync(CancellationToken ct)
    {
        StopAllCoroutines();

        await _media.AutoDiscoverVideosAsync(ct);
        
        if (autoLoadMusicFromStreaming)
            await _music.LoadMusicAsync(ct);

        if (_config.StartupPanelsEnabled)
            await _startup.RunAsync(ct, StartIdleNowAsync);
        else
            await StartIdleNowAsync(ct);
    }

    private Task StartIdleNowAsync(CancellationToken ct) => StartIdleLoop(ct);

    public Task StartIdleLoop(CancellationToken externalCt)
    {
        StopIdleLoop();
        _idleCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _cts.Token);
        return _idle.StartIdleNowAsync(_idleCts.Token);
    }

    public void StopIdleLoop()
    {
        if (_idleCts != null)
        {
            _idleCts.Cancel();
            _idleCts.Dispose();
            _idleCts = null;
        }
    }

    public void StopIdlePlayback()
    {
        // Force alpha 0 immediately
        if (idleGA) idleGA.alpha = 0f;
        if (idleGB) idleGB.alpha = 0f;
        
        // Stop players to prevent "white flash" / refresh issues and race conditions
        if (idleA) _assetManager.ReleaseVideo(idleA);
        if (idleB) _assetManager.ReleaseVideo(idleB);
    }

    void Update()
    {
        // _tuning?.CheckToggleInput(); // REMOVED - TuningPanelBinder deleted
    }

    void LateUpdate() => _rtQueue.Drain();

    void OnDestroy()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _active?.Cleanup();  // Dispose semaphore to prevent resource leak
    }

    // ==========================================
    // PUBLIC API
    // ==========================================

    public void ReceiveActive(int value) => _active.ReceiveActive(value);

    public void SetActive(bool on)
    {
        if (on) _active.TriggerActive("External/Level=1", ref _activeCts, _cts.Token);
        else    _active.RequestReturnToIdle(ref _activeCts, _cts.Token);
    }

    public void OnActiveButtonClicked()
    {
        if (inIdle && !activeRunning)
            SetActive(true);
    }
    
    // Helper to expose config reload
    public void LoadConfig() => _config.TryLoadAndApply();
    
    // Helper for Music
    public void EnsureMusicSourceConfigured()
    {
         if (!audioSource) audioSource = GetComponent<AudioSource>();
         if (!audioSource) audioSource = gameObject.AddComponent<AudioSource>();
         
         audioSource.playOnAwake = false;
         audioSource.loop = true;
         audioSource.volume = musicVolume;
    }

    // Helper alias for modules that expect 'audioSource' but we have 'musicSource' serialized usually
    // We'll just define audioSource as the main field or property
    public AudioSource audioSource; // Used by MusicFlow
    
    public void RefreshSlidersFromVars() { /* _tuning?.UpdateUI(); REMOVED */ }

    // ==========================================
    // SHARED UTILITIES/HELPERS (Used by Modules)
    // ==========================================

    public void EnsureRTs() 
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

    RenderTexture CreateRT(string name)
    {
        int w = Mathf.Max(16, _cfgW);
        int h = Mathf.Max(16, _cfgH);
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            antiAliasing = 1
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

    public void PrimeCanvasGroups()
    {
        if (idleGA)      idleGA.alpha = 0f;
        if (idleGB)      idleGB.alpha = 0f;
        if (activeGroup) activeGroup.alpha = 0f;
    }

    // Fades
    public async Task FadeOneAsync(CanvasGroup g, float from, float to, float dur, CancellationToken ct)
    {
        if (!g) return;
        dur = Mathf.Max(0.01f, dur);
        float t = 0f; g.alpha = from;
        while (t < dur)
        {
            t += Time.deltaTime;
            g.alpha = Mathf.Lerp(from, to, t / dur);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
        g.alpha = to;
    }

    public async Task FadeTwoAsync(CanvasGroup a, CanvasGroup b, float toA, float toB, float dur, CancellationToken ct)
    {
        dur = Mathf.Max(0.01f, dur);
        float t = 0f, a0 = a ? a.alpha : 0f, b0 = b ? b.alpha : 0f;
        while (t < dur)
        {
            t += Time.deltaTime; float k = Mathf.Clamp01(t / dur);
            if (a) a.alpha = Mathf.Lerp(a0, toA, k);
            if (b) b.alpha = Mathf.Lerp(b0, toB, k);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
        if (a) a.alpha = toA; if (b) b.alpha = toB;
    }
    
    // Editor sync
#if UNITY_EDITOR
    void OnValidate()
    {
        if (idleShared != null) foreach (var r in idleShared) r?.SyncFromEditorObject();
        if (idleAList  != null) foreach (var r in idleAList)  r?.SyncFromEditorObject();
        if (idleBList  != null) foreach (var r in idleBList)  r?.SyncFromEditorObject();
        if (activeList != null) foreach (var r in activeList) r?.SyncFromEditorObject();
    }
#endif
}