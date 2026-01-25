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
    /// <summary>
    /// Central async asset management system with memory pooling and background loading
    /// </summary>
    public class AsyncAssetManager : MonoBehaviour
    {
        public static AsyncAssetManager Instance { get; private set; }

        [Header("Configuration")]
        [SerializeField] private int maxConcurrentVideoLoads = 1; // PRODUCTION FIX: One video at a time for 8K HAP stability
        [SerializeField] private float videoLoadTimeoutSeconds = 10f;
        
        [Header("GPU Safety")]
        [SerializeField] private int gpuBarrierFrames = 2; // Frames to wait for GPU finish (2=normal, 4=stress build)
        [SerializeField] private int nativeCleanupFrames = 1; // Frames to wait after Close/Open

        [SerializeField] private int renderTexturePoolSize = 1; // 5K TEXTURES ARE HUGE. POOL SIZE 1 IS MAX SAFE.
        [SerializeField] private bool enableDebugLogs = true;

        // RenderTexture pool to prevent VRAM fragmentation
        private readonly Dictionary<string, Queue<RenderTexture>> _rtPool = new Dictionary<string, Queue<RenderTexture>>();
        private readonly HashSet<RenderTexture> _rtInUse = new HashSet<RenderTexture>();
        
        // Load queue management
        private SemaphoreSlim _videoLoadSemaphore = new SemaphoreSlim(1, 1);
        private readonly Dictionary<HapPlayer, CancellationTokenSource> _activeLoads = new Dictionary<HapPlayer, CancellationTokenSource>();
        
        // PRODUCTION FIX: Per-player operation lock to prevent interleaving
        // Prevents ReleaseVideo fire-and-forget from racing with LoadVideoAsync on same player
        private readonly Dictionary<HapPlayer, SemaphoreSlim> _playerLocks = new Dictionary<HapPlayer, SemaphoreSlim>();
        private readonly object _playerLocksLock = new object();
        
        // GLOBAL NATIVE GATE: Serialize ALL HAP Open/Close operations across all instances.
        // This is critical for plugins that are not thread-safe.
        private static readonly SemaphoreSlim _hapNativeGate = new SemaphoreSlim(1, 1);
        private static int _hapOpsInFlight = 0; // SANITY CHECK
        
        // Cleanup tracking
        private readonly List<RenderTexture> _rtToDestroy = new List<RenderTexture>();

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            
            _videoLoadSemaphore = new SemaphoreSlim(maxConcurrentVideoLoads, maxConcurrentVideoLoads);
            
            // CRITICAL FIX: Ensure Dispatcher exists on Main Thread before any async calls
            var dispatcher = UnityMainThreadDispatcher.Instance;
            
            LogInfo("AsyncAssetManager initialized");
        }

        void LateUpdate()
        {
            // Cleanup deferred RenderTextures on main thread
            if (_rtToDestroy.Count > 0)
            {
                foreach (var rt in _rtToDestroy)
                {
                    if (rt != null)
                    {
                        try
                        {
                            rt.Release();
                            Destroy(rt);
                        }
                        catch (Exception ex)
                        {
                            LogWarning($"Error destroying RenderTexture: {ex.Message}");
                        }
                    }
                }
                _rtToDestroy.Clear();
            }
        }

        void OnDestroy()
        {
            // Cancel all active loads
            foreach (var kvp in _activeLoads.ToList())
            {
                kvp.Value?.Cancel();
            }
            _activeLoads.Clear();

            // Cleanup all pooled RenderTextures
            foreach (var queue in _rtPool.Values)
            {
                while (queue.Count > 0)
                {
                    var rt = queue.Dequeue();
                    if (rt != null)
                    {
                        rt.Release();
                        Destroy(rt);
                    }
                }
            }
            _rtPool.Clear();
            _rtInUse.Clear();

            _videoLoadSemaphore?.Dispose();
        }

        #region RenderTexture Pool Management

        /// <summary>
        /// Get or create a RenderTexture from the pool
        /// CRITICAL: Validate pooled RTs before reuse
        /// </summary>
        public RenderTexture GetOrCreateRenderTexture(int width, int height, string name = "PooledRT")
        {
            // Standardize requirements
            const int RequiredDepth = 24;
            var requiredFormat = RenderTextureFormat.ARGB32;

            string key = $"{width}x{height}_d{RequiredDepth}_{requiredFormat}";
            
            if (!_rtPool.TryGetValue(key, out var q))
                _rtPool[key] = q = new Queue<RenderTexture>();

            RenderTexture rt = null;

            // CRITICAL FIX: Validate candidates from pool before reuse
            while (q.Count > 0 && rt == null)
            {
                var candidate = q.Dequeue();

                // Candidate might have been destroyed/released
                if (candidate == null)
                {
                    LogWarning("Dequeued null RT from pool, skipping");
                    continue;
                }
                
                if (!candidate.IsCreated())
                {
                    LogWarning($"Dequeued RT not created (ID:{candidate.GetInstanceID()}), destroying");
                    ScheduleDestroy(candidate);
                    continue;
                }
                
                if (candidate.depth < RequiredDepth)
                {
                    LogWarning($"Dequeued RT depth={candidate.depth} < {RequiredDepth} (ID:{candidate.GetInstanceID()}), destroying");
                    ScheduleDestroy(candidate);
                    continue;
                }
                
                if (candidate.format != requiredFormat)
                {
                    LogWarning($"Dequeued RT format={candidate.format} != {requiredFormat} (ID:{candidate.GetInstanceID()}), destroying");
                    ScheduleDestroy(candidate);
                    continue;
                }
                
                if (candidate.width != width || candidate.height != height)
                {
                    LogWarning($"Dequeued RT size={candidate.width}x{candidate.height} != {width}x{height} (ID:{candidate.GetInstanceID()}), destroying");
                    ScheduleDestroy(candidate);
                    continue;
                }
                
                if (candidate.antiAliasing != 1)
                {
                    LogWarning($"Dequeued RT AA={candidate.antiAliasing} != 1 (ID:{candidate.GetInstanceID()}), destroying");
                    ScheduleDestroy(candidate);
                    continue;
                }
                
                if (candidate.useMipMap)
                {
                    LogWarning($"Dequeued RT has mipmaps (ID:{candidate.GetInstanceID()}), destroying");
                    ScheduleDestroy(candidate);
                    continue;
                }

                // Passed all validations - use this RT
                rt = candidate;
                LogInfo($"Reusing validated RenderTexture: {key} (ID:{rt.GetInstanceID()}, depth:{rt.depth}, created:{rt.IsCreated()})");
            }

            if (rt == null)
            {
                // CRITICAL FIX: Check active list for existing RT with same unique name to prevent duplicates (VRAM Leak)
                // This handles cases where logic keeps RTs alive (Stability Mode) but requests them again.
                string activeName = $"{name}_{key}";
                var existing = _rtInUse.FirstOrDefault(x => x.name == activeName);
                if (existing != null)
                {
                     // LogInfo($"Reusing in-use RenderTexture: {activeName} (ID:{existing.GetInstanceID()})"); // Verbose
                     return existing;
                }

                // Create fresh RT
                rt = new RenderTexture(width, height, RequiredDepth, requiredFormat)
                {
                    name = activeName,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    antiAliasing = 1,
                    depth = RequiredDepth
                };
                rt.Create();
                LogInfo($"Created new RenderTexture: {key} (ID:{rt.GetInstanceID()}, depth:{rt.depth})");
            }

            _rtInUse.Add(rt);
            return rt;
        }

        /// <summary>
    /// Return a RenderTexture to the pool for reuse
    /// CRITICAL: Only pool RTs matching pipeline requirements (depth 24, ARGB32, AA=1, no mipmaps)
    /// </summary>
    public void ReturnRenderTexture(RenderTexture rt)
    {
        if (rt == null) return;

        _rtInUse.Remove(rt);
        
        // CRITICAL FIX: Validate RT descriptor before pooling
        // Reject any RT that doesn't match pipeline requirements
        if (rt.depth < 24)
        {
            LogWarning($"Rejecting RT from pool: depth={rt.depth} < 24 (destroying instead)");
            ScheduleDestroy(rt);
            return;
        }
        
        if (rt.format != RenderTextureFormat.ARGB32)
        {
            LogWarning($"Rejecting RT from pool: format={rt.format} != ARGB32 (destroying instead)");
            ScheduleDestroy(rt);
            return;
        }
        
        if (rt.antiAliasing != 1)
        {
            LogWarning($"Rejecting RT from pool: antiAliasing={rt.antiAliasing} != 1 (destroying instead)");
            ScheduleDestroy(rt);
            return;
        }
        
        if (rt.useMipMap)
        {
            LogWarning($"Rejecting RT from pool: useMipMap=true (destroying instead)");
            ScheduleDestroy(rt);
            return;
        }
        
        if (!rt.IsCreated())
        {
            LogWarning($"Rejecting RT from pool: not created (destroying instead)");
            ScheduleDestroy(rt);
            return;
        }

        string key = $"{rt.width}x{rt.height}_d24_ARGB32";  // Fixed descriptor key

        if (!_rtPool.ContainsKey(key))
        {
            _rtPool[key] = new Queue<RenderTexture>();
        }

        // Limit pool size
        if (_rtPool[key].Count < renderTexturePoolSize)
        {
            _rtPool[key].Enqueue(rt);
            LogInfo($"Returned RenderTexture to pool: {key} (depth={rt.depth})");
        }
        else
        {
            // Pool full, destroy
            ScheduleDestroy(rt);
            LogInfo($"Pool full, destroying RenderTexture: {key}");
        }
    }

        private void ScheduleDestroy(RenderTexture rt)
        {
            if (rt == null) return;
            LogInfo($"Scheduled RT for destruction: {rt.width}x{rt.height}");
        }

        /// <summary>
        /// Get or create a per-player operation lock
        /// PRODUCTION: Prevents LoadVideo/ReleaseVideo operations from interleaving on same player
        /// </summary>
        private SemaphoreSlim GetPlayerLock(HapPlayer player)
        {
            lock (_playerLocksLock)
            {
                if (!_playerLocks.ContainsKey(player))
                {
                    _playerLocks[player] = new SemaphoreSlim(1, 1);
                }
                return _playerLocks[player];
            }
        }

        /// <summary>
        /// Release and cleanup per-player lock if no longer needed
        /// </summary>
        private void ReleasePlayerLock(HapPlayer player)
        {
            lock (_playerLocksLock)
            {
                if (_playerLocks.ContainsKey(player))
                {
                    var sem = _playerLocks[player];
                    // Only dispose if no waiters and not currently held
                    if (sem.CurrentCount == 1)
                    {
                        _playerLocks.Remove(player);
                        sem.Dispose();
                    }
                }
            }
        }

        // ==========================================
        // VIDEO LOADING
        // ==========================================

        /// <summary>
        /// Load video asynchronously with cancellation support
        /// PRODUCTION: Per-player locked to prevent interleaving with ReleaseVideo
        /// </summary>
        public async Task<bool> LoadVideoAsync(string path, HapPlayer player, CancellationToken ct = default, Action onDetach = null)
        {
            if (player == null)
            {
                LogWarning("LoadVideoAsync: player is null");
                return false;
            }

            if (string.IsNullOrEmpty(path))
            {
                LogWarning("LoadVideoAsync: path is null or empty");
                return false;
            }

            // PRODUCTION: Acquire per-player lock to prevent interleaving
            var playerLock = GetPlayerLock(player);
            await playerLock.WaitAsync(ct);
            
            try
            {
                // Cancel any existing load for this player
                lock (_playerLocksLock)
                {
                    if (_activeLoads.ContainsKey(player))
                    {
                        _activeLoads[player]?.Cancel();
                        _activeLoads.Remove(player);
                    }
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_playerLocksLock)
                {
                    _activeLoads[player] = cts;
                }

                try
                {
                    // Wait for available slot (prevent too many concurrent loads)
                    await _videoLoadSemaphore.WaitAsync(cts.Token);

                    try
                    {
                        LogInfo($"Loading video: {Path.GetFileName(path)}");

                        // Verify file exists (on background thread)
                        bool fileExists = await Task.Run(() => File.Exists(path), cts.Token);
                        if (!fileExists)
                        {
                            LogWarning($"Video file not found: {path}");
                            return false;
                        }

                        // GLOBAL NATIVE GATE: Serialized native access
                        await _hapNativeGate.WaitAsync(cts.Token);
                        try
                        {
                            // SANITY CHECK
                            int ops = Interlocked.Increment(ref _hapOpsInFlight);
                            if (ops > 1) LogError($"CRITICAL: NATIVE OVERLAP DETECTED (ops={ops}) - GATE FAILURE!");

                            // Step 1: Detach texture on main thread (Stop new GPU work)
                            await AsyncExtensions.RunOnMainThread(() =>
                            {
                                // Detach downstream consumers (RawImages) first
                                onDetach?.Invoke();
                                
                                if (player != null) player.targetTexture = null;
                            }, cts.Token);
                            
                            // Step 2: GPU Barrier (Wait for EndOfFrame + 1 frame)
                            // This ensures the GPU has finished using the texture we just detached
                            await AsyncExtensions.WaitForEndOfFrame(cts.Token);
                            await AsyncExtensions.WaitFrames(1, cts.Token);
                            
                            // Step 3: Close existing stream on Main Thread (Native operation)
                            if (player != null)
                            {
                                await AsyncExtensions.RunOnMainThread(() => player.Close(), cts.Token);
                                
                                // Frame barrier after close (Native cleanup)
                                if (nativeCleanupFrames > 0)
                                    await AsyncExtensions.WaitFrames(nativeCleanupFrames, cts.Token);
                            }

                            // Step 4: Open new stream on Main Thread (Native operation)
                            if (player != null)
                            {
                                await AsyncExtensions.RunOnMainThread(() => player.Open(path), cts.Token);
                                
                                // Wait for Open to fully initialize (Native init)
                                await AsyncExtensions.WaitFrames(nativeCleanupFrames, cts.Token);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _hapOpsInFlight);
                            _hapNativeGate.Release();
                        }


                        bool opened = false;
                        var timeout = TimeSpan.FromSeconds(videoLoadTimeoutSeconds);
                        var startTime = DateTime.UtcNow;

                        while (!opened && (DateTime.UtcNow - startTime) < timeout)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            
                            await Task.Yield();
                            
                            // Check if player is ready (on main thread)
                            // FIX: Stronger validation (IsValid + Duration)
                            opened = await AsyncExtensions.RunOnMainThread(() => 
                            {
                                return player.isValid && player.streamDuration > 0.1f;
                            }, cts.Token);
                        }

                        // Extra safety: Wait for 2 frames to ensure texture is uploaded
                        await AsyncExtensions.WaitForSecondsRealtime(0.05f, cts.Token);

                        if (!opened)
                        {
                            LogWarning($"Video load timeout: {Path.GetFileName(path)}");
                            return false;
                        }

                        LogInfo($"Video loaded successfully: {Path.GetFileName(path)} ({player.streamDuration:F2}s, gpuBarrier={gpuBarrierFrames} frames)");
                        return true;
                    }
                    finally
                    {
                        _videoLoadSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    LogInfo($"Video load cancelled: {Path.GetFileName(path)}");
                    // CRITICAL VALIDATION: If cancelled, we must close to prevent leaks/native state corruption
                    await AsyncExtensions.RunOnMainThread(() => { try { player.Close(); } catch {} });
                    return false;
                }
                catch (Exception ex)
                {
                    LogWarning($"Video load error: {ex.Message}");
                    // Ensure clean state
                    await AsyncExtensions.RunOnMainThread(() => { try { player.Close(); } catch {} });
                    return false;
                }
                finally
                {
                    lock (_playerLocksLock)
                    {
                        _activeLoads.Remove(player);
                    }
                    cts?.Dispose();
                }
            }
            finally
            {
                // PRODUCTION: Always release per-player lock
                playerLock.Release();
            }
        }

        /// <summary>
        /// Release video and cleanup resources (synchronous wrapper)
        /// </summary>
        public void ReleaseVideo(HapPlayer player)
        {
            // Fire-and-forget the async version
            _ = ReleaseVideoAsync(player, CancellationToken.None);
        }

        /// <summary>
        /// Release video and cleanup resources with GPU-safe barriers
        /// PRODUCTION: Per-player locked to prevent interleaving with LoadVideoAsync
        /// </summary>
        public async Task ReleaseVideoAsync(HapPlayer player, CancellationToken ct = default, Action onDetach = null)
        {
            if (player == null) return;

            // PRODUCTION: Acquire per-player lock (same as LoadVideoAsync)
            var playerLock = GetPlayerLock(player);
            await playerLock.WaitAsync(ct);
            
            try
            {
                bool wasLoading = false;

                // Cancel any active load
                lock (_playerLocksLock)
                {
                    if (_activeLoads.ContainsKey(player))
                    {
                        _activeLoads[player]?.Cancel();
                        wasLoading = true;
                    }
                }

                // If we were loading, the loading task will handle Close() safely
                if (wasLoading)
                {
                    LogInfo($"ReleaseVideo: Cancelling active load for player (delegating Close to task).");
                    return;
                }

                // PRODUCTION FIX: Non-loading case MUST also use GPU-safe sequence
                // Before: Just called Close() immediately → crashes if GPU still using texture
                // After: Detach + wait + Close with frame barriers
                
                try
                {
                    // Step 1: Detach texture on main thread
                    await AsyncExtensions.RunOnMainThread(() =>
                    {
                        try
                        {
                            // Detach downstream consumers (RawImages) first
                            onDetach?.Invoke();
                            
                            if (player != null && player.targetTexture != null)
                            {
                                player.targetTexture = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogWarning($"Error detaching texture: {ex.Message}");
                        }
                    }, ct);
                    
                    // Step 2: GPU Barrier (Wait for EndOfFrame + 1 frame)
                    // This ensures the GPU has finished using the texture we just detached
                    await AsyncExtensions.WaitForEndOfFrame(ct);
                    await AsyncExtensions.WaitFrames(1, ct);
                    
                    // GLOBAL NATIVE GATE: Serialized native access
                    await _hapNativeGate.WaitAsync(ct);
                    try
                    {
                        int ops = Interlocked.Increment(ref _hapOpsInFlight);
                        if (ops > 1) LogError($"CRITICAL: NATIVE OVERLAP DETECTED (ops={ops}) - GATE FAILURE!");

                        // Step 3: Close on main thread
                        await AsyncExtensions.RunOnMainThread(() =>
                        {
                            try
                            {
                                player.Close();
                                LogInfo("Player closed safely (GPU-synchronized).");
                            }
                            catch (Exception ex)
                            {
                                LogWarning($"Error closing player: {ex.Message}");
                            }
                        }, ct);
                        
                        // Step 4: Wait for native cleanup (CONFIGURABLE)
                        await AsyncExtensions.WaitFrames(nativeCleanupFrames, ct);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _hapOpsInFlight);
                        _hapNativeGate.Release();
                    }
                    
                    // Step 4: Wait for native cleanup (CONFIGURABLE)
                    await AsyncExtensions.WaitFrames(nativeCleanupFrames, ct);
                }
                catch (OperationCanceledException)
                {
                    LogInfo("ReleaseVideo cancelled.");
                }
                catch (Exception ex)
                {
                    LogWarning($"ReleaseVideo error: {ex.Message}");
                }
            }
            finally
            {
                // PRODUCTION: Always release per-player lock
                playerLock.Release();
            }
        }


        #endregion

        #region Audio Loading

        /// <summary>
        /// Load audio clip asynchronously
        /// </summary>
        public async Task<AudioClip> LoadAudioAsync(string path, AudioType audioType = AudioType.MPEG, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(path))
            {
                LogWarning("LoadAudioAsync: path is null or empty");
                return null;
            }

            try
            {
                // Verify file exists (on background thread)
                bool fileExists = await Task.Run(() => File.Exists(path), ct);
                if (!fileExists)
                {
                    LogWarning($"Audio file not found: {path}");
                    return null;
                }

                LogInfo($"Loading audio: {Path.GetFileName(path)}");

                // Load audio using UnityWebRequest (must be on main thread)
                AudioClip clip = null;
                await AsyncExtensions.RunOnMainThread(async () =>
                {
                    using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, audioType))
                    {
                        var operation = www.SendWebRequest();
                        await operation.ToTask(ct);

                        if (www.result == UnityWebRequest.Result.Success)
                        {
                            clip = DownloadHandlerAudioClip.GetContent(www);
                            clip.name = Path.GetFileNameWithoutExtension(path);
                            LogInfo($"Audio loaded: {clip.name}");
                        }
                        else
                        {
                            LogWarning($"Audio load failed: {www.error}");
                        }
                    }
                }, ct);

                return clip;
            }
            catch (OperationCanceledException)
            {
                LogInfo($"Audio load cancelled: {Path.GetFileName(path)}");
                return null;
            }
            catch (Exception ex)
            {
                LogWarning($"Audio load error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region File Discovery

        /// <summary>
        /// Discover video files asynchronously
        /// </summary>
        public async Task<string[]> DiscoverVideosAsync(string folderPath, string pattern = "*.mov", CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(folderPath))
                return Array.Empty<string>();

            try
            {
                // Run file discovery on background thread
                var files = await Task.Run(() =>
                {
                    if (!Directory.Exists(folderPath))
                        return Array.Empty<string>();

                    ct.ThrowIfCancellationRequested();
                    return Directory.GetFiles(folderPath, pattern, SearchOption.TopDirectoryOnly);
                }, ct);

                LogInfo($"Discovered {files.Length} videos in {folderPath}");
                return files;
            }
            catch (OperationCanceledException)
            {
                LogInfo($"Video discovery cancelled: {folderPath}");
                return Array.Empty<string>();
            }
            catch (Exception ex)
            {
                LogWarning($"Video discovery error: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        #endregion

        // ==========================================
        // UNIFIED LOADING HELPER (Refactoring)
        // ==========================================

        /// <summary>
        /// Unified helper to Load + Configure + Assign RTs (Removes duplication between Idle/Active flows)
        /// </summary>
        public async Task<bool> LoadAndAssignVideoAsync(
            string fullPath, 
            HapPlayer hp, 
            int w, int h, 
            string rtName,
            RenderTexture manualRT,            // The manual RT (priority)
            UnityEngine.UI.RawImage targetImg, // The UI image to update
            bool isLooping,
            CancellationToken ct,
            Action<RenderTexture> onRtAssigned = null // Callback to update controller field
        )
        {
            // 1. Load (Async)
            bool ok = await LoadVideoAsync(fullPath, hp, ct, () => 
            {
                // Pre-detach callback
                if (targetImg) targetImg.texture = null;
            });

            if (!ok) return false;

            // 2. Configure (Main Thread)
            await AsyncExtensions.RunOnMainThread(() =>
            {
                hp.loop = isLooping; 
                hp.speed = 1f; 
                hp.time = 0f;
                
                // Use Manual RT if available (The Fix)
                RenderTexture rt = manualRT;
                if (rt == null)
                {
                    rt = GetOrCreateRenderTexture(w, h, rtName);
                }
                
                hp.targetTexture = rt;
                if (targetImg) targetImg.texture = rt;

                // Notify caller to update their state (e.g. _ctrl.idleRT_A = rt)
                onRtAssigned?.Invoke(rt);

            }, ct);

            return true;
        }

        #region Logging

        private void LogInfo(string message)
        {
            if (enableDebugLogs)
                ControllerMain.LogInfo($"[AsyncAssetManager] {message}");
        }

        private void LogWarning(string message)
        {
            ControllerMain.LogWarn($"[AsyncAssetManager] {message}");
        }

        private void LogError(string message)
        {
            ControllerMain.LogError($"[AsyncAssetManager] {message}");
        }

        #endregion
    }
}
