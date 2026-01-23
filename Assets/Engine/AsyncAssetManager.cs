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
        [SerializeField] private int maxConcurrentVideoLoads = 2;
        [SerializeField] private float videoLoadTimeoutSeconds = 10f;
        [SerializeField] private int renderTexturePoolSize = 4;
        [SerializeField] private bool enableDebugLogs = true;

        // RenderTexture pool to prevent VRAM fragmentation
        private readonly Dictionary<string, Queue<RenderTexture>> _rtPool = new Dictionary<string, Queue<RenderTexture>>();
        private readonly HashSet<RenderTexture> _rtInUse = new HashSet<RenderTexture>();
        
        // Load queue management
        private SemaphoreSlim _videoLoadSemaphore = new SemaphoreSlim(2, 2);
        private readonly Dictionary<HapPlayer, CancellationTokenSource> _activeLoads = new Dictionary<HapPlayer, CancellationTokenSource>();
        
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
        /// </summary>
        public RenderTexture GetOrCreateRenderTexture(int width, int height, string name = "PooledRT")
        {
            string key = $"{width}x{height}";
            
            if (!_rtPool.ContainsKey(key))
            {
                _rtPool[key] = new Queue<RenderTexture>();
            }

            RenderTexture rt;
            if (_rtPool[key].Count > 0)
            {
                rt = _rtPool[key].Dequeue();
                LogInfo($"Reusing RenderTexture from pool: {key}");
            }
            else
            {
                rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = $"{name}_{key}",
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    antiAliasing = 1
                };
                rt.Create();
                LogInfo($"Created new RenderTexture: {key}");
            }

            _rtInUse.Add(rt);
            return rt;
        }

        /// <summary>
        /// Return a RenderTexture to the pool for reuse
        /// </summary>
        public void ReturnRenderTexture(RenderTexture rt)
        {
            if (rt == null) return;

            _rtInUse.Remove(rt);
            string key = $"{rt.width}x{rt.height}";

            if (!_rtPool.ContainsKey(key))
            {
                _rtPool[key] = new Queue<RenderTexture>();
            }

            // Limit pool size
            if (_rtPool[key].Count < renderTexturePoolSize)
            {
                _rtPool[key].Enqueue(rt);
                LogInfo($"Returned RenderTexture to pool: {key}");
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
            _rtToDestroy.Add(rt);
        }

        #endregion

        #region Video Loading

        /// <summary>
        /// Load video asynchronously with cancellation support
        /// </summary>
        public async Task<bool> LoadVideoAsync(string path, HapPlayer player, CancellationToken ct = default)
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

            // Cancel any existing load for this player
            if (_activeLoads.ContainsKey(player))
            {
                _activeLoads[player]?.Cancel();
                _activeLoads.Remove(player);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeLoads[player] = cts;

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

                    // Open video on main thread
                    bool opened = false;
                    await AsyncExtensions.RunOnMainThread(() =>
                    {
                        player.Open(path);
                    }, cts.Token);

                    // Wait for video to open with timeout
                    var timeout = TimeSpan.FromSeconds(videoLoadTimeoutSeconds);
                    var startTime = DateTime.UtcNow;

                    while (!opened && (DateTime.UtcNow - startTime) < timeout)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        
                        await Task.Yield();
                        
                        // Check if player is ready (on main thread)
                        opened = await AsyncExtensions.RunOnMainThread(() => 
                            player.streamDuration > 0.01f, cts.Token);
                    }

                    if (!opened)
                    {
                        LogWarning($"Video load timeout: {Path.GetFileName(path)}");
                        return false;
                    }

                    LogInfo($"Video loaded successfully: {Path.GetFileName(path)} ({player.streamDuration:F2}s)");
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
                return false;
            }
            catch (Exception ex)
            {
                LogWarning($"Video load error: {ex.Message}");
                return false;
            }
            finally
            {
                _activeLoads.Remove(player);
                cts?.Dispose();
            }
        }

        /// <summary>
        /// Release video and cleanup resources
        /// </summary>
        public void ReleaseVideo(HapPlayer player)
        {
            if (player == null) return;

            // Cancel any active load
            if (_activeLoads.ContainsKey(player))
            {
                _activeLoads[player]?.Cancel();
                _activeLoads.Remove(player);
            }

            // Close player
            try
            {
                player.Close();
            }
            catch (Exception ex)
            {
                LogWarning($"Error closing player: {ex.Message}");
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

        #endregion
    }
}
