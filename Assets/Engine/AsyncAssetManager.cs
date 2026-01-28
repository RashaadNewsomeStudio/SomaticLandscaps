using System; 
using System.Collections.Generic; 
using System.IO; 
using System.Linq; 
using System.Threading; 
using System.Threading.Tasks; 
using UnityEngine; 
using UnityEngine.Networking; 
using Klak.Hap; 
using Klak.Spout; 

namespace SomaticLandscapes.Async 
{
    public class AsyncAssetManager : MonoBehaviour
    {
        public static AsyncAssetManager Instance { get; private set; }

        [Header("Configuration")]
        [SerializeField] private int maxConcurrentVideoLoads = 1;
        [SerializeField] private float videoLoadTimeoutSeconds = 10f;
        [SerializeField] private int renderTexturePoolSize = 1;
        [SerializeField] private bool enableDebugLogs = true;
        [SerializeField] private int nativeCleanupFrames = 1;

        // Pools & Locks
        private readonly Dictionary<string, Queue<RenderTexture>> _rtPool = new Dictionary<string, Queue<RenderTexture>>();
        private readonly HashSet<RenderTexture> _rtInUse = new HashSet<RenderTexture>();
        private readonly List<RenderTexture> _rtToDestroy = new List<RenderTexture>();
        
        private SemaphoreSlim _videoLoadSemaphore = new SemaphoreSlim(1, 1);
        private readonly Dictionary<HapPlayer, CancellationTokenSource> _activeLoads = new Dictionary<HapPlayer, CancellationTokenSource>();
        private readonly Dictionary<HapPlayer, SemaphoreSlim> _playerLocks = new Dictionary<HapPlayer, SemaphoreSlim>();
        private readonly object _playerLocksLock = new object();
        
        // Global Gate & Spout Control
        // Global Gate & Spout Control
        private static readonly SemaphoreSlim _hapNativeGate = new SemaphoreSlim(1, 1);
        private SpoutSender _cachedSpoutSender;

        public static bool IsBusy => Instance != null && 
            (_hapNativeGate.CurrentCount == 0 || 
             Instance._videoLoadSemaphore.CurrentCount < Instance.maxConcurrentVideoLoads || 
             Instance._activeLoads.Count > 0);

        private SpoutSender GetSpoutSender() 
        { 
            if (_cachedSpoutSender == null) _cachedSpoutSender = FindFirstObjectByType<SpoutSender>(); 
            return _cachedSpoutSender; 
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _videoLoadSemaphore = new SemaphoreSlim(maxConcurrentVideoLoads, maxConcurrentVideoLoads);
            _ = GlobalMainThreadDispatcher.Instance; // Ensure init
            LogInfo("AsyncAssetManager initialized");
        }

        void LateUpdate()
    {
        lock (_rtToDestroy)
        {
            if (_rtToDestroy.Count > 0)
            {
                foreach (var rt in _rtToDestroy) { if (rt) { rt.Release(); Destroy(rt); } }
                _rtToDestroy.Clear();
            }
        }
    }

        void OnDestroy()
        {
            foreach (var kvp in _activeLoads.ToList()) kvp.Value?.Cancel();
            _activeLoads.Clear();
            foreach (var q in _rtPool.Values) { while(q.Count>0) { var rt=q.Dequeue(); if(rt) { rt.Release(); Destroy(rt); }}}
            _rtPool.Clear(); _rtInUse.Clear();
            _videoLoadSemaphore?.Dispose();
        }

        // --- RT POOL ---

        public RenderTexture GetOrCreateRenderTexture(int width, int height, string name = "PooledRT")
        {
            string key = $"{width}x{height}_d24_ARGB32";
            if (!_rtPool.TryGetValue(key, out var q)) _rtPool[key] = q = new Queue<RenderTexture>();

            RenderTexture rt = null;
            while (q.Count > 0 && rt == null)
            {
                var c = q.Dequeue();
                if (IsValidRT(c)) {
                    if (c.width == width && c.height == height) rt = c;
                    else ScheduleDestroy(c);
                } else ScheduleDestroy(c);
            }

            if (rt == null)
            {
                string activeName = $"{name}_{key}";
                var existing = _rtInUse.FirstOrDefault(x => x.name == activeName);
                if (existing != null) return existing;

                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) {
                    name = activeName, wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false, autoGenerateMips = false, antiAliasing = 1, depth = 24
                };
                rt.Create();
                LogInfo($"Created RT: {key}");
            }
            _rtInUse.Add(rt);
            return rt;
        }

        public void ReturnRenderTexture(RenderTexture rt)
        {
            if (rt == null) return;
            _rtInUse.Remove(rt);

            if (!IsValidRT(rt)) { ScheduleDestroy(rt); return; }

            string key = $"{rt.width}x{rt.height}_d24_ARGB32";
            if (!_rtPool.ContainsKey(key)) _rtPool[key] = new Queue<RenderTexture>();

            if (_rtPool[key].Count < renderTexturePoolSize) _rtPool[key].Enqueue(rt);
            else ScheduleDestroy(rt);
        }

        public void ScheduleDestroy(RenderTexture rt) { if (rt) lock (_rtToDestroy) _rtToDestroy.Add(rt); }

        private bool IsValidRT(RenderTexture rt)
        {
            return rt != null && rt.IsCreated() && rt.depth >= 24 && 
                   rt.format == RenderTextureFormat.ARGB32 && rt.antiAliasing == 1 && !rt.useMipMap;
        }

        // --- LOAD/RELEASE LOGIC ---

        private SemaphoreSlim GetPlayerLock(HapPlayer p)
        {
            lock (_playerLocksLock) {
                if (!_playerLocks.ContainsKey(p)) _playerLocks[p] = new SemaphoreSlim(1, 1);
                return _playerLocks[p];
            }
        }

        /// <summary>
        /// Museum-grade playback handshake: Ensures time actually advances or fails hard.
        /// Waits for real frame progression (not Task.Delay) so Unity player loop runs.
        /// </summary>
        private async Task<bool> EnsurePlaybackAdvancesAsync(HapPlayer hp, string label, CancellationToken ct)
        {
            float t0 = 0f, t1 = 0f;

            await AsyncExtensions.RunOnMainThread(() =>
            {
                try { hp.time = 0f; hp.speed = 1f; } catch {}
                try { t0 = (float)hp.time; } catch { t0 = 0f; }
            }, ct);

            // Wait a few frames (not Task.Delay) so Unity player loop runs
            for (int i = 0; i < 20; i++)
            {
                await AsyncExtensions.WaitForEndOfFrame(ct);
                await AsyncExtensions.RunOnMainThread(() =>
                {
                    try { t1 = (float)hp.time; } catch { t1 = t0; }
                }, ct);

                if (t1 > t0 + 0.01f) return true;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LogWarning($"Playback did not start for {label} (t0={t0:0.000}, t1={t1:0.000}).");
#endif
            return false;
        }

        public async Task<bool> LoadVideoAsync(string path, HapPlayer player, CancellationToken ct = default, Action onDetach = null, Action onSuccess = null)
        {
            if (!player || string.IsNullOrEmpty(path)) return false;

            var playerLock = GetPlayerLock(player);
            await playerLock.WaitAsync(ct);
            try
            {
                lock (_playerLocksLock) {
                    if (_activeLoads.ContainsKey(player)) { _activeLoads[player]?.Cancel(); _activeLoads.Remove(player); }
                }
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_playerLocksLock) _activeLoads[player] = cts;

                try
                {
                    await _videoLoadSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        if (!await Task.Run(() => File.Exists(path), cts.Token)) return false;

                        // === FIRST ATTEMPT ===
                        bool firstTrySuccess = await AttemptOpenAndValidateAsync(player, path, cts.Token, onDetach, onSuccess);
                        
                        if (firstTrySuccess)
                        {
                            LogInfo($"Loaded: {Path.GetFileName(path)}");
                            return true;
                        }

                        // === RETRY ONCE (decoder warm-up issue) ===
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        LogWarning($"First load attempt failed for {Path.GetFileName(path)}, retrying once...");
#endif
                        // FIX 1 & 2: Use Native Gate wrapper for retry close
                        await ExecuteNativeOpAsync(async () => {
                            await AsyncExtensions.RunOnMainThread(() => { try { player.Close(); } catch {} }, cts.Token);
                            await AsyncExtensions.WaitForSecondsRealtime(0.2f, cts.Token);
                        }, cts.Token);

                        bool secondTrySuccess = await AttemptOpenAndValidateAsync(player, path, cts.Token, onDetach, onSuccess);
                        
                        if (secondTrySuccess)
                        {
                            LogInfo($"Loaded: {Path.GetFileName(path)} (retry succeeded)");
                            return true;
                        }

                        // === BOTH ATTEMPTS FAILED → QUARANTINE ===
                        return false;
                    }
                    finally { _videoLoadSemaphore.Release(); }
                }
                catch (Exception
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    e
#endif
                ) { 
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    LogWarning($"Load error: {e.Message}");
#endif
                    await AsyncExtensions.RunOnMainThread(() => { try { player.Close(); } catch {} });
                    return false; 
                }
                finally { lock (_playerLocksLock) _activeLoads.Remove(player); cts.Dispose(); }
            }
            finally { playerLock.Release(); }
        }

        /// <summary>
        /// Single open-validate attempt with strict handshake
        /// </summary>
        /// <summary>
        /// Single open-validate attempt with strict handshake
        /// </summary>
        private async Task<bool> AttemptOpenAndValidateAsync(HapPlayer player, string path, CancellationToken ct, Action onDetach, Action onSuccess)
        {
            return await ExecuteNativeOpAsync(async () => {
                await AsyncExtensions.RunOnMainThread(() => { onDetach?.Invoke(); if (player) player.targetTexture = null; }, ct);
                
                if (player) {
                    await AsyncExtensions.RunOnMainThread(() => player.Close(), ct);
                    await AsyncExtensions.WaitFrames(nativeCleanupFrames, ct);
                }
                if (player) {
                    await AsyncExtensions.RunOnMainThread(() => {
                        player.Open(path);
                        // FIX 5: Ensure component enabled after Open
                        if (player) player.enabled = true; 
                    }, ct);
                    await AsyncExtensions.WaitFrames(2, ct); // Micro-cooldown (Fix 6)
                }
                
                // Scope RT assignment inside gate (Fix 2 refinement)
                if (onSuccess != null) await AsyncExtensions.RunOnMainThread(onSuccess, ct);

                // Wait for Valid (inside gate to prevent races)
                var timeout = DateTime.UtcNow.AddSeconds(videoLoadTimeoutSeconds);
                bool opened = false;
                while (!opened && DateTime.UtcNow < timeout) {
                    if (ct.IsCancellationRequested) break;
                    opened = await AsyncExtensions.RunOnMainThread(() => player.isValid && player.streamDuration > 0.1f, ct);
                    if (!opened) await AsyncExtensions.WaitForEndOfFrame(ct);
                }

                if (!opened) return false;

                // === STRICT START HANDSHAKE (Safe inside gate) ===
                bool started = await EnsurePlaybackAdvancesAsync(player, Path.GetFileName(path), ct);
                return started;

            }, ct);
        }

        public async Task ReleaseVideoAsync(HapPlayer player, CancellationToken ct = default, Action onDetach = null)
        {
            if (!player) return;
            var playerLock = GetPlayerLock(player);
            await playerLock.WaitAsync(ct);
            try
            {
                lock (_playerLocksLock) {
                    if (_activeLoads.ContainsKey(player)) { _activeLoads[player]?.Cancel(); return; } // Let loader handle Close
                }

                try
                {
                    await ExecuteNativeOpAsync(async () => {
                        await AsyncExtensions.RunOnMainThread(() => { 
                            onDetach?.Invoke(); 
                            if (player) player.targetTexture = null; 
                        }, ct);
                        
                        // Wait for pipe clear
                        await AsyncExtensions.WaitFrames(1, ct);

                        await AsyncExtensions.RunOnMainThread(() => player.Close(), ct);
                        await AsyncExtensions.WaitFrames(nativeCleanupFrames, ct);
                    }, ct);
                }
                catch (Exception e) { LogWarning($"Release error: {e.Message}"); }
            }
            finally { playerLock.Release(); }
        }

        /// <summary>
        /// ProFix3: Global gate wrapper that pauses Spout/DXGI sharing during native operations
        /// </summary>
        /// <summary>
        /// ProFix3: Global gate wrapper that pauses Spout/DXGI sharing during native operations
        /// </summary>
        private async Task<bool> ExecuteNativeOpAsync(Func<Task<bool>> op, CancellationToken ct) // Overload for methods returning bool
        {
             bool result = false;
             await ExecuteNativeOpAsync(async () => { result = await op(); }, ct);
             return result;
        }

        private async Task ExecuteNativeOpAsync(Func<Task> op, CancellationToken ct)
        {
            await _hapNativeGate.WaitAsync(ct);
            bool spoutPaused = false;
            SpoutSender sender = null;
            try
            {
                // Pause Spout (SAFE PAUSE)
                if (Instance)
                {
                    // Use new Paused property instead of enabling/disabling component
                    // Safe MainThread lookup of SpoutSender
                    await AsyncExtensions.RunOnMainThread(() => {
                        sender = GetSpoutSender();
                        if (sender) sender.Paused = true;
                    }, ct);
                    
                    if (sender) 
                    {
                        spoutPaused = true;
                        // Barrier BEFORE: Wait 2 frames to ensure GPU queue handles the pause
                        await AsyncExtensions.WaitForEndOfFrame(ct);
                        await AsyncExtensions.WaitForEndOfFrame(ct);
                    }
                }

                // Execute Native Op
                await op();

                // Barrier AFTER: Wait before resuming
                await AsyncExtensions.WaitForEndOfFrame(ct);
                await AsyncExtensions.WaitForEndOfFrame(ct);

                // Resume Spout
                if (spoutPaused && sender)
                {
                    await AsyncExtensions.RunOnMainThread(() => { if (sender) sender.Paused = false; }, ct);
                }
            }
            finally
            {
                // Crash safety: ensure Spout resumes if loop aborted
                if (spoutPaused && sender)
                {
                    try {
                         GlobalMainThreadDispatcher.Enqueue(() => { if (sender) sender.Paused = false; });
                    } catch {}
                }
                _hapNativeGate.Release();
            }
        }

        public void ReleaseVideo(HapPlayer p) => _ = ReleaseVideoAsync(p, CancellationToken.None);

        public async Task<bool> LoadAndAssignVideoAsync(string fullPath, HapPlayer hp, int w, int h, string rtName, RenderTexture manualRT, UnityEngine.UI.RawImage targetImg, bool isLooping, CancellationToken ct, Action<RenderTexture> onRtAssigned = null)
        {
             // LEAK FIX: Capture old RT reference before we potentially lose it/replace it
             RenderTexture oldRT = (hp != null) ? hp.targetTexture : null;

             return await LoadVideoAsync(fullPath, hp, ct, 
                () => { if (targetImg) targetImg.texture = null; }, 
                () => {
                    hp.loop = isLooping; hp.speed = 1f; hp.time = 0f;
                    RenderTexture rt = manualRT;
                    if (rt == null) rt = GetOrCreateRenderTexture(w, h, rtName);
                    
                    // LEAK FIX: release the old texture back to pool if we are replacing it
                    if (oldRT != null && oldRT != rt)
                    {
                         ReturnRenderTexture(oldRT);
                    }

                    hp.targetTexture = rt;
                    if (targetImg) targetImg.texture = rt;
                    onRtAssigned?.Invoke(rt);
                });
        }

        // --- DISCOVERY & AUDIO ---

        public async Task<string[]> DiscoverVideosAsync(string folder, string pattern="*.mov", CancellationToken ct=default)
        {
            if (string.IsNullOrEmpty(folder)) return Array.Empty<string>();
            try {
                return await Task.Run(() => Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>(), ct);
            } catch { return Array.Empty<string>(); }
        }

        public async Task<AudioClip> LoadAudioAsync(string path, AudioType type = AudioType.MPEG, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try {
                if (!await Task.Run(() => File.Exists(path), ct)) return null;
                AudioClip clip = null;
                await AsyncExtensions.RunOnMainThread(async () => {
                    using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type)) {
                        await www.SendWebRequest().ToTask(ct);
                        if (www.result == UnityWebRequest.Result.Success) {
                            clip = DownloadHandlerAudioClip.GetContent(www);
                            clip.name = Path.GetFileNameWithoutExtension(path);
                        }
                    }
                }, ct);
                return clip;
            } catch { return null; }
        }

        private void LogInfo(string m) { if(enableDebugLogs) Debug.Log($"[AsyncAssetManager] {m}"); }
        private void LogWarning(string m) { Debug.LogWarning($"[AsyncAssetManager] {m}"); }
        private void LogError(string m) { Debug.LogError($"[AsyncAssetManager] {m}"); }
    }
}
