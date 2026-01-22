// ArtworkOscMinimal.cs (panel toggle + controller fallback)
// Minimal OSC int32 (and optional float) receiver + on-screen logs + FPS.
// BOTH texts live under one panel that is shown/hidden with F3 (no UI toggle button).
//
// Level behavior (idempotent):
// - value == 1 (or any non-zero when acceptFloatAsLevel) => request Active (only if in Ambient)
// - value == 0                                          => request Ambient (only if currently Active)
//
// Buttons (optional):
// - If activeButton assigned, it is clicked on "Active" requests
// - If ambientButton assigned, it is clicked on "Ambient" requests
//
// Notes:
// - If your sender uses floats, enable 'acceptFloatAsLevel' to accept ,f (non-zero => Active).
// - Address must match oscAddress exactly.

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class ArtworkOscMinimal : MonoBehaviour
{
    [Header("OSC")]
    public int listenPort = 7000;
    public string oscAddress = "/artwork/active"; // must match sender exactly
    [Tooltip("Also accept OSC float (',f'); non-zero treated as Active (1).")]
    public bool acceptFloatAsLevel = true;

    [Header("Buttons (optional)")]
    public Button activeButton;    // invoked on Active request
    public Button ambientButton;   // invoked on Ambient request

    [Header("Direct Controller (optional fallback)")]
    [Tooltip("If not assigned, will auto-find one in the scene on Start(). Used if buttons are not set.")]
    public ArtworkController artworkController;

    [Header("Debug Panel (assign a parent GameObject that contains BOTH texts)")]
    [Tooltip("Parent panel GameObject that contains the logText and fpsText (will be toggled with F3). Starts hidden.")]
    public GameObject debugPanel;

    [Header("Log Display")]
    [Tooltip("Assign the TextMeshProUGUI used to show log lines.")]
    public TextMeshProUGUI logText;

    [Header("FPS Display")]
    [Tooltip("Assign the TextMeshProUGUI used to show FPS.")]
    public TextMeshProUGUI fpsText;
    [Tooltip("How often (in seconds) to update the FPS text.")]
    public float fpsRefreshInterval = 0.2f;

    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;

    private readonly ConcurrentQueue<string> _uiLog = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();

    // FPS accumulators
    private float _fpsElapsed = 0f;
    private int _fpsFrames = 0;

    // Track last normalized level (0 or 1) for log noise reduction; start unknown
    private int _lastLevel = int.MinValue;

    // Hard cap for in-memory log lines to prevent RAM growth when panel is hidden
    private const int MaxBuffered = 500;

    void Start()
    {
        if (artworkController == null)
            artworkController = FindFirstObjectByType<ArtworkController>();

        try
        {
            string artworkCfg = ControllerMain.PathInConfig("ArtworkConfig.json");
            Log($"Using ArtworkConfig: {artworkCfg} {(File.Exists(artworkCfg) ? "[FOUND]" : "[MISSING]")}");
        }
        catch (Exception e)
        {
            Log($"Config path check failed: {e.Message}");
        }

        try
        {
            _udp = new UdpClient(listenPort);
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "OSC_RX" };
            _rxThread.Start();
            Log($"Listening UDP {IPAddress.Any}:{listenPort} for '{oscAddress}' (int32{(acceptFloatAsLevel ? " or float" : "")}).");
        }
        catch (Exception ex)
        {
            Log($"UDP open failed on {listenPort}: {ex.Message}");
        }

        // Initialize debug panel visibility: start hidden
        if (debugPanel != null)
        {
            debugPanel.SetActive(false);
        }
        else
        {
            if (logText != null) logText.gameObject.SetActive(false);
            if (fpsText != null) fpsText.gameObject.SetActive(false);
        }
    }

    void OnDestroy()
    {
        _running = false;
        try { _udp?.Close(); } catch { }
        try { _rxThread?.Join(150); } catch { }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F3))
        {
            if (debugPanel != null)
            {
                debugPanel.SetActive(!debugPanel.activeSelf);
            }
            else
            {
                bool newState = !(logText != null && logText.gameObject.activeSelf);
                if (logText != null) logText.gameObject.SetActive(newState);
                if (fpsText != null) fpsText.gameObject.SetActive(newState);
            }
        }

        bool panelVisible = debugPanel ? debugPanel.activeSelf
                                       : (logText?.gameObject.activeInHierarchy == true || fpsText?.gameObject.activeInHierarchy == true);

        // Always drain the log queue to keep memory flat; only write to UI when visible
        {
            string line;
            bool any = false;
            var sb = new StringBuilder(1024);
            while (_uiLog.TryDequeue(out line))
            {
                sb.AppendLine(line);
                any = true;
            }
            if (any && panelVisible && logText && logText.gameObject.activeInHierarchy)
            {
                logText.text = sb.ToString() + logText.text;
            }
            // If panel is hidden, drained lines are discarded (prevents growth).
        }

        if (panelVisible && fpsText && fpsText.gameObject.activeInHierarchy)
        {
            _fpsElapsed += Time.unscaledDeltaTime;
            _fpsFrames++;

            if (_fpsElapsed >= Mathf.Max(0.05f, fpsRefreshInterval))
            {
                float fps = _fpsFrames / _fpsElapsed;
                fpsText.text = $"{fps:0.} FPS";
                _fpsElapsed = 0f;
                _fpsFrames = 0;
            }
        }
        else
        {
            _fpsElapsed = 0f;
            _fpsFrames = 0;
        }

        // Run any marshalled actions
        while (_actions.TryDequeue(out var a)) a?.Invoke();
    }

    private void ReceiveLoop()
    {
        if (_udp == null) return;
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                var data = _udp.Receive(ref ep);
                ParseAndHandle(data, ep);
            }
            catch (ObjectDisposedException)
            {
                break; // socket closed during shutdown
            }
            catch (SocketException)
            {
                // usually benign (interrupted), keep looping
            }
            catch (Exception ex)
            {
                Log($"[RX ERR] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ParseAndHandle(byte[] packet, IPEndPoint from)
    {
        int idx = 0;
        string addr = ReadOscString(packet, ref idx);
        string types = ReadOscString(packet, ref idx);

        if (!string.Equals(addr, oscAddress, StringComparison.Ordinal))
        {
            // Silent ignore: different OSC address
            return;
        }

        bool ok = false;
        int level = 0; // normalized to 0 or 1

        if (types == ",i" && idx + 4 <= packet.Length)
        {
            int raw = (packet[idx] << 24) | (packet[idx + 1] << 16) | (packet[idx + 2] << 8) | packet[idx + 3];
            level = (raw != 0) ? 1 : 0;
            ok = true;
        }
        else if (acceptFloatAsLevel && types == ",f" && idx + 4 <= packet.Length)
        {
            // big-endian float
            var be = new byte[4] { packet[idx], packet[idx + 1], packet[idx + 2], packet[idx + 3] };
            if (BitConverter.IsLittleEndian) Array.Reverse(be);
            float f = BitConverter.ToSingle(be, 0);
            level = Mathf.Approximately(f, 0f) ? 0 : 1;
            ok = true;
        }

        if (!ok)
        {
            Log($"Bad OSC types or length from {from.Address}:{from.Port}. types='{types}' bytes={packet.Length}");
            return;
        }

        // Only log on change to reduce noise
        if (_lastLevel != level)
        {
            Log($"RX {from.Address}:{from.Port} {addr} -> {(level == 1 ? "ACTIVE (1)" : "AMBIENT (0)")}");
            _lastLevel = level;
        }

        UnityMainThread(() => HandleLevel(level));
    }

    private void HandleLevel(int level)
    {
        if (level == 1)
        {
            // Request Active (one-way): only starts if controller is in Idle.
            if (activeButton != null)
                activeButton.onClick?.Invoke();
            else if (artworkController != null)
                artworkController.ReceiveActive(1);
            else
                Log("No Active button and no ArtworkController found for value=1.");
        }
        else // level == 0
        {
            // Request Ambient (one-way): only returns if controller is Active.
            if (ambientButton != null)
                ambientButton.onClick?.Invoke();
            else if (artworkController != null)
                artworkController.ReceiveActive(0);
            else
                Log("No Ambient button and no ArtworkController found for value=0.");
        }
    }

    private void UnityMainThread(Action a) => _actions.Enqueue(a);

    private static string ReadOscString(byte[] buf, ref int index)
    {
        int start = index;
        while (index < buf.Length && buf[index] != 0) index++;
        string s = Encoding.ASCII.GetString(buf, start, index - start);
        index++; // skip null
        while (index % 4 != 0) index++;
        return s;
    }

    private void Log(string msg)
    {
        string t = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{t}] {msg}";
        Debug.Log(line);

        _uiLog.Enqueue(line);

        // Hard cap to prevent unbounded memory growth when panel is hidden.
        while (_uiLog.Count > MaxBuffered && _uiLog.TryDequeue(out _)) { }
    }
}
