using System; 
using System.Collections.Generic; 
using System.IO; 
using System.Linq; 
using System.Threading; 
using System.Threading.Tasks; 
using UnityEngine; 
using UnityEngine.Networking; 
using Klak.Hap; 

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
        
        // Global Gate
        private static readonly SemaphoreSlim _hapNativeGate = new SemaphoreSlim(1, 1);

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
                        await _hapNativeGate.WaitAsync(cts.Token);
                        try
                        {
                            await AsyncExtensions.RunOnMainThread(() => { try { player.Close(); } catch {} }, cts.Token);
                            await AsyncExtensions.WaitForSecondsRealtime(0.2f, cts.Token);
                        }
                        finally { _hapNativeGate.Release(); }

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
        private async Task<bool> AttemptOpenAndValidateAsync(HapPlayer player, string path, CancellationToken ct, Action onDetach, Action onSuccess)
        {
            await _hapNativeGate.WaitAsync(ct);
            try
            {
                await AsyncExtensions.RunOnMainThread(() => { onDetach?.Invoke(); if (player) player.targetTexture = null; }, ct);
                await AsyncExtensions.WaitForEndOfFrame(ct);
                await AsyncExtensions.WaitFrames(1, ct); // GPU Barrier

                if (player) {
                    await AsyncExtensions.RunOnMainThread(() => player.Close(), ct);
                    await AsyncExtensions.WaitFrames(nativeCleanupFrames, ct);
                }
                if (player) {
                    await AsyncExtensions.RunOnMainThread(() => player.Open(path), ct);
                    await AsyncExtensions.WaitFrames(nativeCleanupFrames, ct);
                }
            }
            finally { _hapNativeGate.Release(); }

            // Wait for Valid
            var timeout = DateTime.UtcNow.AddSeconds(videoLoadTimeoutSeconds);
            bool opened = false;
            while (!opened && DateTime.UtcNow < timeout) {
                ct.ThrowIfCancellationRequested();
                opened = await AsyncExtensions.RunOnMainThread(() => player.isValid && player.streamDuration > 0.1f, ct);
                if (!opened) await Task.Yield();
            }

            if (!opened) return false;

            // Apply texture setup
            if (onSuccess != null) await AsyncExtensions.RunOnMainThread(onSuccess, ct);

            // === STRICT START HANDSHAKE ===
            bool started = await EnsurePlaybackAdvancesAsync(player, Path.GetFileName(path), ct);
            return started;
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
                    await AsyncExtensions.RunOnMainThread(() => { 
                        onDetach?.Invoke(); 
                        if (player) player.targetTexture = null; 
                    }, ct);
                    
                    await AsyncExtensions.WaitForEndOfFrame(ct); 
                    await AsyncExtensions.WaitFrames(1, ct);

                    await _hapNativeGate.WaitAsync(ct);
                    try {
                        await AsyncExtensions.RunOnMainThread(() => player.Close(), ct);
                    } finally { _hapNativeGate.Release(); }
                    
                    await AsyncExtensions.WaitFrames(nativeCleanupFrames, ct);
                }
                catch (Exception e) { LogWarning($"Release error: {e.Message}"); }
            }
            finally { playerLock.Release(); }
        }

        public void ReleaseVideo(HapPlayer p) => _ = ReleaseVideoAsync(p, CancellationToken.None);

        public async Task<bool> LoadAndAssignVideoAsync(string fullPath, HapPlayer hp, int w, int h, string rtName, RenderTexture manualRT, UnityEngine.UI.RawImage targetImg, bool isLooping, CancellationToken ct, Action<RenderTexture> onRtAssigned = null)
        {
             return await LoadVideoAsync(fullPath, hp, ct, 
                () => { if (targetImg) targetImg.texture = null; }, 
                () => {
                    hp.loop = isLooping; hp.speed = 1f; hp.time = 0f;
                    RenderTexture rt = manualRT;
                    if (rt == null) rt = GetOrCreateRenderTexture(w, h, rtName);
                    
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
