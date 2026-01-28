using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class ControllerMain : MonoBehaviour
{
    public static ControllerMain Instance { get; private set; }

    [Serializable]
    public class ConfigData
    {
        public bool DebugMode = false;
        public string AmbientStreamPath = "";
        public string ActiveStreamPath  = "";
        public string AudioPath         = ""; // Restored
        
        public string LogFileName = "runtime.log";
        public int    MaxLogDays = 7;
        
        public bool   UILogOverlayEnabled = true;
        public string UIToggleKey = "F2";
    }

    public ConfigData Config { get; private set; } = new ConfigData();

    public string AmbientStreamPath => Config.AmbientStreamPath;
    public string ActiveStreamPath  => Config.ActiveStreamPath;
    public string AudioPath         => Config.AudioPath; // Restored

    // Paths
    private string _baseRoot;
    private string _configDir;
    private string _configPath;
    private string _logsDir;
    private string _logFilePath;

    // Logging
    private StreamWriter _logWriter;
    private readonly object _logLock = new object();
    private readonly List<string> _earlyLogs = new List<string>();
    private bool _loggingInitialized = false;

    // UI
    public GameObject logPanel;
    public TMP_Text   logText;
    private KeyCode   _toggleKey = KeyCode.F2;
    private bool      _uiVisible = false;

    // Cleanup
    private float     _lastCleanupTime;
    private const float CLEANUP_INTERVAL = 7200f; // 2 hours (museum stability)

    // ----------------------------------------------------------------------
    // STATIC API
    // ----------------------------------------------------------------------
    public static string PathInConfig(string fileName) 
        => Path.Combine(Instance?._configDir ?? Application.persistentDataPath, fileName);

    public static void LogStep (string m) => Instance?.Log("STEP", m);
    public static void LogInfo (string m) => Instance?.Log("INF",  m);
    public static void LogWarn (string m) => Instance?.Log("WRN",  m);
    public static void LogError(string m) => Instance?.Log("ERR",  m);

    // ═══════════════════════════════════════════════════════════════
    // INITIALIZATION
    // ═══════════════════════════════════════════════════════════════

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        ResolvePaths();
        Application.logMessageReceived += OnUnityLog;
        
        Debug.Log("[ControllerMain] Awake complete. Paths resolved.");
    }

    void Start()
    {
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await Task.Yield(); // Wait one frame to ensure other Awakes run

        // 1. Load Config
        await LoadConfigAsync();

        // 2. Setup Logging
        SetupLogging();

        // 3. UI Setup
        if (!string.IsNullOrEmpty(Config.UIToggleKey)) 
            Enum.TryParse(Config.UIToggleKey, out _toggleKey);
        
        if (logPanel) logPanel.SetActive(false);
        
        LogInfo("ControllerMain initialized.");
        LogInfo($"BaseRoot: {_baseRoot}");
        LogInfo($"ActivePath: {ActiveStreamPath}");
        LogInfo($"AmbientPath: {AmbientStreamPath}");
        LogInfo($"AudioPath: {AudioPath}"); // Logged for verification

        // Flush buffer
        _loggingInitialized = true;
        lock (_logLock)
        {
            foreach (var line in _earlyLogs) WriteToFile(line);
            _earlyLogs.Clear();
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(_toggleKey))
        {
            _uiVisible = !_uiVisible;
            if (logPanel) logPanel.SetActive(_uiVisible);
        }

        if (Time.time - _lastCleanupTime > CLEANUP_INTERVAL)
        {
            _lastCleanupTime = Time.time;
            Resources.UnloadUnusedAssets();
        }
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnUnityLog;
        lock (_logLock)
        {
            _logWriter?.Flush();
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }

    // ----------------------------------------------------------------------
    // PATHS & CONFIG
    // ----------------------------------------------------------------------

    private void ResolvePaths()
    {
        // Priority: Command Line -> Env Var -> Exe Dir -> PersistentData
        _baseRoot = GetCmdArg("--baseDir");
        
        if (string.IsNullOrEmpty(_baseRoot)) 
            _baseRoot = Environment.GetEnvironmentVariable("SOMATIC_BASEDIR");
            
        if (string.IsNullOrEmpty(_baseRoot))
            _baseRoot = Path.GetDirectoryName(Application.dataPath); // Exe dir

        if (string.IsNullOrEmpty(_baseRoot))
            _baseRoot = Path.Combine(Application.persistentDataPath, "SomaticLandscapes");

        _baseRoot = _baseRoot.Replace('\\', '/');
        _configDir = Path.Combine(_baseRoot, "Config");
        _logsDir   = Path.Combine(_baseRoot, "Logs");
        _configPath = Path.Combine(_configDir, "ControllerMain.json");
    }

    private string GetCmdArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i=0; i<args.Length-1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i+1];
        return null;
    }

    private async Task LoadConfigAsync()
    {
        try 
        {
            if (!Directory.Exists(_configDir)) Directory.CreateDirectory(_configDir);
            
            if (File.Exists(_configPath))
            {
                string json = await Task.Run(() => File.ReadAllText(_configPath));
                Config = JsonUtility.FromJson<ConfigData>(json);
            }
            else
            {
                // Defaults
                string sa = Application.streamingAssetsPath.Replace('\\','/');
                string exe = Path.GetDirectoryName(Application.dataPath)?.Replace('\\','/');

                Config.AmbientStreamPath = Path.Combine(sa, "Ambient").Replace('\\','/');
                Config.ActiveStreamPath  = Path.Combine(sa, "Active").Replace('\\','/');
                Config.AudioPath         = Path.Combine(exe, "Music").Replace('\\','/');

                File.WriteAllText(_configPath, JsonUtility.ToJson(Config, true));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Config load failed: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------------
    // LOGGING
    // ----------------------------------------------------------------------

    private void SetupLogging()
    {
        try
        {
            if (!Directory.Exists(_logsDir)) Directory.CreateDirectory(_logsDir);
            
            // Prune old logs
            var days = Mathf.Max(1, Config.MaxLogDays);
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var f in Directory.GetFiles(_logsDir, "*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }

            // Open new log
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string name = string.IsNullOrEmpty(Config.LogFileName) ? "runtime" : Path.GetFileNameWithoutExtension(Config.LogFileName);
            _logFilePath = Path.Combine(_logsDir, $"{name}_{timestamp}.log");
            
            _logWriter = new StreamWriter(_logFilePath, true, Encoding.UTF8) { AutoFlush = true };
            _logWriter.WriteLine($"=== LOG START {DateTime.Now} ===");
        }
        catch (Exception ex) 
        {
            Debug.LogError($"Logging setup failed: {ex.Message}");
        }
    }

    private void Log(string level, string msg)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] [{level}] {msg}";
        
        // Console echo
        if (level == "ERR") Debug.LogError($"[Controller] {msg}");
        else if (level == "WRN") Debug.LogWarning($"[Controller] {msg}");
        else Debug.Log($"[Controller] {msg}");

        WriteToFile(line);
        UpdateUI(line);
    }

    private void OnUnityLog(string cond, string stack, LogType type)
    {
        if (cond.StartsWith("[Controller]")) return; // Avoid echo loops

        string lvl = type switch { 
            LogType.Error or LogType.Exception => "ERR", 
            LogType.Warning => "WRN", 
            _ => "INF" 
        };
        
        string line = $"[{DateTime.Now:HH:mm:ss}] [UNITY-{lvl}] {cond}";
        if (type == LogType.Exception) line += $"\n{stack}";
        
        WriteToFile(line);
        UpdateUI(line);
    }

    private void WriteToFile(string line)
    {
        lock (_logLock)
        {
            if (_logWriter != null) 
            {
                try { _logWriter.WriteLine(line); } catch {}
            }
            else if (!_loggingInitialized)
            {
                _earlyLogs.Add(line);
            }
        }
    }

    private void UpdateUI(string line)
    {
        if (GlobalMainThreadDispatcher.Instance != null)
        {
            GlobalMainThreadDispatcher.Enqueue(() => {
                if (logText && Config.UILogOverlayEnabled)
                {
                     // User Request: Newest logs at the TOP ("refresh the top lines")
                     // Prepend the new line
                     logText.text = line + "\n" + logText.text;
                     
                     // Truncate from the BOTTOM if too long
                     if (logText.text.Length > 15000) 
                         logText.text = logText.text.Substring(0, 15000); 
                }
            });
        }
    }
}
