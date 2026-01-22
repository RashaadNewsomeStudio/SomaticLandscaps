using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// CRITICAL FIX: Prevents simultaneous video loading that causes VRAM exhaustion crashes.
/// FIX 3: Uses ticket-based queue semaphore (not boolean timeout) for strict mutual exclusion.
/// Place this on the same GameObject as ArtworkController.
/// </summary>
public class VideoMemoryFix : MonoBehaviour
{
    public static VideoMemoryFix Instance { get; private set; }
    
    // Flag for other controllers to check
    public bool IsHighPerformance { get; private set; } = false;

    // FIX 3: Ticket-based queue system (replaces boolean + timeout)
    private int _nextTicket = 0;
    private int _currentTicket = 0;
    private Queue<int> _waitingTickets = new Queue<int>();

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // Detect High-End HW (Thresholds lowered to safely include 16GB RAM / 6GB VRAM systems)
        long sysRam = SystemInfo.systemMemorySize;
        long vRam   = SystemInfo.graphicsMemorySize;
        
        // Revised: > 12GB RAM and > 4GB VRAM
        if (sysRam > 12000 && vRam > 4000)
        {
            IsHighPerformance = true;
            Debug.Log($"[VideoMemoryFix] High-End System Detected (RAM:{sysRam}MB, VRAM:{vRam}MB). Unlocking performance.");
        }
        else
        {
            IsHighPerformance = false;
            Debug.Log($"[VideoMemoryFix] Standard System Detected (RAM:{sysRam}MB, VRAM:{vRam}MB). Using safe limits.");
        }

        if (IsHighPerformance)
        {
            // --- HIGH PERFORMANCE MODE ---
            // Dynamic VRAM allocation: Use ~30% of available VRAM for texture cache
            int dynamicBudget = Mathf.Clamp((int)(vRam * 0.30f), 2048, 8192); // Min 2GB, Max 8GB
            
            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsMemoryBudget = dynamicBudget;
            QualitySettings.streamingMipmapsAddAllCameras = true; 
            QualitySettings.globalTextureMipmapLimit = 0; // Full Res
            
            Debug.Log($"[VideoMemoryFix] High-Perf Mode: Texture cache set to {dynamicBudget}MB (~30% of {vRam}MB VRAM).");
        }
        else
        {
            // --- STANDARD/LOW MEMORY MODE ---
            Resources.UnloadUnusedAssets();
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();

            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsMemoryBudget = 512; // MB
            QualitySettings.streamingMipmapsAddAllCameras = true;
            QualitySettings.globalTextureMipmapLimit = 0; 
            
            Debug.Log("[VideoMemoryFix] Applied conservative VRAM settings (512MB).");
        }
    }

    /// <summary>
    /// FIX 3: Ticket-based queue semaphore. Guarantees only ONE video open at a time.
    /// No timeout bypass - caller must wait for their ticket or fail gracefully.
    /// </summary>
    public IEnumerator WaitForLoadSlot()
    {
        // Acquire ticket
        int myTicket = _nextTicket++;
        _waitingTickets.Enqueue(myTicket);

        // Wait until it's my turn (strict FIFO queue)
        int maxWait = 0;
        while (_currentTicket != myTicket)
        {
            yield return null;
            maxWait++;
            
            // Safety: If waiting more than 10 seconds (600 frames @ 60fps), log warning but keep waiting
            if (maxWait > 600 && maxWait % 300 == 0)
            {
                Debug.LogWarning($"[VideoMemoryFix] Ticket {myTicket} waiting {maxWait} frames (current={_currentTicket}, queue={_waitingTickets.Count})");
            }
        }

        // My turn! Remove from queue (should be front)
        if (_waitingTickets.Count > 0 && _waitingTickets.Peek() == myTicket)
            _waitingTickets.Dequeue();
    }

    /// <summary>
    /// Release slot: Advance to next ticket. MUST be called in finally block.
    /// </summary>
    public void ReleaseLoadSlot()
    {
        _currentTicket++;
    }
    
    void LateUpdate()
    {
        // Monitor memory usage (F5) and queue status
        if (Input.GetKeyDown(KeyCode.F5))
        {
            long total = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576;
            long allocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576;
            long mono = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576;
            
            Debug.Log($"[MEMORY] HighPerf:{IsHighPerformance} | Reserved: {total} MB | Allocated: {allocated} MB | Mono: {mono} MB | LoadQueue: {_waitingTickets.Count} waiting, current ticket={_currentTicket}");
        }
    }
}
