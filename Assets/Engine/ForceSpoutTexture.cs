using UnityEngine;
using UnityEngine.UI;        // RawImage + AspectRatioFitter
using Klak.Spout;

/// Fixed Spout texture output + adaptive fullscreen preview on any monitor.
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(SpoutSender))]
public class ForceSpoutTexture : MonoBehaviour
{
    [Header("Spout Output (fixed)")]
    public int spoutWidth  = 5280;
    public int spoutHeight = 1620;

    [Header("Preview / UX")]
    [Tooltip("Mirror the Spout RT to the window so operators can see it.")]
    public bool previewOnScreen = true;

    [Tooltip("Preserve aspect of the Spout texture (adds letterboxing/pillarboxing).")]
    public bool preserveAspect = true;

    [Tooltip("Create a dummy camera so Unity never shows 'No cameras rendering'.")]
    public bool hideNoCameraOverlay = true;

    private SpoutSender   sender;
    private Camera        cam;
    private RenderTexture spoutRT;

    // UI preview
    private Canvas   previewCanvas;
    private RawImage previewImage;
    private Camera   dummyDisplayCam;

    private bool _initialized = false;

    private void Awake()
    {
        sender = GetComponent<SpoutSender>();
        cam    = GetComponent<Camera>();
        
        // Wait for external init via ArtworkController. 
        // If not called by Start(), we auto-init with inspector values.
    }

    private void Start()
    {
        if (!_initialized)
            Initialize(spoutWidth, spoutHeight);
    }

    public void Initialize(int w, int h)
    {
        if (_initialized) return;

        // Safety: Ensure components are found if Awake hasn't run yet (Execution Order)
        if (sender == null) sender = GetComponent<SpoutSender>();
        if (cam == null)    cam    = GetComponent<Camera>();

        spoutWidth = w;
        spoutHeight = h;

        // 1) Create fixed-size RenderTexture for Spout (depth=24 to match RenderGraph requirements)
        spoutRT = new RenderTexture(spoutWidth, spoutHeight, 24, RenderTextureFormat.ARGB32)
        {
            useMipMap        = false,
            autoGenerateMips = false,
            antiAliasing     = 1,
            name             = $"SpoutRT_{spoutWidth}x{spoutHeight}"
        };
        spoutRT.Create();

        // 2) Render this camera into the RT and send it via Spout (Texture mode)
        cam.targetTexture    = spoutRT;
        sender.sourceTexture = spoutRT;

        // PRODUCTION FIX: Make Spout camera "boring" - no post-processing, minimal features
        // Why: URP RenderGraph + post-processing + RenderTexture output = instability
        #if UNITY_PIPELINE_URP
        var additionalCameraData = cam.GetUniversalAdditionalCameraData();
        if (additionalCameraData != null)
        {
            additionalCameraData.renderPostProcessing = false;  // Disable all post-processing
            additionalCameraData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
        }
        #endif

        // 3) Make the game window fill the current monitor (any resolution)
        int sysW = Display.main.systemWidth;
        int sysH = Display.main.systemHeight;

        var refreshRate = Screen.currentResolution.refreshRateRatio;
        Screen.SetResolution(
            sysW,
            sysH,
            FullScreenMode.FullScreenWindow,
            refreshRate // Use display's native refresh rate
        );

        if (Display.displays.Length > 0)
            Display.displays[0].Activate();

        // 4) On-screen preview (letterboxed) so operators see the correct aspect
        if (previewOnScreen)
            CreateFullscreenPreview(spoutRT);

        // 5) Suppress "No cameras rendering" overlay
        if (hideNoCameraOverlay)
            CreateDummyDisplayCamera();

        Debug.Log($"[Spout] Output Initialized: {spoutWidth}x{spoutHeight}. Screen: {sysW}x{sysH}.");
        _initialized = true;
    }

    private void CreateFullscreenPreview(RenderTexture rt)
    {
        // Canvas
        var canvasGO = new GameObject(
            "SpoutPreviewCanvas",
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );

        previewCanvas = canvasGO.GetComponent<Canvas>();
        previewCanvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        previewCanvas.sortingOrder = short.MaxValue;

        // RawImage (+ optional AspectRatioFitter for letterboxing)
        var imgGO = new GameObject("SpoutPreview", typeof(RawImage));
        if (preserveAspect)
            imgGO.AddComponent<AspectRatioFitter>();

        imgGO.transform.SetParent(canvasGO.transform, false);
        previewImage = imgGO.GetComponent<RawImage>();
        previewImage.texture       = rt;
        previewImage.raycastTarget = false;

        // Stretch rect to window
        var r = previewImage.rectTransform;
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = Vector2.zero;
        r.offsetMax = Vector2.zero;

        // Keep correct aspect (letterbox)
        if (preserveAspect)
        {
            var fitter = imgGO.GetComponent<AspectRatioFitter>();
            fitter.aspectMode  = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = (float)rt.width / rt.height;
        }
    }

    private void CreateDummyDisplayCamera()
    {
        var go = new GameObject("DummyDisplayCamera");
        dummyDisplayCam = go.AddComponent<Camera>();
        dummyDisplayCam.clearFlags      = CameraClearFlags.SolidColor;
        dummyDisplayCam.backgroundColor = Color.black;
        dummyDisplayCam.cullingMask     = 0;       // render nothing
        dummyDisplayCam.depth           = -1000;   // behind everything
        dummyDisplayCam.targetTexture   = null;    // writes to Display 1
        DontDestroyOnLoad(go);
    }

    private void OnDestroy()
    {
        // UI cleanup
        if (previewImage != null)
            previewImage.texture = null;
        if (previewCanvas != null)
            Destroy(previewCanvas.gameObject);

        // Dummy cam cleanup
        if (dummyDisplayCam != null)
            Destroy(dummyDisplayCam.gameObject);

        // Unbind camera & Spout
        if (cam != null)
            cam.targetTexture = null;
        if (sender != null)
            sender.sourceTexture = null;

        // Destroy RT
        if (spoutRT != null)
        {
            spoutRT.Release();
            Destroy(spoutRT);
        }
    }
}
