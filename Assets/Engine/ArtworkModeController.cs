using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class ArtworkModeController : MonoBehaviour
{
    [Header("OSC Settings")]
    public int listenPort = 7000;
    public string oscAddress = "/artwork/active";
    public bool acceptFloat = true;
    [HideInInspector] public bool blockReturnToIdle = false; // Set via ArtworkConfig.json

    [Header("References")]
    public ArtworkController ctrl;
    public GameObject debugPanel;
    public TextMeshProUGUI logText;
    public TextMeshProUGUI fpsText;

    private UdpClient _udp;
    private Thread _thread;
    private volatile bool _running;
    private ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();
    private volatile int _lastLevel = -1;

    // FPS
    private float _fpsTime;
    private int _fpsCount;

    void Start()
    {
        if (!ctrl) ctrl = FindFirstObjectByType<ArtworkController>();
        
        try
        {
            _udp = new UdpClient(listenPort);
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
            Log($"OSC Listening on port {listenPort} for {oscAddress}");
        }
        catch (Exception ex)
        {
            Log($"OSC Init Failed: {ex.Message}");
        }

        if (debugPanel) debugPanel.SetActive(false);
    }

    void OnDestroy()
    {
        _running = false;
        _udp?.Close();
        _thread?.Join(100);
    }

    void Update()
    {
        // Toggle UI
        if (Input.GetKeyDown(KeyCode.F3))
        {
            if (debugPanel) debugPanel.SetActive(!debugPanel.activeSelf);
        }

        // Process Actions
        while (_actions.TryDequeue(out var act)) act?.Invoke();

        // FPS
        if (debugPanel && debugPanel.activeSelf && fpsText)
        {
            _fpsTime += Time.unscaledDeltaTime;
            _fpsCount++;
            if (_fpsTime >= 0.5f)
            {
                fpsText.text = $"{(_fpsCount / _fpsTime):0.} FPS";
                _fpsTime = 0;
                _fpsCount = 0;
            }
        }

        // OSC State Sync: 
        // If the game has returned to idle (activeRunning check), we must ensure 
        // our internal _lastLevel matches 0 so that the next "1" packet filters through.
        if (ctrl && !ctrl.activeRunning && _lastLevel == 1)
        {
             _lastLevel = 0;
             // ControllerMain.LogInfo("[OSC] Auto-reset state to 0 (Game is Idle)");
        }
    }

    private void ReceiveLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                if (_udp.Available > 0)
                {
                    var data = _udp.Receive(ref ep);
                    int idx = 0;
                    string addr = ReadOscString(data, ref idx); 
                    string types = ReadOscString(data, ref idx);

                    // Debug Log for EVERY packet
                    // ControllerMain.LogInfo($"[OSC_RAW] From={ep} Addr='{addr}' Types='{types}' Bytes={data.Length}");

                    if (addr != oscAddress) 
                    {
                         // ControllerMain.LogInfo($"[OSC_IGNORE] Address mismatch. Got '{addr}', expected '{oscAddress}'");
                         continue;
                    }

                    int val = 0;
                    bool parsed = false;

                    if (types == ",i")
                    {
                        val = (data[idx] << 24 | data[idx+1] << 16 | data[idx+2] << 8 | data[idx+3]) != 0 ? 1 : 0;
                        parsed = true;
                    }
                    else if (acceptFloat && types == ",f")
                    {
                        byte[] bytes = { data[idx], data[idx+1], data[idx+2], data[idx+3] };
                        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                        float f = BitConverter.ToSingle(bytes, 0);
                        val = Mathf.Approximately(f, 0f) ? 0 : 1;
                        parsed = true;
                        // ControllerMain.LogInfo($"[OSC_FLOAT] Parsed float {f} -> {val}");
                    }
                    else
                    {
                         ControllerMain.LogWarn($"[OSC_ERR] Unsupported types '{types}' or format.");
                    }

                    if (parsed)
                    {
                         // FILTER: Block return-to-idle (0) commands if enabled in config
                         if (val == 0 && blockReturnToIdle)
                         {
                             ControllerMain.LogInfo($"[OSC_BLOCKED] Return-to-idle command (0) ignored (blockReturnToIdle=true)");
                             continue; // Skip processing this command
                         }
                         
                         // Always log state changes or periodic updates
                         if (val != _lastLevel)
                         {
                             ControllerMain.LogInfo($"[OSC_CHANGE] {addr} val={val} (Previous={_lastLevel})");
                             _lastLevel = val;
                             _actions.Enqueue(() => {
                                 Log($"RX: Active={val}");
                                 if (ctrl) ctrl.ReceiveActive(val);
                             });
                         }
                         else
                         {
                             // Heartbeat log if needed (optional)
                             // ControllerMain.LogInfo($"[OSC_HEARTBEAT] {addr} val={val}");
                         }
                    }
                }
                else
                {
                    Thread.Sleep(1); 
                }
            }
            catch (Exception ex) { ControllerMain.LogError($"[OSC_CRASH] {ex.Message}"); }
        }
    }

    private string ReadOscString(byte[] data, ref int i)
    {
        int start = i;
        while (i < data.Length && data[i] != 0) i++;
        string s = Encoding.ASCII.GetString(data, start, i - start);
        i++;
        while (i % 4 != 0) i++;
        return s;
    }

    private void Log(string m)
    {
        // ControllerMain.LogInfo($"[OSC_UI] {m}");
        if (logText)
        {
            try {
                if (logText.text.Length > 2000) logText.text = logText.text.Substring(500);
                logText.text += $"[{DateTime.Now:HH:mm:ss}] {m}\n";
            } catch {}
        }
    }
}
