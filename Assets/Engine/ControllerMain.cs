// ControllerMain.cs - Robust startup with safe filesystem operations
// FIX: All file I/O moved out of Awake() to prevent startup crashes
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using SomaticLandscapes.Async;

[DisallowMultipleComponent]
public class ControllerMain : MonoBehaviour
{
    public static ControllerMain Instance { get; private set; }

    [Serializable]
    public class ConfigData
    {
        public bool EnableThis = true;
        public bool DebugMode = false;

        public string AmbientStreamPath = "";
        public string ActiveStreamPath  = "";
        public string AudioPath         = "";

        public string LogRootMode = "Persistent"; // Changed default: Persistent | External | StreamingAssets | ExeDirectory
        public string LogFileName = "runtime.log";
        public bool   LogRotateEachRun = true;
        public bool   LogKeepLatestAlias = true;
        public string LogAbsoluteDir = "";
        public string LogAbsoluteFile = "";
        public int    MaxLogDays = 14;

        public bool   UILogOverlayEnabled = true;
        public int    UILogMaxLines = 200;
        public string UIOverlayMessage = "";
        public string UIToggleKey = "F2";

        public int RotateBySizeMB   = 50;
        public int FlushEveryNLines = 20;
    }

    public ConfigData Config { get; private set; } = new ConfigData();

    public string AmbientStreamPath => Config?.AmbientStreamPath ?? string.Empty;
    public string ActiveStreamPath  => Config?.ActiveStreamPath  ?? string.Empty;
    public string AudioPath         => Config?.AudioPath         ?? string.Empty;
    public bool   DebugMode         => Config?.DebugMode         ?? false;

    // Paths
    string _baseRoot;
    string _configDir;
    string _configPath;
    string _logsDir;
    string _primaryLogFilePath;
    string _latestAliasPath;

    // Command-line override
    string _cmdLineBaseDir = null;

    // Writers/streams
    StreamWriter _primaryWriter;
    FileStream   _primaryStream;
    StreamWriter _aliasWriter;
    FileStream   _aliasStream;

    DateTime _lastPrune = DateTime.MinValue;

    // UI overlay
    public GameObject logPanel;
    public TMP_Text   logText;
    public Button     toggleButton;

    KeyCode toggleKey = KeyCode.F2;
    int uiMaxLines = 200;
    readonly Queue<string> _uiLines = new Queue<string>();
    bool _uiVisible = false;

    long _rotateBytesThreshold = 0;
    int  _linesSinceFlush = 0;

    // Safe logger state
    bool _fileLoggingEnabled = true;
    int _fileWriteFailures = 0;
    DateTime _nextFileWriteRetry = DateTime.MinValue;
    
    // Initialization state
    bool _initialized = false;
    readonly List<string> _earlyLog = new List<string>();
    
    // Async cancellation
    private CancellationTokenSource _cancellationTokenSource;

    // GPU Memory Management
    float _lastMemoryCleanup = 0f;
    const int MEMORY_CLEANUP_INTERVAL = 300; // 5 minutes

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════
    
    public static string ConfigDirectory =>
        Instance?._configDir
        ?? Path.Combine(Application.persistentDataPath, "SomaticLandscapes", "Config");

    public static string PathInConfig(string fileName) =>
        Path.Combine(ConfigDirectory, fileName).Replace('\\','/');

    public static void LogStep (string m) => Instance?.WriteLine("STEP", m);
    public static void LogInfo (string m) => Instance?.WriteLine("INF",  m);
    public static void LogWarn (string m) => Instance?.WriteLine("WRN",  m);
    public static void LogError(string m) => Instance?.WriteLine("ERR",  m);

    // ═══════════════════════════════════════════════════════════════
    // LIFECYCLE (SAFE STARTUP)
    // ═══════════════════════════════════════════════════════════════

    void Awake()
    {
        // CRITICAL: Minimal Awake - NO FILE I/O!
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Parse command line early (safe, no I/O)
        ParseCommandLineArgs();

        // IMPORTANT: Resolve paths NOW (safe - just calculates strings, no I/O)
        // Other scripts (like ArtworkController) may call PathInConfig() during their Awake()
        SafeResolveBaseRoot();

        // Set safe defaults (no I/O)
        Config = new ConfigData();
        NormalizePaths(Config); // Safe - just sets default string paths
        
        Debug.Log("[ControllerMain] Awake complete (file I/O deferred to Start)");
    }

    void Start()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _ = InitializeAsync(_cancellationTokenSource.Token);
    }

    async Task InitializeAsync(CancellationToken ct)
    {
        Debug.Log("[ControllerMain] === Begin Safe Initialization ===");
        
        try
        {
            // Paths already resolved in Awake
            // Step 1: Load config (with fallback to defaults)
            await Task.Yield();
            await SafeLoadConfigAsync(ct);
            
            // Step 2: UI configuration
            uiMaxLines = Mathf.Max(50, Config.UILogMaxLines);
            _uiVisible = false; // Start hidden
            toggleKey  = ParseKeyOrDefault(Config.UIToggleKey, KeyCode.F2);
            
            // Step 3: Initialize logging (safe, won't crash)
            await Task.Yield();
            SafePrepareDirsAndLog();
            
            // Step 4: Flush early logs to file
            foreach (var msg in _earlyLog)
            {
                TryWriteFile(msg);
            }
            _earlyLog.Clear();
            
            // Step 5: System info snapshot
            LogInfo("=== UNITY SYSTEM INFO SNAPSHOT ===");
            LogInfo($"Unity Version: {Application.unityVersion}");
            LogInfo($"Platform: {Application.platform}");
            LogInfo($"Graphics API: {SystemInfo.graphicsDeviceType}");
            LogInfo($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVendor})");
            LogInfo($"Driver: {SystemInfo.graphicsDeviceVersion}");
            LogInfo($"VRAM: {SystemInfo.graphicsMemorySize} MB");
            LogInfo($"CPU: {SystemInfo.processorType} ({SystemInfo.processorCount} cores)");
            LogInfo($"RAM: {SystemInfo.systemMemorySize} MB");
            LogInfo($"Render Threaded: {SystemInfo.graphicsMultiThreaded}");
            LogInfo($"OS: {SystemInfo.operatingSystem}");
            LogInfo("===================================");

            _rotateBytesThreshold = (long)Mathf.Max(0, Config.RotateBySizeMB) * 1024L * 1024L;
            _linesSinceFlush = 0;

            SafePruneOldLogs(true);
            EnsureOverlayUI();
            SetLogPanelVisible(false);

            LogInfo("ControllerMain initialized successfully.");
            LogInfo($"EnableThis={Config.EnableThis}, DebugMode={Config.DebugMode}");
            LogInfo($"BaseRoot={_baseRoot}");
            LogInfo($"ConfigDir={_configDir}");
            LogInfo($"LogsDir={_logsDir}");
            LogInfo($"AmbientStreamPath={Config.AmbientStreamPath}");
            LogInfo($"ActiveStreamPath={Config.ActiveStreamPath}");
            LogInfo($"AudioPath={Config.AudioPath}");
            
            if (!string.IsNullOrEmpty(Config.UIOverlayMessage))
                LogInfo($"UIOverlayMessage=\"{Config.UIOverlayMessage}\"");
            
            _initialized = true;
            Debug.Log("[ControllerMain] === Initialization Complete ===");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("[ControllerMain] Initialization cancelled");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ControllerMain] Initialization failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    void OnEnable()  => Application.logMessageReceived += OnUnityLog;
    void OnDisable() => Application.logMessageReceived -= OnUnityLog;

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            ToggleLogPanel();

        // Periodic GPU memory cleanup to prevent driver timeouts
        if (Time.time - _lastMemoryCleanup > MEMORY_CLEANUP_INTERVAL)
        {
            ForceGPUMemoryCleanup();
            _lastMemoryCleanup = Time.time;
        }
    }

    void OnApplicationQuit()
    {
        CleanupCancellationToken();
        CloseLogs();
    }
    
    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        CleanupCancellationToken();
        CloseLogs();
    }
    
    void CleanupCancellationToken()
    {
        if (_cancellationTokenSource != null)
        {
            try
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                    _cancellationTokenSource.Cancel();
            }
            catch { /* Already disposed */ }
            
            try
            {
                _cancellationTokenSource.Dispose();
            }
            catch { /* Already disposed */ }
            
            _cancellationTokenSource = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // GPU MEMORY MANAGEMENT
    // ═══════════════════════════════════════════════════════════════

    void ForceGPUMemoryCleanup()
    {
        try
        {
            // Release unused video textures and assets
            Resources.UnloadUnusedAssets();

            // Force garbage collection
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();

            LogInfo("[GPU] Memory cleanup performed");
        }
        catch (Exception ex)
        {
            LogWarn($"[GPU] Memory cleanup failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // COMMAND-LINE ARGS
    // ═══════════════════════════════════════════════════════════════

    void ParseCommandLineArgs()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--baseDir", StringComparison.OrdinalIgnoreCase))
                {
                    _cmdLineBaseDir = args[i + 1];
                    Debug.Log($"[ControllerMain] Command-line baseDir: {_cmdLineBaseDir}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ControllerMain] Failed to parse command-line args: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SAFE PATH RESOLUTION
    // ═══════════════════════════════════════════════════════════════

    void SafeResolveBaseRoot()
    {
        Debug.Log("[ControllerMain] Resolving base paths...");

        // Strategy (ordered by priority):
        // 1. Command-line --baseDir (if writable)
        // 2. Environment variable SOMATIC_BASEDIR (if writable)
        // 3. Exe directory (default - same as before)
        // 4. Fallback to Application.persistentDataPath (if exe dir not writable)

        string candidate = null;

        // Try command-line
        if (!string.IsNullOrEmpty(_cmdLineBaseDir))
        {
            candidate = _cmdLineBaseDir;
            if (TestWritePermission(candidate))
            {
                _baseRoot = candidate;
                Debug.Log($"[ControllerMain] Using command-line baseDir: {_baseRoot}");
            }
            else
            {
                Debug.LogWarning($"[ControllerMain] Command-line baseDir not writable: {candidate}");
            }
        }

        // Try environment variable
        if (_baseRoot == null)
        {
            try
            {
                candidate = Environment.GetEnvironmentVariable("SOMATIC_BASEDIR");
                if (!string.IsNullOrEmpty(candidate) && TestWritePermission(candidate))
                {
                    _baseRoot = candidate;
                    Debug.Log($"[ControllerMain] Using env var SOMATIC_BASEDIR: {_baseRoot}");
                }
            }
            catch { }
        }

        // DEFAULT: Exe directory (same folder as game executable)
        // Always use this as default - don't test permissions upfront
        if (_baseRoot == null)
        {
            candidate = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(candidate))
            {
                _baseRoot = candidate;
                Debug.Log($"[ControllerMain] Using exe directory (default): {_baseRoot}");
            }
            else
            {
                Debug.LogWarning("[ControllerMain] Could not determine exe directory");
            }
        }

        // FALLBACK: persistentDataPath (only if we couldn't determine exe directory)
        if (_baseRoot == null)
        {
            _baseRoot = Path.Combine(Application.persistentDataPath, "SomaticLandscapes");
            Debug.Log($"[ControllerMain] Using fallback persistentDataPath: {_baseRoot}");
        }

        _baseRoot = _baseRoot.Replace('\\', '/');
        _configDir  = Path.Combine(_baseRoot, "Config").Replace('\\','/');
        _configPath = Path.Combine(_configDir, "ControllerMain.json").Replace('\\','/');

        Debug.Log($"[ControllerMain] Resolved ConfigPath: {_configPath}");
    }

    bool TestWritePermission(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            
            string testFile = Path.Combine(dir, $".write_test_{Guid.NewGuid()}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SAFE CONFIG LOADING
    // ═══════════════════════════════════════════════════════════════

    async Task SafeLoadConfigAsync(CancellationToken ct)
    {
        Debug.Log("[ControllerMain] Loading config...");

        try
        {
            SafeCreateDir(_configDir);
            
            if (File.Exists(_configPath))
            {
                // Try to load existing config
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        // Read file asynchronously on background thread
                        string json = await Task.Run(() => File.ReadAllText(_configPath, new UTF8Encoding(false)), ct);
                        var loaded = JsonUtility.FromJson<ConfigData>(json);
                        
                        if (loaded != null)
                        {
                            Config = loaded;
                            NormalizePaths(Config);
                            Debug.Log("[ControllerMain] Config loaded successfully");
                            return;
                        }
                        else
                        {
                            // Invalid JSON - rename and use defaults
                            string badName = $"ControllerMain.bad_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                            File.Move(_configPath, Path.Combine(_configDir, badName));
                            Debug.LogWarning($"[ControllerMain] Invalid config JSON, renamed to {badName}");
                            break;
                        }
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        // File locked, retry
                        await Task.Delay(200, ct);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ControllerMain] Failed to load config (attempt {attempt+1}): {ex.Message}");
                        break;
                    }
                }
            }

            // Create default config
            Config = new ConfigData();
            NormalizePaths(Config);
            
            try
            {
                string json = JsonUtility.ToJson(Config, true);
                await Task.Run(() => File.WriteAllText(_configPath, json, new UTF8Encoding(false)), ct);
                Debug.Log("[ControllerMain] Created default config");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ControllerMain] Could not write default config: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ControllerMain] Config loading failed, using defaults: {ex.Message}");
            Config = new ConfigData();
            NormalizePaths(Config);
        }
    }

    void SafeLoadConfig()
    {
        Debug.Log("[ControllerMain] Loading config...");

        try
        {
            SafeCreateDir(_configDir);
            
            if (File.Exists(_configPath))
            {
                // Try to load existing config
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        string json = File.ReadAllText(_configPath, new UTF8Encoding(false));
                        var loaded = JsonUtility.FromJson<ConfigData>(json);
                        
                        if (loaded != null)
                        {
                            Config = loaded;
                            NormalizePaths(Config);
                            Debug.Log("[ControllerMain] Config loaded successfully");
                            return;
                        }
                        else
                        {
                            // Invalid JSON - rename and use defaults
                            string badName = $"ControllerMain.bad_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                            File.Move(_configPath, Path.Combine(_configDir, badName));
                            Debug.LogWarning($"[ControllerMain] Invalid config JSON, renamed to {badName}");
                            break;
                        }
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        // File locked, retry
                        System.Threading.Thread.Sleep(200);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ControllerMain] Failed to load config (attempt {attempt+1}): {ex.Message}");
                        break;
                    }
                }
            }

            // Create default config
            Config = new ConfigData();
            NormalizePaths(Config);
            
            try
            {
                string json = JsonUtility.ToJson(Config, true);
                File.WriteAllText(_configPath, json, new UTF8Encoding(false));
                Debug.Log("[ControllerMain] Created default config");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ControllerMain] Could not write default config: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ControllerMain] Config loading failed, using defaults: {ex.Message}");
            Config = new ConfigData();
            NormalizePaths(Config);
        }
    }

    void NormalizePaths(ConfigData c)
    {
        string exeDir = Path.GetDirectoryName(Application.dataPath)?.Replace('\\','/');

        if (string.IsNullOrWhiteSpace(c.AmbientStreamPath))
            c.AmbientStreamPath = Path.Combine(Application.streamingAssetsPath, "Ambient").Replace('\\','/');
        if (string.IsNullOrWhiteSpace(c.ActiveStreamPath))
            c.ActiveStreamPath  = Path.Combine(Application.streamingAssetsPath, "Active").Replace('\\','/');
        if (string.IsNullOrWhiteSpace(c.AudioPath))
            c.AudioPath         = Path.Combine(exeDir, "Music").Replace('\\','/');

        c.AmbientStreamPath = EnsureSlash(Forward(c.AmbientStreamPath));
        c.ActiveStreamPath  = EnsureSlash(Forward(c.ActiveStreamPath));
        c.AudioPath         = EnsureSlash(Forward(c.AudioPath));

        if (c.MaxLogDays <= 0)        c.MaxLogDays = 14;
        if (c.UILogMaxLines < 50)     c.UILogMaxLines = 200;
        if (c.RotateBySizeMB < 0)     c.RotateBySizeMB = 0;
        if (c.FlushEveryNLines < 1)   c.FlushEveryNLines = 1;
    }

    // ═══════════════════════════════════════════════════════════════
    // SAFE LOGGING INITIALIZATION
    // ═══════════════════════════════════════════════════════════════

    void SafePrepareDirsAndLog()
    {
        Debug.Log("[ControllerMain] Preparing logs...");

        try
        {
            if (!string.IsNullOrWhiteSpace(Config.LogAbsoluteFile))
            {
                _primaryLogFilePath = Config.LogAbsoluteFile.Replace('\\', '/');
                _logsDir = Path.GetDirectoryName(_primaryLogFilePath)?.Replace('\\', '/');
            }
            else
            {
                _logsDir = !string.IsNullOrWhiteSpace(Config.LogAbsoluteDir)
                    ? Config.LogAbsoluteDir.Replace('\\', '/')
                    : ResolveLogsRoot(Config.LogRootMode);

                string baseName = string.IsNullOrWhiteSpace(Config.LogFileName) ? "runtime.log" : Config.LogFileName.Trim();
                if (Config.LogRotateEachRun)
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(baseName);
                    string ext = Path.GetExtension(baseName);
                    if (string.IsNullOrEmpty(ext)) ext = ".log";
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    _primaryLogFilePath = Combine(_logsDir, $"{Sanitize(nameNoExt)}_{stamp}{ext}");
                }
                else
                {
                    _primaryLogFilePath = Combine(_logsDir, baseName);
                }

                if (Config.LogKeepLatestAlias)
                    _latestAliasPath = Combine(_logsDir, baseName);
            }

            SafeCreateDir(_configDir);
            SafeCreateDir(_logsDir);

            SafeOpenLogFile(_primaryLogFilePath, isPrimary: true);

            if (!string.IsNullOrEmpty(_latestAliasPath) &&
                !string.Equals(_latestAliasPath, _primaryLogFilePath, StringComparison.OrdinalIgnoreCase))
            {
                SafeOpenLogFile(_latestAliasPath, isPrimary: false);
            }

            Debug.Log($"[ControllerMain] Log file: {_primaryLogFilePath}");
            if (!string.IsNullOrEmpty(_latestAliasPath) && _latestAliasPath != _primaryLogFilePath)
                Debug.Log($"[ControllerMain] Latest alias: {_latestAliasPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ControllerMain] Failed to prepare logs: {ex.Message}");
        }
    }

    void SafeOpenLogFile(string path, bool isPrimary)
    {
        try
        {
            SafeCreateDir(Path.GetDirectoryName(path));
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var sw = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };

            string tag = isPrimary ? "LOG START" : "LOG START (Alias)";
            sw.WriteLine($"========== {tag} [{DateTime.Now:yyyy-MM-dd}] ==========");
            sw.WriteLine($"[PATH] Log: {path}");
            sw.WriteLine($"[TEST] Open OK at {DateTime.Now:HH:mm:ss}");
            sw.Flush();

            if (isPrimary) { _primaryStream = fs; _primaryWriter = sw; }
            else           { _aliasStream   = fs; _aliasWriter   = sw; }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ControllerMain] Could not open log file {path}: {ex.Message}");
            
            // Try persistentDataPath fallback for primary only
            if (isPrimary)
            {
                try
                {
                    var fallback = Combine(Application.persistentDataPath, "SomaticLandscapes", "Logs", Path.GetFileName(path));
                    SafeCreateDir(Path.GetDirectoryName(fallback));
                    var fs = new FileStream(fallback, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    var sw = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
                    sw.WriteLine($"========== LOG START (Fallback) [{DateTime.Now:yyyy-MM-dd}] ==========");
                    sw.WriteLine($"[PATH] Log: {fallback}");
                    sw.Flush();

                    _primaryStream = fs;
                    _primaryWriter = sw;
                    _primaryLogFilePath = fallback;
                    Debug.Log($"[ControllerMain] Using fallback log: {fallback}");
                }
                catch
                {
                    Debug.LogWarning("[ControllerMain] File logging disabled (could not open any log file)");
                    _fileLoggingEnabled = false;
                }
            }
        }
    }

    string ResolveLogsRoot(string mode)
    {
        return (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "streamingassets" => Combine(Application.dataPath, "StreamingAssets", "Logs"),
            "exedirectory"    => Combine(Path.GetDirectoryName(Application.dataPath), "Logs"),
            "persistent"      => Combine(Application.persistentDataPath, "SomaticLandscapes", "Logs"),
            _                 => Combine(_baseRoot, "Logs")
        };
    }
    void WriteLine(string lvl, string msg)
    {
        // Always echo to Unity console
        if      (lvl == "ERR") Debug.LogError($"[ControllerMain] {msg}");
        else if (lvl == "WRN") Debug.LogWarning($"[ControllerMain] {msg}");
        else                   Debug.Log($"[ControllerMain] {msg}");

        var line = $"[{DateTime.Now:HH:mm:ss}] [{lvl}] {msg}";

        // If not yet initialized, buffer
        if (!_initialized)
        {
            _earlyLog.Add(line);
            return;
        }

        TryWriteFile(line);
        AppendUILog(line);
    }

    void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (!string.IsNullOrEmpty(condition) && condition.StartsWith("[ControllerMain]")) return;

        string lvl = type switch
        {
            LogType.Error or LogType.Exception or LogType.Assert => "ERR",
            LogType.Warning => "WRN",
            _ => "LOG"
        };

        var line = $"[{DateTime.Now:HH:mm:ss}] [{lvl}] {condition}";
        if (type == LogType.Exception && !string.IsNullOrEmpty(stackTrace))
            line += Environment.NewLine + stackTrace;

        TryWriteFile(line);
        AppendUILog(line);

        // Crash marker for critical errors
        if (type == LogType.Exception
            || (!string.IsNullOrEmpty(condition) && (
                   condition.IndexOf("Crash", StringComparison.OrdinalIgnoreCase) >= 0
                || condition.IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0
                || condition.IndexOf("device lost", StringComparison.OrdinalIgnoreCase) >= 0
                || condition.IndexOf("access violation", StringComparison.OrdinalIgnoreCase) >= 0)))
        {
            TryWriteFile($"[CRASH-MARKER] {DateTime.Now:HH:mm:ss} >>> Unity reported a critical or fatal error <<<");
            if (!string.IsNullOrEmpty(stackTrace))
                TryWriteFile($"StackTrace: {stackTrace}");
            SafeFlushAll();
        }
    }

    void TryWriteFile(string line)
    {
        if (!_fileLoggingEnabled)
        {
            // Check if retry time has passed
            if (DateTime.Now >= _nextFileWriteRetry)
            {
                _fileLoggingEnabled = true;
                _fileWriteFailures = 0;
            }
            else
            {
                return; // Skip file write during backoff
            }
        }

        try
        {
            SafeRotateIfTooBig();

            _primaryWriter?.WriteLine(line);
            _aliasWriter?.WriteLine(line);

            _linesSinceFlush++;
            if (_linesSinceFlush >= Mathf.Max(1, Config.FlushEveryNLines))
            {
                SafeFlushAll();
                _linesSinceFlush = 0;
            }
        }
        catch (Exception ex)
        {
            _fileWriteFailures++;
            if (_fileWriteFailures >= 3)
            {
                // Disable file logging temporarily
                _fileLoggingEnabled = false;
                _nextFileWriteRetry = DateTime.Now.AddMinutes(1);
                Debug.LogWarning($"[ControllerMain] File logging disabled temporarily after {_fileWriteFailures} failures: {ex.Message}");
            }
        }
    }

    void SafeFlushAll()
    {
        try
        {
            _primaryWriter?.Flush();
            _aliasWriter?.Flush();
            _primaryStream?.Flush(false);
            _aliasStream?.Flush(false);
        }
        catch { /* Ignore flush failures */ }
    }

    void SafeRotateIfTooBig()
    {
        if (_primaryStream == null || _rotateBytesThreshold <= 0) return;
        try
        {
            if (_primaryStream.Length < _rotateBytesThreshold) return;

            _primaryWriter?.Flush();
            _primaryWriter?.Dispose(); _primaryStream?.Dispose();
            _primaryWriter = null; _primaryStream = null;

            var baseName = string.IsNullOrWhiteSpace(Config.LogFileName) ? "runtime.log" : Config.LogFileName.Trim();
            var nameNoExt = Path.GetFileNameWithoutExtension(baseName);
            var ext = Path.GetExtension(baseName); if (string.IsNullOrEmpty(ext)) ext = ".log";
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _primaryLogFilePath = Combine(_logsDir, $"{Sanitize(nameNoExt)}_{stamp}{ext}");

            SafeOpenLogFile(_primaryLogFilePath, isPrimary: true);

            if (!string.IsNullOrEmpty(_latestAliasPath) &&
                !string.Equals(_latestAliasPath, _primaryLogFilePath, StringComparison.OrdinalIgnoreCase))
            {
                _aliasWriter?.Flush();
                _aliasWriter?.Dispose(); _aliasStream?.Dispose();
                SafeOpenLogFile(_latestAliasPath, isPrimary: false);
            }

            SafePruneOldLogs(false);
            LogInfo($"Log rotated due to size > {Config.RotateBySizeMB} MB. New file: {_primaryLogFilePath}");
        }
        catch { }
    }

    void SafePruneOldLogs(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastPrune).TotalHours < 6) return;
        _lastPrune = DateTime.UtcNow;

        try
        {
            if (!Directory.Exists(_logsDir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-Mathf.Max(1, Config?.MaxLogDays ?? 14));
            foreach (var f in Directory.GetFiles(_logsDir, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTimeUtc < cutoff) fi.Delete();
                }
                catch { }
            }
        }
        catch { }
    }

    void CloseLogs()
    {
        try
        {
            _primaryWriter?.Flush();
            _aliasWriter?.Flush();
        }
        catch { }
        _primaryWriter?.Dispose(); _primaryStream?.Dispose();
        _aliasWriter?.Dispose();   _aliasStream?.Dispose();
        _primaryWriter = null; _primaryStream = null;
        _aliasWriter   = null; _aliasStream   = null;
    }
    void EnsureOverlayUI()
    {
        if (logPanel != null && logText != null && toggleButton != null)
        {
            SetLogPanelVisible(_uiVisible);
            toggleButton.onClick.RemoveAllListeners();
            toggleButton.onClick.AddListener(ToggleLogPanel);
            return;
        }

        var canvasObj = new GameObject("AutoLogCanvas");
        canvasObj.transform.SetParent(transform, false);
        var canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        logPanel = new GameObject("LogPanel");
        logPanel.transform.SetParent(canvas.transform, false);
        logPanel.AddComponent<Image>().color = new Color(0, 0, 0, 0.7f);

        var rect = logPanel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.01f, 0.01f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        var textObj = new GameObject("LogText", typeof(TextMeshProUGUI));
        textObj.transform.SetParent(logPanel.transform, false);
        logText = textObj.GetComponent<TMP_Text>();
        logText.fontSize = 14;
        logText.color = Color.white;
        logText.alignment = TextAlignmentOptions.TopLeft;
        logText.textWrappingMode = TextWrappingModes.Normal;
        logText.overflowMode = TextOverflowModes.Truncate;
        logText.text = Config.UIOverlayMessage ?? "";

        var textRect = logText.GetComponent<RectTransform>();
        textRect.anchorMin = new Vector2(0.02f, 0.1f);
        textRect.anchorMax = new Vector2(0.98f, 0.9f);
        textRect.offsetMin = textRect.offsetMax = Vector2.zero;

        var btnObj = new GameObject("ToggleBtn", typeof(Button), typeof(Image));
        btnObj.transform.SetParent(logPanel.transform, false);
        toggleButton = btnObj.GetComponent<Button>();
        btnObj.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

        var btnRect = btnObj.GetComponent<RectTransform>();
        btnRect.anchorMin = new Vector2(0.35f, 0.01f);
        btnRect.anchorMax = new Vector2(0.65f, 0.09f);
        btnRect.offsetMin = btnRect.offsetMax = Vector2.zero;

        var label = new GameObject("Label", typeof(TextMeshProUGUI));
        label.transform.SetParent(btnObj.transform, false);
        var labelText = label.GetComponent<TMP_Text>();
        labelText.text = "Toggle Logs (F2)";
        labelText.fontSize = 16;
        labelText.alignment = TextAlignmentOptions.Center;
        labelText.color = Color.white;
        var labelRect = labelText.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

        toggleButton.onClick.AddListener(ToggleLogPanel);
        SetLogPanelVisible(_uiVisible);
    }

    public void ToggleLogPanel() => SetLogPanelVisible(!(logPanel != null && logPanel.activeSelf));

    void SetLogPanelVisible(bool visible)
    {
        _uiVisible = visible;
        if (logPanel) logPanel.SetActive(visible);
    }

    void AppendUILog(string line)
    {
        _uiLines.Enqueue(line);
        while (_uiLines.Count > uiMaxLines)
            _uiLines.Dequeue();

        if (logText != null && logText.gameObject.activeInHierarchy)
        {
            var sb = new StringBuilder();
            var linesList = new List<string>(_uiLines);
            linesList.Reverse(); // newest first
            foreach (var l in linesList) sb.AppendLine(l);
            logText.text = sb.ToString();
        }
    }

    static KeyCode ParseKeyOrDefault(string s, KeyCode fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try { return (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true); }
        catch { return fallback; }
    }

    static string Combine(params string[] parts) =>
        Path.Combine(parts.Where(s => !string.IsNullOrEmpty(s)).ToArray()).Replace('\\', '/');

    static void SafeCreateDir(string pathLike)
    {
        if (string.IsNullOrEmpty(pathLike)) return;
        try
        {
            string dir = Path.HasExtension(pathLike) ? Path.GetDirectoryName(pathLike) : pathLike;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir.Replace('\\', '/'));
        }
        catch { }
    }

    static string Forward(string p) => string.IsNullOrEmpty(p) ? p : p.Replace('\\', '/');
    static string EnsureSlash(string p) => string.IsNullOrEmpty(p) ? p : (p.EndsWith("/") ? p : p + "/");
    static string Sanitize(string name)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name;
    }
}

