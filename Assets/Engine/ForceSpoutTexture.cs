using UnityEngine;
using UnityEngine.UI;        // RawImage + AspectRatioFitter
using Klak.Spout;

/// <summary>
/// REFACTORED: Passive Spout bridge that sends an externally-managed RenderTexture.
/// No longer creates its own RT or changes screen resolution (those are handled by ArtworkController).
/// This eliminates redundant GPU surface creation and swapchain churn.
/// </summary>
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(SpoutSender))]
public class ForceSpoutTexture : MonoBehaviour
{
    [Header("Preview / UX")]
    [Tooltip("Mirror the Spout RT to the window so operators can see it.")]
    public bool previewOnScreen = true;

    [Tooltip("Preserve aspect of the Spout texture (adds letterboxing/pillarboxing).")]
    public bool preserveAspect = true;

    [Tooltip("Create a dummy camera so Unity never shows 'No cameras rendering'.")]
    public bool hideNoCameraOverlay = true;

    private SpoutSender   sender;
    private Camera        cam;
    private RenderTexture externalSourceRT;  // The RT we receive from ArtworkController

    // UI preview
    private Canvas   previewCanvas;
    private RawImage previewImage;
    private Camera   dummyDisplayCam;

    private bool _initialized = false;

    private void Awake()
    {
        sender = GetComponent<SpoutSender>();
        cam    = GetComponent<Camera>();
    }

    /// <summary>
    /// Initialize the Spout bridge with an externally-managed source RT.
    /// This can be called multiple times if the source RT changes (e.g., config reload).
    /// </summary>
    public void Initialize(RenderTexture sourceRT)
    {
        if (sourceRT == null)
        {
            Debug.LogError("[ForceSpoutTexture] Cannot initialize with null RenderTexture!");
            return;
        }

        // Allow re-init if the source RT changes
        if (_initialized && externalSourceRT == sourceRT)
        {
            Debug.Log("[ForceSpoutTexture] Already initialized with this RT, skipping.");
            return;
        }

        externalSourceRT = sourceRT;

        // Wire the Camera to render into the external RT
        cam.targetTexture = externalSourceRT;

        // Wire Spout to send the external RT (Texture mode)
        sender.sourceTexture = externalSourceRT;

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

        // On-screen preview (letterboxed) so operators see the correct aspect
        if (previewOnScreen && !_initialized)
            CreateFullscreenPreview(externalSourceRT);

        // Suppress "No cameras rendering" overlay
        if (hideNoCameraOverlay && !_initialized)
            CreateDummyDisplayCamera();

        Debug.Log($"[ForceSpoutTexture] Initialized with external RT: {externalSourceRT.name} ({externalSourceRT.width}x{externalSourceRT.height})");
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

        // Unbind camera & Spout (but DON'T destroy the external RT - we don't own it)
        if (cam != null)
            cam.targetTexture = null;
        if (sender != null)
            sender.sourceTexture = null;

        // NOTE: We do NOT destroy externalSourceRT - it's owned by ArtworkController
    }

    public void PauseOutput(bool paused)
    {
        if (sender != null) sender.Paused = paused;
    }
}
