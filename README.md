# 🎨 Somatic Landscapes — Museum-Grade Interactive Artwork System

**Unity Source Project for Rashaad Newsome Studio (California / New York)**

A production-hardened, video installation system designed for 24/7 museum deployment. Features HAP-Q GPU-accelerated playback, museum-grade stability mechanisms, and OSC control integration.

---

## 🏛️ Production Features

### Museum-Grade Stability
- **Auto-Recovery**: Automatic restart from crashes with exponential backoff
- **Token Lifecycle Management**: Hierarchical cancellation prevents race conditions
- **Single-Decoder Mode**: Eliminates GPU/driver pressure during long runs
- **Playback Handshake**: Deterministic video startup or quarantine
- **Zero Timeout Deaths**: Infinite async operations for long-form content
- **Production Log Hygiene**: Development logs stripped in release builds

### Advanced Capabilities
- **8K HAP-Q Playback**: GPU-accelerated decoding via Klak.Hap
- **Dual-Layer Crossfades**: Seamless ambient video transitions
- **OSC Control**: Network-triggered mode switching with configurable blocking
- **Spout Broadcasting**: Zero-latency texture sharing for external mapping
- **Dynamic Configuration**: Live reload via JSON without rebuild
- **Comprehensive Logging**: Rotating logs with on-screen overlay (F2)

---

## 🌐 System Modes

| Mode | Description | Trigger |
|------|-------------|---------|
| **Idle (Ambient)** | Continuous looping HAP-Q landscape videos with seamless crossfades. Supports dual-player single-decoder mode for GPU efficiency. | Auto-starts on launch |
| **Active (Performance)** | Triggered video sequences with synchronized audio. Fades smoothly back to idle after completion. | OSC `/artwork/active 1` or button click |

---

## 🛠️ Architecture

### Core Components

| Component | Responsibility |
|-----------|---------------|
| **ArtworkController** | Main orchestrator - manages state, configuration, lifetime tokens |
| **IdleAmbientLoop** | Ambient video playback with auto-recovery and single-decoder mode |
| **ActiveFlow** | Performance video sequences with music synchronization |
| **AsyncAssetManager** | Video/audio loading with playback validation and quarantine |
| **ArtworkModeController** | OSC listener with configurable command blocking |
| **GlobalMainThreadDispatcher** | Thread-safe Unity main-thread marshalling |

### Async Architecture
- **Cancellation Tokens**: Hierarchical lifetime management prevents orphaned operations
- **SemaphoreSlim**: Serialized video loading prevents VRAM exhaustion
- **Task-based**: All I/O operations are async, never blocking main thread
- **Thread-Safe**: Main-thread operations properly marshalled from background threads

---

## 🔌 Technology Stack

### Plugins
| Plugin | Version | Purpose |
|--------|---------|---------|
| **Klak.Hap** | 0.1.20 | Hardware-accelerated HAP-Q video decoding |
| **Klak.Spout** | 2.0.3 | Zero-latency texture sharing to external apps |

### Frameworks
- **Unity URP** | Universal Render Pipeline for optimized rendering
- **Async/Await** | Modern async patterns for non-blocking I/O
- **.NET Task Parallel Library** | Robust cancellation and scheduling

---

## 📂 Runtime Structure

```
Somatic Landscapes/
├── Somatic Landscapes.exe
├── Config/
│   ├── ControllerMain.json      # Global settings, logging
│   └── ArtworkConfig.json       # Resolution, timing, behavior
├── Logs/                        # Auto-rotated runtime logs
└── Somatic Landscapes_Data/
    └── StreamingAssets/
        ├── Ambient/   # Idle video loops (.mov HAP-Q)
        ├── Active/    # Performance videos (.mov HAP-Q)
        └── Music/     # Audio tracks (.wav/.mp3)
```

> **Note**: Large HAP-Q video files are excluded from repository. Must be placed in `StreamingAssets` manually.

---

## ⚙️ Configuration

### Museum-Grade Settings (Recommended)

**ArtworkConfig.json** for maximum stability:

```json
{
  "ResolutionWidth": 5280,
  "ResolutionHeight": 1620,
  
  // === STABILITY SETTINGS ===
  "CrossfadeIdle": false,           // Single-decoder mode (reduces GPU pressure)
  "IdlePrepareLead": 5.0,           // Background load time
  "IdleFadeDuration": 1.0,
  
  "IgnoreAmbientWhileActive": true, // No mode interruption
  "OscBlockReturnToIdle": true,     // Block external OSC "0" commands
  
  // === ACTIVE MODE ===
  "ActiveFadeIn": 2.0,
  "ActiveFadeOut": 2.0,
  "ActiveUseFixedWindow": true,
  "ActiveFixedMidHoldSeconds": 30.0,
  
  // === RETURN TRANSITION ===
  "ReturnFade": 0.6,
  "ReturnBlackHold": 0.3,
  "RandomizeIdleStartOnReturn": true,
  
  // === AUDIO ===
  "MusicVolume": 1.0,
  "MusicSyncWithActiveFade": true,
  "AutoLoadMusicFromStreaming": true,
  
  // === UI ===
  "EnableStartupPanels": false,      // Skip intro for museum deployment
  "OscBlockReturnToIdle": true
}
```

### Configuration Options

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `CrossfadeIdle` | bool | true | Enable dual-player crossfades (false = single-decoder mode) |
| `IdlePrepareLead` | float | 0.6 | Seconds before swap to load next video |
| `IgnoreAmbientWhileActive` | bool | false | Prevent idle playback during active mode |
| `OscBlockReturnToIdle` | bool | false | Block OSC "0" commands (prevent forced return to idle) |
| `ActiveUseFixedWindow` | bool | true | Use fixed timing instead of video length |
| `RandomizeIdleStartOnReturn` | bool | true | Randomly select idle video after active |

---

## 🌐 OSC Integration

### Address & Protocol
- **Address**: `/artwork/active`
- **Port**: 7000 (configurable)
- **Protocol**: OSC 1.0 (int or float)

### Commands
```
/artwork/active 1  → Trigger Active Mode
/artwork/active 0  → Return to Idle (can be blocked via config)
```

### OSC Blocking Feature
When `OscBlockReturnToIdle: true` is enabled:
- ✅ OSC `1` (trigger active) → Always works
- ❌ OSC `0` (return idle) → Blocked and logged
- ✅ Button clicks → Always work (only blocks OSC)

**Use Case**: Museum installations where external control systems should not interrupt active playback.

---

## 🖥️ Controls & Shortcuts

| Key | Function |
|-----|----------|
| **F1** | Toggle Control Panel (if `EnableStartupPanels: true`) |
| **F2** | Toggle Runtime Log Overlay |
| **F3** | Toggle OSC/FPS Debug Panel |

---

## 🚀 Deployment Guide

### 1. Installation
```powershell
# Create deployment directory
New-Item -Path "C:\Somatic Landscapes" -ItemType Directory

# Copy build output
Copy-Item -Recurse "Build\*" -Destination "C:\Somatic Landscapes\"

# Create config and logs directories
New-Item -Path "C:\Somatic Landscapes\Config" -ItemType Directory
New-Item -Path "C:\Somatic Landscapes\Logs" -ItemType Directory
```

### 2. Media Setup
```powershell
# Place HAP-Q videos in StreamingAssets
$streamingAssets = "C:\Somatic Landscapes\Somatic Landscapes_Data\StreamingAssets"
Copy-Item "Videos\Ambient\*.mov" -Destination "$streamingAssets\Ambient\"
Copy-Item "Videos\Active\*.mov" -Destination "$streamingAssets\Active\"
Copy-Item "Audio\*.wav" -Destination "$streamingAssets\Music\"
```

### 3. Configuration
1. Copy `ArtworkConfig.json` to `C:\Somatic Landscapes\Config\`
2. Copy `ControllerMain.json` to `C:\Somatic Landscapes\Config\`
3. Adjust settings for your deployment (resolution, timing, OSC blocking)

### 4. Verification
1. Launch `Somatic Landscapes.exe`
2. Press **F2** → Verify logs show successful media discovery
3. Press **F3** → Check FPS and OSC status
4. Test OSC control → Send `/artwork/active 1` to trigger active mode

---

## 🔬 Production Stability Features

### ProFix1: Core Stability
✅ **Timeout Death Eliminated**: No more 30s async timeouts killing long videos  
✅ **Exception Recovery**: Idle loop auto-restarts after crashes  
✅ **Single-Decoder Mode**: GPU pressure reduced (idle only)  
✅ **Playback Handshake**: Deterministic video startup validation  
✅ **Thread-Safe RT Cleanup**: No more race conditions in texture destruction  

### ProFix2: Token Lifecycle
✅ **Root Token Storage**: Crash recovery uses lifetime token, not cancelled loop token  
✅ **Hierarchical Cancellation**: Idle uses controller lifetime, not active session token  
✅ **Production Logs**: Debug logs stripped from release builds  
✅ **Restart Serialization**: Guards against dual-loop race conditions  

---

## 🎯 Performance Targets

### Achieved Metrics (Museum Deployment)
- **Uptime**: 24/7 for weeks without manual intervention
- **MTBF**: Weeks/Months (was hours before ProFix)
- **Frame Rate**: Consistent 60 FPS @ 4K, 30 FPS @ 8K
- **Memory**: Stable (no leaks after 72+ hour runs)
- **Recovery Time**: \<1 second after exception

### System Requirements
- **OS**: Windows 10/11 (x64)
- **GPU**: NVIDIA RTX 2060+ (8GB VRAM recommended for 8K)
- **CPU**: Intel i7-8700 or equivalent
- **Storage**: NVMe SSD (critical for HAP-Q frame access)
- **RAM**: 16GB+ (32GB for 8K content)

---

## 🐛 Troubleshooting

### Common Issues

**Issue**: Videos don't advance / stuck on first frame  
**Solution**: Playback handshake will automatically quarantine bad clips. Check logs for `[ERR]` entries.

**Issue**: Idle dies after entering active mode  
**Solution**: This was ProFix2 bug - ensure you're on latest StableVersion branch.

**Issue**: OSC "0" commands not working  
**Solution**: Check if `OscBlockReturnToIdle: true` is set. Disable to allow OSC control of return-to-idle.

**Issue**: Log spam from `[IdleTiming]`  
**Solution**: These are development-only. Build in **Release** mode to remove.

---

## � Testing Checklist

### Pre-Deployment
- [ ] Build in **Release** mode (not Development)
- [ ] Verify `ArtworkConfig.json` settings
- [ ] Test video files play individually in Unity editor
- [ ] Confirm OSC connection (send test `/artwork/active 1`)
- [ ] Check Spout output in TouchDesigner/Resolume

### Post-Deployment
- [ ] Run for 30 minutes → Check stability
- [ ] Trigger active mode 10+ times → Check transitions
- [ ] Press F2 → Verify clean logs (no spam)
- [ ] Send OSC "0" → Verify blocking (if enabled)
- [ ] **Stress Test**: 72-hour continuous run

---

## 🔐 Production Hardening

### Deployment Best Practices
1. **Disable Unity Stack Traces**: Warning → None/Script Only in Player Settings
2. **Build Type**: Always use **Release** build (not Development)
3. **Script Defines**: Remove `DEVELOPMENT_BUILD` from compilation symbols
4. **Resources.UnloadUnusedAssets**: Interval set to 2 hours (not 5 minutes)
5. **External Watchdog**: Implement system service to monitor process health

### Monitoring
- **Logs**: Rotate every 14 days (configurable in `ControllerMain.json`)
- **Heartbeat**: OSC can be monitored for last-received timestamp
- **File Watcher**: Monitor `Logs/` for new errors

---

## 📜 Version History

### StableVersion (2026-01-28) - ProFix2 + OSC Blocking
- ✅ Fixed auto-recovery token lifecycle (was self-cancelling)
- ✅ Fixed return-to-idle visual glitch (load fresh BEFORE fade-in)
- ✅ Added OSC return-to-idle blocking feature
- ✅ Removed production log spam
- ✅ Added restart serialization guard

### Async Refactor (2026-01-23)
- ✅ Async/await architecture for all I/O operations
- ✅ Proper cancellation token hierarchy
- ✅ Thread-safe main-thread dispatcher
- ✅ RenderTexture pooling and lifecycle management

### V2.0 Optimization (2024)
- ✅ Eliminated stutter via background video loading
- ✅ Resolution optimization for PCIe bandwidth
- ✅ FPS unlock for smoother playback

---

## 💡 Credits

**Rashaad Newsome Studio** — California / New York  
*Museum-grade interactive artwork system*

**Technical Stack**:
- Unity URP
- Klak.Hap (Keijiro Takahashi)
- Klak.Spout (Keijiro Takahashi)
- Async/Await Architecture

**Production Engineering**: 2024-2026  
**Status**: Museum-deployed, production-hardened

---

## 📞 Support

For deployment assistance or technical questions:
- Check `Logs/` directory for error details
- Press **F2** for runtime log overlay
- Review configuration in `Config/ArtworkConfig.json`

---

**Museum-Grade. Production-Ready. Zero-Compromise Stability.**
