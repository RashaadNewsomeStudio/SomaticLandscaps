using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Networking;
using Klak.Hap;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class ArtworkController : MonoBehaviour
{
    // ---- Safety: defer RenderTexture destruction to avoid GPU-thread races ----
    private readonly List<RenderTexture> _rtToDestroy = new List<RenderTexture>();
    private void ScheduleDestroy(RenderTexture rt)
    {
        if (rt == null) return;
        try { rt.Release(); } catch (Exception e) { Debug.LogWarning($"[ArtworkController] Release error: {e.Message}"); }
        lock (_rtToDestroy) { _rtToDestroy.Add(rt); }
    }
    private void LateUpdate()
    {
        lock (_rtToDestroy)
        {
            if (_rtToDestroy.Count > 0)
            {
                for (int i = 0; i < _rtToDestroy.Count; i++)
                {
                    var r = _rtToDestroy[i];
                    if (r != null) try { Destroy(r); } catch (Exception e) { Debug.LogWarning($"[ArtworkController] Destroy error: {e.Message}"); }
                }
                _rtToDestroy.Clear();
            }
        }
    }
    // prevent overlapping idle prepares (guard against concurrent GPU work)
    private bool _isPreparingA;
    private bool _isPreparingB;

    // -------------------------
    //  StreamingAssets wrapper
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
            var assetAbs    = Path.GetFullPath(Path.Combine(projectRoot, assetPath)).Replace('\\', '/');
            if (!assetAbs.StartsWith(saRootAbs, StringComparison.Ordinal))
            {
                Debug.LogWarning($"[ArtworkController] {assetPath} is not under StreamingAssets.");
                return;
            }
            var rel = assetAbs.Substring(saRootAbs.Length).TrimStart('/');
            rel = rel.StartsWith("/", StringComparison.Ordinal) ? rel.Substring(1) : rel;
            relativePath = rel;
        }
    #endif
    }

    static bool HasAnyValid(StreamingAssetRef[] arr)
    {
        if (arr == null || arr.Length == 0) return false;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] != null && !string.IsNullOrEmpty(arr[i].relativePath))
                return true;
        return false;
    }

    static string GetRel(StreamingAssetRef r) => r == null ? null : r.relativePath;

    // -------------------------
    //  Config (JSON-driven)
    // -------------------------
    [Serializable]
    private class ArtworkConfigData
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

        // Music loader controls
        public bool   AutoLoadMusicFromStreaming = true;
        public string MusicSubfolder = "Music";

        // NEW: enable/disable startup panels (intro + screen adjustment)
        public bool EnableStartupPanels = true;
    }

    int _cfgW = 1920, _cfgH = 1080;

    // -------------------------
    //  Behavior toggles
    // -------------------------
    [Header("BEHAVIOR")]
    [SerializeField] private bool ignoreAmbientWhileActive = true; // true => ignore OSC 0 during Active (non-interruptible)
    private bool _startupPanelsEnabled = true; // driven by config.json

    // -------------------------
    //  External control hooks
    // -------------------------
    /// <summary>
    /// External trigger (OSC / programmatic).
    ///   value == 1 → go Active (only if currently in Idle).
    ///   value == 0 → go Ambient (only if currently Active) — may be ignored if ignoreAmbientWhileActive == true.
    ///   any other value → ignored.
    /// </summary>
    public void ReceiveActive(int value)
    {
        if (value == 1)
        {
            if (inIdle && !activeRunning)
            {
                ControllerMain.LogStep("ReceiveActive(1) => ACTIVE (from idle)");
                SetActive(true);
            }
            else
            {
                ControllerMain.LogStep("ReceiveActive(1) ignored — already active or transitioning.");
            }
        }
        else if (value == 0)
        {
            if (activeRunning)
            {
                if (ignoreAmbientWhileActive)
                {
                    ControllerMain.LogStep("ReceiveActive(0) ignored — IgnoreAmbientWhileActive=true (non-interruptible Active).");
                    return;
                }

                ControllerMain.LogStep("ReceiveActive(0) => AMBIENT (return)");
                SetActive(false);
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

    /// <summary>
    /// Core state setter.
    /// on == true  → start Active ONLY if currently idle.
    /// on == false → return to Ambient ONLY if currently active.
    /// </summary>
    public void SetActive(bool on)
    {
        if (on)
        {
            if (activeRunning || !inIdle)
            {
                ControllerMain.LogStep("SetActive(true) ignored — already active or not in idle.");
                return;
            }
            TriggerActiveSequence("External/Level=1");
        }
        else
        {
            if (!activeRunning)
            {
                ControllerMain.LogStep("SetActive(false) ignored — already ambient.");
                return;
            }
            ControllerMain.LogStep("SetActive(false) → returning to Ambient.");
            StartForceReturnToIdle();
        }
    }

    public void ReturnToIdle() => SetActive(false);

    // -------------------------
    //  External controller paths
    // -------------------------
    private ControllerMain _controllerMain;
    private string _externalAmbientPath;
    private string _externalActivePath;
    private string _externalAudioPath;

    // -------------------------
    //  Optional startup panels
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
    //  HAP: player/targets
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
    //  Robustness / open guards
    // -------------------------
    [Header("ROBUSTNESS")]
    [Min(0.05f)] public float  hapOpenTimeout = 3.0f;  // FIX 2: Increased from 1.5s to prevent false negatives
    [Min(0.0f)]  public float  hapMinPlayheadAdvance = 0.02f;
    [Min(0.05f)] public float  hapPlayheadGuard = 0.6f;
    [Min(1)]     public int    hapMaxAttemptsPerPick = 3;  // FIX 1: Reduced attempts, use retry-with-delay instead
    [Min(0.1f)]  public float  hapRetryDelay = 0.5f;       // FIX 1: Delay between retries of same file
    [Min(2)]     public int    hapQuarantineAfterFailures = 3;  // FIX 1: Blacklist after N failures

    // -------------------------
    //  UI / Control / Debug
    // -------------------------
    [Header("Trigger (optional)")]
    public Button             activeButton;  // UI button to trigger Active (one-way)
    public Button             ambientButton; // UI button to return to Ambient (one-way)

    [Header("RUNTIME TUNING PANEL (optional)")]
    public GameObject         tuningPanel;
    public Slider             sIdleFade, sIdlePrepareLead, sIdleMinFade, sActiveFadeIn, sActiveFadeOut, sReturnFade, sReturnBlackHold, sMusicRampIn, sMusicFadeIn, sMusicRampOut;
    public Toggle             tRandomizeIdleStartOnReturn, tMusicSyncWithActiveFade;
    public TMP_Text           vIdleFade, vIdlePrepareLead, vIdleMinFade, vActiveFadeIn, vActiveFadeOut, vReturnFade, vReturnBlackHold, vMusicRampIn, vMusicFadeIn, vMusicRampOut;
    public KeyCode            panelToggleKey = KeyCode.F1;

    // -------------------------
    //  State
    // -------------------------
    private bool              usingA = true, inIdle = true, activeRunning = false;
    private int               lastIdleIndexA = -1, lastIdleIndexB = -1, lastActiveIndex = -1, lastMusicIndex = -1;
    private bool              _idlePrimedFromReturn = false, _returnStartIsA = true, _returnStartWithA = true;
    private Coroutine         _standbyDotsRoutine = null, _idleLoopRoutine, _activeRoutine;
    private Coroutine         _prepIdleACo, _prepIdleBCo, _prepActiveCo;
    private readonly HashSet<HapPlayer> _openInFlight = new HashSet<HapPlayer>();
    private RenderTexture     _ownIdleA, _ownIdleB, _ownActive;
    private List<int>         _cycleIdleA = new List<int>(), _cycleIdleB = new List<int>(), _cycleIdleShared = new List<int>(), _cycleActive = new List<int>(), _cycleMusic = new List<int>();
    private HashSet<int>      _badIdleA = new HashSet<int>(), _badIdleB = new HashSet<int>(), _badIdleShared = new HashSet<int>(), _badActive = new HashSet<int>();
    // FIX 1: Track retry state for prepare storm elimination
    private int _lastAttemptedIdleA = -1, _lastAttemptedIdleB = -1;
    private int _failureCountIdleA = 0, _failureCountIdleB = 0;
    private Dictionary<int, int> _quarantineIdleA = new Dictionary<int, int>(), _quarantineIdleB = new Dictionary<int, int>(), _quarantineIdleShared = new Dictionary<int, int>();
    private readonly List<AudioClip> _loadedMusic = new List<AudioClip>();

#if UNITY_EDITOR
    void OnValidate()
    {
        if (idleShared != null) foreach (var r in idleShared) r?.SyncFromEditorObject();
        if (idleAList  != null) foreach (var r in idleAList)  r?.SyncFromEditorObject();
        if (idleBList  != null) foreach (var r in idleBList)  r?.SyncFromEditorObject();
        if (activeList != null) foreach (var r in activeList) r?.SyncFromEditorObject();
    }
#endif

    // -------------------------
    //  Lifecycle
    // -------------------------
    void Awake()
    {
        _controllerMain = FindFirstObjectByType<ControllerMain>();
        if (_controllerMain != null)
        {
            _externalAmbientPath = _controllerMain.AmbientStreamPath;
            _externalActivePath  = _controllerMain.ActiveStreamPath;
            _externalAudioPath   = _controllerMain.AudioPath;
            ControllerMain.LogStep($"ArtworkController Awake: Ambient='{_externalAmbientPath ?? "-"}', Active='{_externalActivePath ?? "-"}', Audio='{_externalAudioPath ?? "-"}'");
        }
        else
        {
            Debug.LogWarning("[ArtworkController] No ControllerMain found — using StreamingAssets.");
        }

        if (TryLoadArtworkConfig(out var _cfg)) ApplyArtworkConfig(_cfg);

        AutoPopulateFromStreamingAssets();
        AutoDiscoverVideos();

        Application.runInBackground = true;
        EnsureRTs();
        WirePlayersToRTs();
        PrimeCanvasGroups();
    }

    void Start()
    {
        EnsureMusicSourceConfigured();

        if (autoLoadMusicFromStreaming)
            StartCoroutine(LoadMusicFromConfiguredFolder());

        if (activeButton)
            activeButton.onClick.AddListener(() =>
            {
                if (inIdle && !activeRunning) SetActive(true);
            });

        if (ambientButton)
            ambientButton.onClick.AddListener(() =>
            {
                // Respect the same non-interruptible setting for UI clicks
                if (activeRunning && ignoreAmbientWhileActive) return;
                if (activeRunning) SetActive(false);
            });

        InitTuningPanel();

        StopAllCoroutines();

        // NEW: allow skipping intro + adjust panels from config.json
        if (_startupPanelsEnabled)
        {
            StartCoroutine(StartupSequenceNoMenu()); // intro → adjust → idle
        }
        else
        {
            // Make sure panels are hidden if disabled
            if (introPanel)  introPanel.SetActive(false);
            if (adjustPanel) adjustPanel.SetActive(false);
            StartCoroutine(Co_StartIdleNow());       // go directly to idle
        }
    }

    void Update()
    {
        if (tuningPanel && panelToggleKey != KeyCode.None && Input.GetKeyDown(panelToggleKey))
        {
            bool newState = !tuningPanel.activeSelf;
            tuningPanel.SetActive(newState);
            if (newState) RefreshSlidersFromVars();
        }
    }

    // -------------------------
    //  Config I/O
    // -------------------------
    bool TryLoadArtworkConfig(out ArtworkConfigData cfg)
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

    void ApplyArtworkConfig(ArtworkConfigData c)
    {
        if (c == null) return;

        _cfgW = Mathf.Max(16, c.ResolutionWidth);
        _cfgH = Mathf.Max(16, c.ResolutionHeight);

        // FIX: Inject resolution into ForceSpoutTexture BEFORE creating RTs
        var spouter = FindFirstObjectByType<ForceSpoutTexture>();
        if (spouter != null)
        {
            spouter.Initialize(_cfgW, _cfgH);
            Debug.Log($"[ArtworkController] Configured Spout to {_cfgW}x{_cfgH}");
        }

        RecreateRTIfMismatch(ref idleRT_A, ref _ownIdleA, "IdleA_RT", _cfgW, _cfgH);
        RecreateRTIfMismatch(ref idleRT_B, ref _ownIdleB, "IdleB_RT", _cfgW, _cfgH);
        RecreateRTIfMismatch(ref activeRT,  ref _ownActive, "Active_RT", _cfgW, _cfgH);
        WirePlayersToRTs();

        idleFade                   = Mathf.Max(0.01f, c.IdleFadeDuration);
        idlePrepareLead            = Mathf.Max(0f,     c.IdlePrepareLead);
        idleMinFade                = Mathf.Max(0.01f,  c.IdleMinFade);
        crossfadeIdle              = c.CrossfadeIdle;

        activeFadeIn               = Mathf.Max(0.05f,  c.ActiveFadeIn);
        activeFadeOut              = Mathf.Max(0.05f,  c.ActiveFadeOut);
        activeUseFixedWindow       = c.ActiveUseFixedWindow;
        activeFixedMidHoldSeconds  = Mathf.Max(0f,     c.ActiveFixedMidHoldSeconds);
        activeEndHoldSeconds       = Mathf.Max(0f,     c.ActiveEndHoldSeconds);

        returnFade                 = Mathf.Max(0.05f,  c.ReturnFade);
        returnBlackHold            = Mathf.Max(0f,     c.ReturnBlackHold);
        randomizeIdleStartOnReturn = c.RandomizeIdleStartOnReturn;

        musicVolume                = Mathf.Clamp01(c.MusicVolume);
        musicRampIn                = Mathf.Max(0f,     c.MusicRampIn);
        musicFadeIn                = Mathf.Max(0f,     c.MusicFadeIn);
        musicRampOut               = Mathf.Max(0f,     c.MusicRampOut);
        musicSyncWithActiveFade    = c.MusicSyncWithActiveFade;

        panelToggleKey             = (KeyCode)c.PanelToggleKey;

        // Apply behavior toggle from JSON
        ignoreAmbientWhileActive   = c.IgnoreAmbientWhileActive;

        // Loader config
        autoLoadMusicFromStreaming = c.AutoLoadMusicFromStreaming;
        musicSubfolder             = string.IsNullOrEmpty(c.MusicSubfolder) ? musicSubfolder : c.MusicSubfolder;

        // NEW: startup panels flag
        _startupPanelsEnabled      = c.EnableStartupPanels;

        RefreshSlidersFromVars();
        ControllerMain.LogStep("[ArtworkController] Applied ArtworkConfig.json");
    }

    void RecreateRTIfMismatch(ref RenderTexture rt, ref RenderTexture owned, string name, int w, int h)
    {
        var target = rt ? rt : owned;
        if (!target || target.width != w || target.height != h)
        {
            if (owned) { try { owned.Release(); } catch {} ScheduleDestroy(owned); owned = null; }
            var newRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                antiAliasing = 1
            };
            newRT.Create();
            owned = newRT;
            rt = rt ? rt : owned;
            if (name.StartsWith("IdleA", StringComparison.Ordinal)) { idleRT_A = owned; if (idleImgA) idleImgA.texture = idleRT_A; }
            else if (name.StartsWith("IdleB", StringComparison.Ordinal)) { idleRT_B = owned; if (idleImgB) idleImgB.texture = idleRT_B; }
            else { activeRT = owned; if (activeImg) activeImg.texture = activeRT; }
        }
    }

    // -------------------------
    //  Media discovery
    // -------------------------
    private void AutoPopulateFromStreamingAssets()
    {
        bool needAmbient = !HasAnyValid(idleAList) && !HasAnyValid(idleBList) && !HasAnyValid(idleShared);
        bool needActive  = !HasAnyValid(activeList);
        if (!needAmbient && !needActive) return;

        string saRoot = Application.streamingAssetsPath.Replace('\\', '/');

        if (needAmbient)
        {
            string ambientDir = Path.Combine(saRoot, "Ambient").Replace('\\', '/');
            if (Directory.Exists(ambientDir))
            {
                var files = Directory.GetFiles(ambientDir, "*.mov");
                var temp = new List<StreamingAssetRef>();
                foreach (var f in files)
                    temp.Add(new StreamingAssetRef { relativePath = "Ambient/" + Path.GetFileName(f) });
                if (temp.Count > 0) idleShared = temp.ToArray();
            }
        }

        if (needActive)
        {
            string activeDir = Path.Combine(saRoot, "Active").Replace('\\', '/');
            if (Directory.Exists(activeDir))
            {
                var files = Directory.GetFiles(activeDir, "*.mov");
                var temp = new List<StreamingAssetRef>();
                foreach (var f in files)
                    temp.Add(new StreamingAssetRef { relativePath = "Active/" + Path.GetFileName(f) });
                if (temp.Count > 0) activeList = temp.ToArray();
            }
        }
    }

    private void AutoDiscoverVideos()
    {
        var idleSharedList = new List<StreamingAssetRef>(idleShared ?? new StreamingAssetRef[0]);
        var activeListList = new List<StreamingAssetRef>(activeList ?? new StreamingAssetRef[0]);

        var existingIdle = new HashSet<string>(idleSharedList.Select(r => Path.GetFileName(GetRel(r) ?? "")), StringComparer.OrdinalIgnoreCase);
        var existingAct  = new HashSet<string>(activeListList.Select(r => Path.GetFileName(GetRel(r) ?? "")), StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(_externalAmbientPath) && Directory.Exists(_externalAmbientPath))
            TryAppendMovsFromFolder(_externalAmbientPath, idleSharedList, existingIdle, null);
        else
        {
            var saAmbient = Path.Combine(Application.streamingAssetsPath, "Ambient");
            if (Directory.Exists(saAmbient))
                TryAppendMovsFromFolder(saAmbient, idleSharedList, existingIdle, "Ambient");
        }

        if (!string.IsNullOrEmpty(_externalActivePath) && Directory.Exists(_externalActivePath))
            TryAppendMovsFromFolder(_externalActivePath, activeListList, existingAct, null);
        else
        {
            var saActive = Path.Combine(Application.streamingAssetsPath, "Active");
            if (Directory.Exists(saActive))
                TryAppendMovsFromFolder(saActive, activeListList, existingAct, "Active");
        }

        idleShared = idleSharedList.ToArray();
        activeList = activeListList.ToArray();

        ControllerMain.LogStep($"Auto-discovered media: Idle={idleShared.Length}, Active={activeList.Length}");
    }

    private void TryAppendMovsFromFolder(string folderAbs, List<StreamingAssetRef> target, HashSet<string> existingNames, string relativeBaseForSA)
    {
        string[] files;
        try
        {
            var f1 = Directory.GetFiles(folderAbs, "*.mov");
            var f2 = Directory.GetFiles(folderAbs, "*.MOV");
            files = f1.Concat(f2).Distinct().ToArray();
        }
        catch
        {
            try { files = Directory.GetFiles(folderAbs, "*.mov"); }
            catch { return; }
        }

        foreach (var abs in files)
        {
            var name = Path.GetFileName(abs);
            if (string.IsNullOrEmpty(name) || existingNames.Contains(name)) continue;

            var r = new StreamingAssetRef
            {
                relativePath = string.IsNullOrEmpty(relativeBaseForSA)
                    ? name
                    : relativeBaseForSA.Replace('\\', '/').TrimEnd('/') + "/" + name
            };

            target.Add(r);
            existingNames.Add(name);
        }
    }

    // -------------------------
    //  Startup (no menu)
    // -------------------------
    private IEnumerator StartupSequenceNoMenu()
    {
        if (introPanel && introGroup)
        {
            introPanel.SetActive(true);
            if (introText1) introText1.gameObject.SetActive(false);
            if (introText2) introText2.gameObject.SetActive(false);
            if (introText3) introText3.gameObject.SetActive(false);
            if (introText4) introText4.gameObject.SetActive(true);
            introGroup.alpha = 0f;
            yield return FadeOne(introGroup, 0f, 1f, introFadeIn);
            if (introText1) { introText1.gameObject.SetActive(true); yield return WaitUnscaled(introLineStep); }
            if (introText2) { introText2.gameObject.SetActive(true); yield return WaitUnscaled(introLineStep); }
            if (introText3) { introText3.gameObject.SetActive(true); yield return WaitUnscaled(introLineStep); }
            if (introText4)  introText4.gameObject.SetActive(true);
            yield return WaitUnscaled(introHold);
            yield return FadeOne(introGroup, introGroup.alpha, 0f, introFadeOut);
            introPanel.SetActive(false);
        }

        if (adjustPanel && adjustGroup)
        {
            adjustPanel.SetActive(true);
            adjustGroup.alpha = 0f;
            if (adjustText) adjustText.text = standbyBaseText;
            if (_standbyDotsRoutine != null) StopCoroutine(_standbyDotsRoutine);
            _standbyDotsRoutine = StartCoroutine(AnimateStandbyDots(adjustText, standbyBaseText, standbyDotStep));
            var imgRt = adjustImage ? adjustImage.rectTransform : null;
            var startScale = Vector3.one * Mathf.Clamp(adjustStartScale, 0.01f, 1f);
            if (imgRt) imgRt.localScale = startScale;
            yield return FadeOne(adjustGroup, 0f, 1f, adjustFadeIn);

            float t = 0f, dur = Mathf.Max(0.01f, adjustScaleDuration);
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / dur);
                if (imgRt) imgRt.localScale = Vector3.LerpUnclamped(startScale, Vector3.one, k);
                yield return null;
            }

            if (_standbyDotsRoutine != null) { StopCoroutine(_standbyDotsRoutine); _standbyDotsRoutine = null; }
            if (adjustText) adjustText.text = standbyBaseText + "...";
            yield return FadeOne(adjustGroup, adjustGroup.alpha, 0f, adjustFadeOut);
            adjustPanel.SetActive(false);
        }

        yield return Co_StartIdleNow();
    }

    private IEnumerator AnimateStandbyDots(TMP_Text label, string baseWord, float step)
    {
        if (!label) yield break;
        int i = 0; float wait = Mathf.Max(0.05f, step);
        while (true)
        {
            int dots = i % 4;
            label.text = dots == 0 ? baseWord : baseWord + new string('.', dots);
            i++; yield return WaitUnscaled(wait);
        }
    }

    private IEnumerator Co_StartIdleNow()
    {
        if (activeButton) activeButton.interactable = true;

        inIdle = true;
        usingA = true;

        // CRITICAL FIX: Load videos SEQUENTIALLY to prevent VRAM exhaustion
        // Load idleA first (current visible video)
        if (_prepIdleACo != null) StopCoroutine(_prepIdleACo);
        _prepIdleACo = StartCoroutine(TryPrepareIdleForSide(idleA, true));
        yield return _prepIdleACo; // WAIT for idleA to finish loading
        
        // FIX: Yield to keep Windows responsive during heavy operations
        yield return null;
        
        // REMOVED: GC.Collect in the middle of playback causes massive stutters.
        // Moved to end-of-active sequences only.
        yield return null;
        
        // THEN load idleB (standby video) - sequential, not parallel
        if (_prepIdleBCo != null) StopCoroutine(_prepIdleBCo);
        _prepIdleBCo = StartCoroutine(TryPrepareIdleForSide(idleB, false));
        yield return _prepIdleBCo; // WAIT for idleB to finish loading

        if (idleGA) idleGA.alpha = 1f;
        if (idleGB) idleGB.alpha = 0f;

        if (_idleLoopRoutine != null) StopCoroutine(_idleLoopRoutine);
        _idleLoopRoutine = StartCoroutine(IdleLoopSmooth());

        ControllerMain.LogStep("Idle loop started (sequential video load complete).");
        yield break;
    }

    private IEnumerator IdleLoopSmooth()
    {
        if (!HasIdlePaths(true) && !HasIdlePaths(false))
        { Debug.LogWarning("[ArtworkController] No idle files assigned."); yield break; }

        if (_idlePrimedFromReturn)
        {
            usingA = _returnStartIsA;
            if (usingA) { if (idleGA) idleGA.alpha = 1f; if (idleGB) idleGB.alpha = 0f; }
            else        { if (idleGA) idleGA.alpha = 0f; if (idleGB) idleGB.alpha = 1f; }
            _idlePrimedFromReturn = false;
        }

        inIdle = true;

        if (!crossfadeIdle)
        {
            if (_prepIdleACo != null) StopCoroutine(_prepIdleACo);
            _prepIdleACo = StartCoroutine(TryPrepareIdleForSide(idleA, true));
            if (idleGA) idleGA.alpha = 1f;
            if (idleGB) idleGB.alpha = 0f;
            yield break;
        }

        while (inIdle)
        {
            var active = usingA ? idleA : idleB;
            var standby = usingA ? idleB : idleA;
            var gAct = usingA ? idleGA : idleGB;
            var gStd = usingA ? idleGB : idleGA;

            float len = ValidDuration(active);
            float targetFade = Mathf.Clamp(idleFade, idleMinFade, len * 0.9f);
            float tPrepare   = Mathf.Max(0f, len - (targetFade + idlePrepareLead));
            float tFadeStart = Mathf.Max(0f, len - targetFade);

            yield return WaitUntilHapTime(active, tPrepare);

            if (usingA)
            {
                if (_prepIdleBCo != null) StopCoroutine(_prepIdleBCo);
                _prepIdleBCo = StartCoroutine(TryPrepareIdleForSide(standby, false));
            }
            else
            {
                if (_prepIdleACo != null) StopCoroutine(_prepIdleACo);
                _prepIdleACo = StartCoroutine(TryPrepareIdleForSide(standby, true));
            }

            if (gStd) gStd.alpha = 0f;

            yield return WaitUntilHapTime(active, tFadeStart);

            float remaining = Mathf.Max(0.01f, len - active.time);
            float fadeDur = Mathf.Min(targetFade, remaining);

            float t = 0f, a0 = gAct ? gAct.alpha : 0f, b0 = gStd ? gStd.alpha : 0f;
            while (t < fadeDur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / fadeDur);
                if (gAct) gAct.alpha = Mathf.Lerp(a0, 0f, k);
                if (gStd) gStd.alpha = Mathf.Lerp(b0, 1f, k);
                yield return null;
            }

            if (gAct) gAct.alpha = 0f;
            if (gStd) gStd.alpha = 1f;

            usingA = !usingA;
            ControllerMain.LogStep("Idle crossfade step.");
        }
    }

    // -------------------------
    //  Active sequence
    // -------------------------
    public void OnActiveButtonClicked()
    {
        if (inIdle && !activeRunning)
            SetActive(true);
    }

    private void TriggerActiveSequence(string sourceTag)
    {
        _returnStartWithA = usingA;

        if (_idleLoopRoutine != null) { StopCoroutine(_idleLoopRoutine); _idleLoopRoutine = null; }
        if (_activeRoutine != null)   { StopCoroutine(_activeRoutine);   _activeRoutine = null; }

        _activeRoutine = StartCoroutine(ActiveSequence());
        ControllerMain.LogStep($"Active sequence triggered (source={sourceTag}).");
    }

    private IEnumerator ActiveSequence()
    {
        activeRunning = true;
        inIdle = false;

        // FIX 4: Stop any pending ambient prepare coroutines
        if (_prepIdleACo != null) { StopCoroutine(_prepIdleACo); _prepIdleACo = null; }
        if (_prepIdleBCo != null) { StopCoroutine(_prepIdleBCo); _prepIdleBCo = null; }

        float idleOut = Mathf.Max(0.01f, idleToActiveFadeOut);
        yield return FadeTwo(idleGA, idleGB, 0f, 0f, idleOut);

        if (activeGroup) activeGroup.alpha = 0f;

        bool opened = false;
        if (_prepActiveCo != null) StopCoroutine(_prepActiveCo);
        _prepActiveCo = StartCoroutine(TryOpenValidActive(activeHP, r => opened = r));
        yield return _prepActiveCo;

        if (!opened)
        {
            yield return FadeTwo(idleGA, idleGB, 1f, 0f, returnFade);
            activeRunning = false;
            inIdle = true;
            _idleLoopRoutine = StartCoroutine(IdleLoopSmooth());
            ControllerMain.LogStep("Active open failed; returning to idle.");
            yield break;
        }

        StartCoroutine(Co_StartMusicAfterDelay(musicRampIn));

        float fin = Mathf.Max(0.05f, activeFadeIn);
        ControllerMain.LogStep($"Active fade-in start (dur={fin:0.00}s)");
        yield return FadeOne(activeGroup, 0f, 1f, fin);

        float dur  = Mathf.Max(0.2f, (float)activeHP.streamDuration);
        float fout = Mathf.Max(0.05f, activeFadeOut);

        float fadeOutStart = activeUseFixedWindow
            ? Mathf.Min(fin + Mathf.Max(0f, activeFixedMidHoldSeconds), Mathf.Max(0f, dur - fout))
            : Mathf.Max(0f, Mathf.Max(dur - fout, fin <= dur ? fin : dur * 0.5f));

        ControllerMain.LogStep($"Active fade-out start scheduled at t={fadeOutStart:0.00}s (dur={fout:0.00}s, total={dur:0.00}s)");

        yield return WaitUntilHapTime(activeHP, fadeOutStart);

        StartCoroutine(RampDownToZero(Mathf.Max(0.01f, musicRampOut)));

        yield return FadeOne(activeGroup, 1f, 0f, fout);

        if (!activeUseFixedWindow && activeEndHoldSeconds > 0f)
            yield return new WaitForSeconds(activeEndHoldSeconds);

        usingA = _returnStartWithA;

        var gStart = usingA ? idleGA : idleGB;

        if (randomizeIdleStartOnReturn)
        {
            // CRITICAL FIX: Force GC ONLY if NOT in high-performance mode
            if (VideoMemoryFix.Instance == null || !VideoMemoryFix.Instance.IsHighPerformance)
            {
                Resources.UnloadUnusedAssets();
                System.GC.Collect();
            }
            
            var hp = usingA ? idleA : idleB;
            if (usingA)
            {
                if (_prepIdleACo != null) StopCoroutine(_prepIdleACo);
                _prepIdleACo = StartCoroutine(TryPrepareIdleForSide(hp, true));
                yield return _prepIdleACo; // WAIT for load to complete
            }
            else
            {
                if (_prepIdleBCo != null) StopCoroutine(_prepIdleBCo);
                _prepIdleBCo = StartCoroutine(TryPrepareIdleForSide(hp, false));
                yield return _prepIdleBCo; // WAIT for load to complete
            }
        }

        if (returnBlackHold > 0f) yield return WaitUnscaled(returnBlackHold);
        if (gStart) yield return FadeOne(gStart, 0f, 1f, Mathf.Max(0.05f, returnFade));

        _returnStartIsA = usingA;
        _idlePrimedFromReturn = true;

        activeRunning = false;
        inIdle = true;
        _idleLoopRoutine = StartCoroutine(IdleLoopSmooth());

        ControllerMain.LogStep("Returned to idle.");
    }

    // External cancel path respecting fades
    private void StartForceReturnToIdle()
    {
        if (_activeRoutine != null) { StopCoroutine(_activeRoutine); _activeRoutine = null; }
        StartCoroutine(ForceReturnToIdleNow());
    }

    private IEnumerator ForceReturnToIdleNow()
    {
        StartCoroutine(RampDownToZero(Mathf.Max(0.01f, musicRampOut)));

        float fout = Mathf.Max(0.05f, activeFadeOut);
        float currentAlpha = activeGroup ? activeGroup.alpha : 0f;

        if (activeGroup) yield return FadeOne(activeGroup, currentAlpha, 0f, fout);

        usingA = _returnStartWithA;

        var gStart = usingA ? idleGA : idleGB;

        if (returnBlackHold > 0f) yield return WaitUnscaled(returnBlackHold);
        if (gStart) yield return FadeOne(gStart, 0f, 1f, Mathf.Max(0.05f, returnFade));

        _returnStartIsA = usingA;
        _idlePrimedFromReturn = true;

        activeRunning = false;
        inIdle = true;
        _idleLoopRoutine = StartCoroutine(IdleLoopSmooth());

        ControllerMain.LogStep("External cancel: returned to idle.");
    }

    // -------------------------
    //  Music (Active only)
    // -------------------------
    private IEnumerator LoadMusicFromConfiguredFolder()
    {
        string dir = null;
        if (!string.IsNullOrEmpty(_externalAudioPath) && Directory.Exists(_externalAudioPath))
            dir = _externalAudioPath;
        else
            dir = Path.Combine(Application.streamingAssetsPath, musicSubfolder ?? "Music");

        if (!Directory.Exists(dir)) yield break;

        string[] files;
        try
        {
            var f1 = Directory.GetFiles(dir, "*.wav");
            var f2 = Directory.GetFiles(dir, "*.WAV");
            files = f1.Concat(f2).Distinct().ToArray();
        }
        catch { yield break; }

        foreach (var file in files)
        {
            var uri = new Uri(file).AbsoluteUri;
            using (var req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return req.SendWebRequest();
#if UNITY_2020_3_OR_NEWER
                if (req.result != UnityWebRequest.Result.Success) continue;
#else
                if (req.isNetworkError || req.isHttpError) continue;
#endif
                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip != null)
                {
                    clip.name = Path.GetFileNameWithoutExtension(file);
                    _loadedMusic.Add(clip);
                }
            }
            yield return null;
        }

        Debug.Log($"[ArtworkController] Loaded {_loadedMusic.Count} .wav clip(s) from {dir}");
    }

    private IEnumerator Co_StartMusicAfterDelay(float delay)
    {
        int bankCount = (_loadedMusic.Count > 0) ? _loadedMusic.Count : (activeMusicClipsFallback?.Length ?? 0);
        if (!musicSource || bankCount == 0) yield break;

        delay = Mathf.Max(0f, delay);
        if (delay > 0f) yield return new WaitForSeconds(delay);

        int newLast;
        int mIdx = NextFromCycleFiltered(_cycleMusic, bankCount, lastMusicIndex, null, out newLast);
        lastMusicIndex = newLast;
        if (mIdx < 0) yield break;

        var chosen = (_loadedMusic.Count > 0) ? _loadedMusic[mIdx] : activeMusicClipsFallback[mIdx];

        EnsureMusicSourceConfigured();
        musicSource.Stop();
        musicSource.clip = chosen;
        musicSource.volume = 0f;
        musicSource.Play();

        float fadeDur = musicSyncWithActiveFade ? Mathf.Max(0.05f, activeFadeIn) : Mathf.Max(0f, musicFadeIn);
        yield return RampUpToCurrentMusicVolume(fadeDur);

        ControllerMain.LogStep($"Music start: '{musicSource.clip?.name}'");
    }

    void EnsureMusicSourceConfigured()
    {
        if (!musicSource) return;
        musicSource.playOnAwake = false;
        musicSource.loop = true;
        musicSource.mute = false;
        musicSource.spatialBlend = 0f;
        musicSource.volume = 0f;
    }

    private IEnumerator RampUpToCurrentMusicVolume(float dur)
    {
        if (!musicSource) yield break;
        dur = Mathf.Max(0f, dur);
        float start = musicSource.volume;
        if (dur <= 0f) { musicSource.volume = Mathf.Clamp01(musicVolume); yield break; }

        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            musicSource.volume = Mathf.Lerp(start, Mathf.Clamp01(musicVolume), Mathf.Clamp01(t / dur));
            yield return null;
        }
        musicSource.volume = Mathf.Clamp01(musicVolume);
    }

    private IEnumerator RampDownToZero(float dur)
    {
        if (!musicSource || !musicSource.isPlaying) yield break;

        dur = Mathf.Max(0.01f, dur);
        float start = musicSource.volume, t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            musicSource.volume = Mathf.Lerp(start, 0f, Mathf.Clamp01(t / dur));
            yield return null;
        }
        musicSource.Stop();
        musicSource.volume = Mathf.Clamp01(musicVolume);
        musicSource.clip = null;
        ControllerMain.LogStep($"Music fade OUT ({musicRampOut:0.00}s)");
    }

    // -------------------------
    //  HAP open/validate
    // -------------------------
    // Guard: only one open at a time per player, prevents GPU resource conflicts
    private IEnumerator ClaimOpen(HapPlayer hp)
    {
        // FIX: Add periodic yields every ~50ms to keep Windows responsive during waits
        float nextYield = Time.unscaledTime + 0.05f;
        while (hp != null && !_openInFlight.Add(hp))
        {
            yield return null;
            
            // Extra yield every 50ms to ensure Windows message pump stays active
            if (Time.unscaledTime >= nextYield)
            {
                yield return null;
                nextYield = Time.unscaledTime + 0.05f;
            }
        }
    }

    // FIX 5: Enhanced validation logging + FIX 2: Reordered Locking (Player -> Gate) to prevent Deadlocks
    private IEnumerator OpenAndValidate(HapPlayer hp, string relativePath, Action<bool> done)
    {
        if (hp == null || string.IsNullOrEmpty(relativePath))
        {
            ControllerMain.LogWarn($"[OpenAndValidate] NULL input: hp={hp}, path='{relativePath ?? "null"}'");
            done(false);
            yield break;
        }

        string filename = Path.GetFileName(relativePath);
        float startTime = Time.unscaledTime;

        // LOCK 1: Claim the Player (Local Lock)
        // Must happen BEFORE Global VRAM Lock to prevent deadlocks
        yield return ClaimOpen(hp);

        try
        {
            // LOCK 2: Claim VRAM Slot (Global Lock)
            VideoMemoryFix gate = VideoMemoryFix.Instance;
            if (gate != null)
                yield return gate.WaitForLoadSlot();

            try
            {
                bool success = false;
                bool wasValid = false;
                double finalDuration = 0;
                bool playheadAdvanced = false;

                // ----- 1. Try absolute path from ControllerMain (external folder) -----
                bool tryAbsolute = false;
                if (_controllerMain != null && !string.IsNullOrEmpty(filename))
                {
                    string baseFolder = (hp == activeHP) ? _externalActivePath : _externalAmbientPath;
                    if (!string.IsNullOrEmpty(baseFolder))
                    {
                        string abs = Path.Combine(baseFolder, filename);
                        if (File.Exists(abs))
                        {
                            tryAbsolute = true;
                            ControllerMain.LogStep($"[OpenAndValidate] Opening '{filename}' (LocalFileSystem: '{abs}')");
                            
                            hp.time  = 0f;
                            hp.speed = 1f;
                            hp.loop  = false;
                            hp.Open(abs, HapPlayer.PathMode.LocalFileSystem);

                            yield return ValidateLoad(hp, startTime, filename, (ok, valid, dur, adv) => 
                            { 
                                success = ok; wasValid = valid; finalDuration = dur; playheadAdvanced = adv; 
                            });

                            if (success)
                            {
                                done(true);
                                yield break; 
                            }
                        }
                    }
                }

                if (tryAbsolute && success) yield break; // Should be handled above

                // ----- 2. Fallback: StreamingAssets relative -----
                if (!PathExistsForOpen(relativePath, pathMode))
                {
                    ControllerMain.LogWarn($"[OpenAndValidate] Path not found: '{relativePath}' (mode={pathMode})");
                    done(false);
                    yield break;
                }

                ControllerMain.LogStep($"[OpenAndValidate] Opening '{filename}' (StreamingAssets: '{relativePath}')");
                
                hp.time  = 0f;
                hp.speed = 1f;
                hp.loop  = false;
                hp.Open(relativePath, pathMode);

                yield return ValidateLoad(hp, startTime, filename, (ok, valid, dur, adv) => 
                { 
                    success = ok; wasValid = valid; finalDuration = dur; playheadAdvanced = adv; 
                });

                done(success);
            }
            finally
            {
                // RELEASE LOCK 2
                if (gate != null)
                    gate.ReleaseLoadSlot();
            }
        }
        finally
        {
            // RELEASE LOCK 1
            _openInFlight.Remove(hp);
        }
    }

    private IEnumerator ValidateLoad(HapPlayer hp, float startTime, string filename, Action<bool, bool, double, bool> resultCallback)
    {
        bool success = false;
        bool wasValid = false;
        double finalDuration = 0;
        bool playheadAdvanced = false;

        float timeout = Mathf.Max(0.05f, hapOpenTimeout);
        float t = 0f;
        while (t < timeout)
        {
            t += Time.deltaTime;
            if (hp.isValid) { wasValid = true; finalDuration = hp.streamDuration; }
            if (hp.isValid && hp.streamDuration > 0.0) { success = true; break; }
            yield return null;
        }

        if (success)
        {
            float guard = Mathf.Max(0.05f, hapPlayheadGuard);
            double startT = hp.time;
            float g = 0f;
            while (g < guard)
            {
                g += Time.deltaTime;
                if (hp.time - startT >= hapMinPlayheadAdvance) { playheadAdvanced = true; break; }
                yield return null;
            }
        }

        float elapsed = Time.unscaledTime - startTime;
        if (success && playheadAdvanced)
            ControllerMain.LogStep($"[OpenAndValidate] SUCCESS '{filename}' (dur={finalDuration:0.00}s, open_time={elapsed:0.00}s)");
        else
            ControllerMain.LogWarn($"[OpenAndValidate] FAILED/STALL '{filename}' (success={success}, valid={wasValid}, dur={finalDuration:0.00}s, flow={playheadAdvanced})");

        resultCallback(success && playheadAdvanced, wasValid, finalDuration, playheadAdvanced);
    }

    // -------------------------
    //  Pickers / helpers
    // -------------------------
    // FIX 1: Eliminate prepare storms - retry same file with delay, quarantine after failures
    private IEnumerator TryPrepareIdleForSide(HapPlayer hp, bool forA)
    {
        // FIX 4: Early exit if active mode is running (no ambient prepares during active)
        if (!inIdle || activeRunning)
        {
            ControllerMain.LogStep(forA ? "Idle prepare A skipped (not idle)" : "Idle prepare B skipped (not idle)");
            yield break;
        }

        // Safety guard to avoid overlapping prepares on GPU resources
        if (forA) { if (_isPreparingA) yield break; _isPreparingA = true; }
        else      { if (_isPreparingB) yield break; _isPreparingB = true; }

        try
        {
            if (hp == null) yield break;

            // FIX 1: Get or reuse last attempted index
            int attemptIdx = forA ? _lastAttemptedIdleA : _lastAttemptedIdleB;
            int failureCount = forA ? _failureCountIdleA : _failureCountIdleB;
            var quarantine = forA ? _quarantineIdleA : _quarantineIdleB;
            StreamingAssetRef[] pool;
            int idx;
            HashSet<int> badSet;

            // FIX 1: Retry same file with delay if previous attempt failed
            int maxAttempts = Mathf.Max(1, hapMaxAttemptsPerPick);
            for (int tries = 0; tries < maxAttempts; tries++)
            {
                // Pick new file only if no previous attempt or previous succeeded
                if (attemptIdx < 0 || failureCount >= maxAttempts)
                {
                    var picked = PickIdlePath(forA, out pool, out idx, out badSet);
                    if (!picked || idx < 0 || idx >= pool.Length)
                    {
                        ControllerMain.LogWarn(forA ? "Idle prepare A: No valid files" : "Idle prepare B: No valid files");
                        yield break;
                    }
                    // Check quarantine
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
                    // Reuse last idx
                    PickIdlePath(forA, out pool, out _, out badSet);
                    idx = attemptIdx;
                }

                if (idx < 0 || idx >= pool.Length) { yield break; }

                string rel = GetRel(pool[idx]);
                if (string.IsNullOrEmpty(rel)) { yield break; }

                ControllerMain.LogStep($"Idle prepare {(forA ? "A" : "B")}: '{Path.GetFileName(rel)}' (attempt {tries + 1}/{maxAttempts}, failures so far={failureCount})");

                bool ok = false;
                yield return StartCoroutine(OpenAndValidate(hp, rel, r => ok = r));

                if (ok)
                {
                    hp.loop = true; hp.speed = 1f; hp.time = 0f;
                    // Reset tracking on success
                    if (forA) { _lastAttemptedIdleA = -1; _failureCountIdleA = 0; }
                    else { _lastAttemptedIdleB = -1; _failureCountIdleB = 0; }
                    yield break;
                }

                // Failed - increment failure count
                failureCount++;
                if (forA) { _lastAttemptedIdleA = attemptIdx; _failureCountIdleA = failureCount; }
                else { _lastAttemptedIdleB = attemptIdx; _failureCountIdleB = failureCount; }

                // FIX 1: Quarantine after N failures
                if (failureCount >= hapQuarantineAfterFailures)
                {
                    quarantine[idx] = failureCount;
                    badSet.Add(idx);
                    ControllerMain.LogWarn($"Idle prepare {(forA ? "A" : "B")}: QUARANTINED '{Path.GetFileName(rel)}' after {failureCount} failures");
                    attemptIdx = -1;  // Force new pick next time
                    failureCount = 0;
                    if (forA) { _lastAttemptedIdleA = -1; _failureCountIdleA = 0; }
                    else { _lastAttemptedIdleB = -1; _failureCountIdleB = 0; }
                    continue;  // Try new file immediately
                }

                // FIX 1: Delay before retry of same file
                ControllerMain.LogWarn($"Idle prepare {(forA ? "A" : "B")}: Failed, retrying same file after {hapRetryDelay:0.00}s delay");
                yield return new WaitForSeconds(hapRetryDelay);
            }

            ControllerMain.LogError($"Idle prepare {(forA ? "A" : "B")}: FAILED after {maxAttempts} attempts");
        }
        finally
        {
            if (forA) _isPreparingA = false; else _isPreparingB = false;
        }
    }

    private IEnumerator TryOpenValidActive(HapPlayer hp, Action<bool> result)
    {
        bool success = false;

        if (hp == null || activeList == null || activeList.Length == 0) { result(false); yield break; }

        int tries = 0;
        while (tries++ < Mathf.Max(1, hapMaxAttemptsPerPick))
        {
            int newLast;
            int idx = NextFromCycleFiltered(_cycleActive, activeList.Length, lastActiveIndex, _badActive, out newLast);
            lastActiveIndex = newLast;

            if (idx >= 0 && idx < activeList.Length)
            {
                string rel = GetRel(activeList[idx]);
                if (!string.IsNullOrEmpty(rel))
                {
                    string file = Path.GetFileName(rel);
                    ControllerMain.LogStep($"Active pick attempt: '{file}'");

                    bool ok = false;
                    yield return StartCoroutine(OpenAndValidate(hp, rel, r => ok = r));
                    if (ok)
                    {
                        hp.loop = false; hp.speed = 1f; hp.time = 0f;
                        ControllerMain.LogStep($"Active playing: '{file}' (dur={hp.streamDuration:0.00}s)");
                        success = true;
                        break;
                    }
                    ControllerMain.LogWarn($"Active open failed: '{file}'");
                    _badActive.Add(idx);
                }
            }
            yield return null;
        }

        result(success);
    }

    private bool HasIdlePaths(bool forA)
    {
        var list = forA ? idleAList : idleBList;
        if (HasAnyValid(list)) return true;
        return HasAnyValid(idleShared);
    }

    private bool HasActivePaths() => HasAnyValid(activeList);

    private bool PickIdlePath(bool forA, out StreamingAssetRef[] pool, out int idx, out HashSet<int> badSet)
    {
        pool = forA
            ? (HasAnyValid(idleAList) ? idleAList : idleShared)
            : (HasAnyValid(idleBList) ? idleBList : idleShared);

        if (pool == null || pool.Length == 0) { idx = -1; badSet = null; return false; }

        List<int> cycle = (pool == idleShared) ? _cycleIdleShared : (forA ? _cycleIdleA : _cycleIdleB);
        badSet = (pool == idleShared) ? _badIdleShared : (forA ? _badIdleA : _badIdleB);

        if (badSet.Count >= pool.Length) badSet.Clear();

        int lastUsed = forA ? lastIdleIndexA : lastIdleIndexB;

        int newLast;
        idx = NextFromCycleFiltered(cycle, pool.Length, lastUsed, badSet, out newLast);

        if (pool == idleShared) _cycleIdleShared = cycle;
        else if (forA) { _cycleIdleA = cycle; lastIdleIndexA = newLast; }
        else           { _cycleIdleB = cycle; lastIdleIndexB = newLast; }

        return idx >= 0;
    }

    static int NextFromCycleFiltered(List<int> cycle, int length, int lastUsed, HashSet<int> blacklist, out int newLastUsed)
    {
        newLastUsed = lastUsed;
        if (length <= 0) return -1;

        if (cycle == null) cycle = new List<int>(length);
        if (cycle.Count == 0)
        {
            cycle.Clear();
            for (int i = 0; i < length; i++)
                if (blacklist == null || !blacklist.Contains(i)) cycle.Add(i);
            if (cycle.Count == 0) for (int i = 0; i < length; i++) cycle.Add(i);

            FisherYatesShuffle(cycle);

            if (cycle.Count > 1 && lastUsed >= 0 && cycle[0] == lastUsed)
            {
                int j = UnityEngine.Random.Range(1, cycle.Count);
                (cycle[0], cycle[j]) = (cycle[j], cycle[0]);
            }
        }

        if (cycle.Count == 0) return -1;

        int pick = cycle[0];
        cycle.RemoveAt(0);
        newLastUsed = pick;
        return pick;
    }

    static void FisherYatesShuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // -------------------------
    //  HAP/Common helpers
    // -------------------------
    void EnsureRTs()
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
        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            antiAliasing = 1
        };
        rt.Create();
        return rt;
    }

    void WirePlayersToRTs()
    {
        if (idleA)    { idleA.targetTexture = idleRT_A;    idleA.targetRenderer = null; }
        if (idleB)    { idleB.targetTexture = idleRT_B;    idleB.targetRenderer = null; }
        if (activeHP) { activeHP.targetTexture = activeRT; activeHP.targetRenderer = null; }
    }

    void PrimeCanvasGroups()
    {
        if (idleGA)      idleGA.alpha = 0f;
        if (idleGB)      idleGB.alpha = 0f;
        if (activeGroup) activeGroup.alpha = 0f;
    }

    float ValidDuration(HapPlayer hp)
    {
        if (hp == null || !hp.isValid) return 5f;
        var d = (float)hp.streamDuration;
        return Mathf.Max(0.2f, d);
    }

    IEnumerator WaitUntilHapTime(HapPlayer hp, float targetTime)
    {
        if (hp == null) yield break;
        float guard = 0f;
        while (hp.time < targetTime && guard < 60f) { guard += Time.deltaTime; yield return null; }
    }

    private bool PathExistsForOpen(string relativePath, HapPlayer.PathMode mode)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
    #if UNITY_ANDROID && !UNITY_EDITOR
        return true;
    #else
        if (mode != HapPlayer.PathMode.StreamingAssets) return false;
        string abs = Path.Combine(Application.streamingAssetsPath, relativePath);
        try { abs = Path.GetFullPath(abs); } catch { return false; }
        return File.Exists(abs);
    #endif
    }

    // -------------------------
    //  UI fades/helpers
    // -------------------------
    private IEnumerator FadeOne(CanvasGroup g, float from, float to, float dur)
    {
        if (!g) yield break;
        dur = Mathf.Max(0.01f, dur);
        float t = 0f; g.alpha = from;
        while (t < dur) { t += Time.deltaTime; g.alpha = Mathf.Lerp(from, to, t / dur); yield return null; }
        g.alpha = to;
    }

    private IEnumerator FadeTwo(CanvasGroup a, CanvasGroup b, float toA, float toB, float dur)
    {
        dur = Mathf.Max(0.01f, dur);
        float t = 0f, a0 = a ? a.alpha : 0f, b0 = b ? b.alpha : 0f;
        while (t < dur)
        {
            t += Time.deltaTime; float k = Mathf.Clamp01(t / dur);
            if (a) a.alpha = Mathf.Lerp(a0, toA, k);
            if (b) b.alpha = Mathf.Lerp(b0, toB, k);
            yield return null;
        }
        if (a) a.alpha = toA; if (b) b.alpha = toB;
    }

    private IEnumerator WaitUnscaled(float s)
    {
        if (s <= 0f) yield break;
        float end = Time.unscaledTime + s;
        while (Time.unscaledTime < end) yield return null;
    }

    // -------------------------
    //  Tuning panel
    // -------------------------
    private void InitTuningPanel()
    {
        if (!tuningPanel) return;

        SetupSliderRange(sIdleFade, 0.05f, 10f);
        SetupSliderRange(sIdlePrepareLead, 0f, 5f);
        SetupSliderRange(sIdleMinFade, 0.01f, 3f);
        SetupSliderRange(sActiveFadeIn, 0.05f, 6f);
        SetupSliderRange(sActiveFadeOut, 0.05f, 6f);
        SetupSliderRange(sReturnFade, 0.05f, 6f);
        SetupSliderRange(sReturnBlackHold, 0f, 3f);
        SetupSliderRange(sMusicRampIn, 0f, 12f);
        SetupSliderRange(sMusicFadeIn, 0f, 12f);
        SetupSliderRange(sMusicRampOut, 0f, 6f);

        RefreshSlidersFromVars();
        WireSliderCallbacks();
    }

    private void SetupSliderRange(Slider s, float min, float max) { if (!s) return; s.minValue = min; s.maxValue = max; }

    private void RefreshSlidersFromVars()
    {
        SetSliderValue(sIdleFade,        idleFade,        vIdleFade,        "Idle Fade");
        SetSliderValue(sIdlePrepareLead, idlePrepareLead, vIdlePrepareLead, "Idle Prepare Lead");
        SetSliderValue(sIdleMinFade,     idleMinFade,     vIdleMinFade,     "Idle Min Fade");
        SetSliderValue(sActiveFadeIn,  activeFadeIn,  vActiveFadeIn,  "Active Fade In");
        SetSliderValue(sActiveFadeOut, activeFadeOut, vActiveFadeOut, "Active Fade Out");
        SetSliderValue(sReturnFade,      returnFade,      vReturnFade,      "Return Fade");
        SetSliderValue(sReturnBlackHold, returnBlackHold, vReturnBlackHold, "Return Black Hold");
        SetSliderValue(sMusicRampIn,  musicRampIn,  vMusicRampIn,  "Music Delay (Start)");
        SetSliderValue(sMusicFadeIn,  musicFadeIn,  vMusicFadeIn,  "Music Fade In");
        SetSliderValue(sMusicRampOut, musicRampOut, vMusicRampOut, "Music Ramp Out");

        if (tRandomizeIdleStartOnReturn) tRandomizeIdleStartOnReturn.isOn = randomizeIdleStartOnReturn;
        if (tMusicSyncWithActiveFade)    tMusicSyncWithActiveFade.isOn   = musicSyncWithActiveFade;
    }

    private void WireSliderCallbacks()
    {
        if (sIdleFade)        sIdleFade.onValueChanged.AddListener(v => { idleFade = Mathf.Max(0.01f, v); SetLabel(vIdleFade, "Idle Fade", v); });
        if (sIdlePrepareLead) sIdlePrepareLead.onValueChanged.AddListener(v => { idlePrepareLead = Mathf.Max(0f, v); SetLabel(vIdlePrepareLead, "Idle Prepare Lead", v); });
        if (sIdleMinFade)     sIdleMinFade.onValueChanged.AddListener(v => { idleMinFade = Mathf.Max(0.01f, v); SetLabel(vIdleMinFade, "Idle Min Fade", v); });
        if (sActiveFadeIn)    sActiveFadeIn.onValueChanged.AddListener(v => { activeFadeIn = Mathf.Max(0.05f, v); SetLabel(vActiveFadeIn, "Active Fade In", v); });
        if (sActiveFadeOut)   sActiveFadeOut.onValueChanged.AddListener(v => { activeFadeOut = Mathf.Max(0.05f, v); SetLabel(vActiveFadeOut, "Active Fade Out", v); });
        if (sReturnFade)      sReturnFade.onValueChanged.AddListener(v => { returnFade = Mathf.Max(0.05f, v); SetLabel(vReturnFade, "Return Fade", v); });
        if (sReturnBlackHold) sReturnBlackHold.onValueChanged.AddListener(v => { returnBlackHold = Mathf.Max(0f, v); SetLabel(vReturnBlackHold, "Return Black Hold", v); });
        if (sMusicRampIn)     sMusicRampIn.onValueChanged.AddListener(v => { musicRampIn = Mathf.Max(0f, v); SetLabel(vMusicRampIn, "Music Delay (Start)", v); });
        if (sMusicFadeIn)     sMusicFadeIn.onValueChanged.AddListener(v => { musicFadeIn = Mathf.Max(0f, v); SetLabel(vMusicFadeIn, "Music Fade In", v); });
        if (sMusicRampOut)    sMusicRampOut.onValueChanged.AddListener(v => { musicRampOut = Mathf.Max(0f, v); SetLabel(vMusicRampOut, "Music Ramp Out", v); });

        if (tRandomizeIdleStartOnReturn) tRandomizeIdleStartOnReturn.onValueChanged.AddListener(b => randomizeIdleStartOnReturn = b);
        if (tMusicSyncWithActiveFade)    tMusicSyncWithActiveFade.onValueChanged.AddListener(b => musicSyncWithActiveFade = b);
    }

    private void SetSliderValue(Slider s, float value, TMP_Text label, string name, string fmt = "0.00")
    { if (s) s.SetValueWithoutNotify(value); SetLabel(label, name, value, fmt); }

    private void SetLabel(TMP_Text label, string name, float value, string fmt = "0.00")
    { if (label) label.text = $"{name}: {value.ToString(fmt)}"; }
}