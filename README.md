# � Somatic Landscapes — Interactive Artwork System

**Unity Source Project for Rashaad Newsome Studio (California / New York)**

---

## 🧭 Overview

**Somatic Landscapes** is an immersive Unity-based video installation designed for panoramic projection environments (native resolution **5280 × 1620**).
It transitions seamlessly between calm **ambient cinematic loops** and **triggered active performances** with synchronized audio.
This system was developed for museum-scale use and operates autonomously once installed.

### ⚙️ Core Modes

| Mode | Description |
|------|--------------|
| **Ambient Mode** | Continuous looping playback of HAP-Q `.mov` landscape videos from the `Ambient/` folder, using dual-layer crossfades for seamless transitions. |
| **Active Mode**  | Triggered manually or via OSC. Plays a fractal or performance sequence with synchronized music, then fades smoothly back to Ambient Mode. |

---

## 🧩 System Components

| Component | Purpose |
|------------|----------|
| **ArtworkController** | Core video engine — handles Idle/Active crossfades, fade timing, and synchronized audio based on `ArtworkConfig.json`. |
| **ControllerMain** | Global configuration, logging, and **Safe Startup** (deferred file I/O). Manages runtime logs with **on-screen overlay** (F2) and log rotation. |
| **ArtworkModeController** | Listens for OSC or external triggers that start Active Mode. |
| **HapPlayer (Klak.Hap)** | GPU-accelerated HAP-Q video decoder ensuring smooth, frame-accurate playback. |
| **VideoMemoryFix** | **CRITICAL**: Manages VRAM usage with a **ticket-based queue** to strictly serialize video loading. Prevents VRAM exhaustion and stutters on high-end systems (unlocks performance if RAM > 12GB & VRAM > 4GB). |

---

## 🔌 Plugins & Drivers

| Plugin | Version | Purpose |
|--------|---------|---------|
| **Klak.Hap** | 0.1.20 | Hardware-accelerated HAP-Q video decoding for smooth 5K playback. |
| **Klak.Spout** | 2.0.3 | Zero-latency texture sharing (Spout) to broadcast render output. |

---

## ⌨️ Controls & Shortcuts

| Key | Function |
|-----|----------|
| **F1** | Toggle Control Panel (if enabled in config) |
| **F2** | Toggle Runtime Log Overlay |
| **F3** | Toggle FPS / Debug Overlay |
| **F5** | Refresh FPS / Memory Stats |
| **H**  | Force High-Performance Mode (Debug) |
| **L**  | Force Low-Memory Mode (Debug) |

---

## 📂 Runtime Folder Architecture

The application expects the following structure alongside the executable:

```
Somatic Landscapes/
├── Somatic Landscapes.exe
├── Config/
│   ├── ControllerMain.json      ← Global paths, logging options
│   └── ArtworkConfig.json       ← Resolution, fade timings, audio settings
├── Logs/                        ← Auto-generated runtime logs
└── Somatic Landscapes_Data/
    └── StreamingAssets/
        ├── Ambient/   ← Place looping landscape videos here (.mov HAP-Q)
        ├── Active/    ← Place fractal/performance videos here (.mov HAP-Q)
        └── Music/     ← Audio tracks for Active mode (if enabled)
```

> **Note**: Large media files (HAP-Q videos) are excluded from the repository and must be placed in `StreamingAssets` manually.

---

## 🔧 Configuration Files

### 1️⃣ ArtworkConfig.json
**Location**: `Config/ArtworkConfig.json`

This file controls the core behavior of the installation. Note that video files are **automatically scanned** from the `Ambient` and `Active` folders.

```json
{
  "ResolutionWidth": 5280,
  "ResolutionHeight": 1620,

  "IdleFadeDuration": 1.5,      // Fade time between ambient loops
  "IdlePrepareLead": 0.6,
  "IdleMinFade": 0.2,
  "CrossfadeIdle": true,

  "ActiveFadeIn": 0.5,          // Fade in time for Active mode
  "ActiveFadeOut": 2.0,         // Fade out time returning to Ambient
  "ActiveUseFixedWindow": true,
  "ActiveFixedMidHoldSeconds": 22.0,
  "ActiveEndHoldSeconds": 0.15,

  "ReturnFade": 0.6,
  "ReturnBlackHold": 0.3,
  "RandomizeIdleStartOnReturn": true,

  "MusicVolume": 1.0,
  "MusicFadeIn": 0.5,
  "AutoLoadMusicFromStreaming": true,
  "MusicSubfolder": "Music",    // Subfolder name in StreamingAssets

  "PanelToggleKey": 282,        // F1 Key
  "EnableStartupPanels": false
}
```

### 2️⃣ ControllerMain.json
**Location**: `Config/ControllerMain.json`

Handles global system settings and logging.

```json
{
  "EnableThis": true,
  "LogRootMode": "External",    // Saves logs relative to the .exe
  "LogFileName": "runtime.log",
  "MaxLogDays": 14,
  "UILogOverlayEnabled": true,
  "UIToggleKey": "F2"           // Key to toggle on-screen log overlay
}
```

---

## 🔗 OSC Integration

**OSC Address**: `/artwork/active`
- **Send `1`**: Triggers **Active Mode**.
- **Send `0`**: Returns to **Ambient Mode**.

**OSC Sender Utility**:
A helper tool `OscSenderGUI.exe` (available separately) can be used to manually trigger modes via a simple GUI or web bridge.

---

## 🧩 Spout Integration

The application automatically broadcasts its render output via Spout.
- **Spout Name**: `Artwork-Spoot`
- **Usage**: Can be consumed by TouchDesigner, Resolume, or OBS on the same machine for mapping or recording.

---

## 🚀 V2.0 Optimization Update

**Resolved Stutter & Instability**:
- **Stutter Fix**: `IdlePrepareLead` increased to **2.0s**. Videos now load in the background, eliminating the transition hitch.
- **Resolution**: Configured to **3520 × 1080** (HD Vertical) to reduce PCIe bandwidth usage on high-end systems.
- **Spout**: Fully configurable via `ArtworkConfig.json`.
- **FPS**: Unlocked (running at native refresh rate).

### ⚠️ Operator Instructions (Critical)
1.  **Do NOT use Browser Spout**: Receiving Spout in a web browser will crash the application. Use TouchDesigner.
2.  **Do NOT use External FPS Monitors**: Overlays like "FPS Monitor" will degrade performance.
3.  **Check FPS**: Press **F3** (Refresh with **F5**).

---

## 🚀 Installation & Deployment

1. **Build Directory**: Create a folder `C:\Somatic Landscapes\`.
2. **Deploy**: Place the built application and `Config` folder inside.
3. **Media Setup**:
   - Ensure `Somatic Landscapes_Data/StreamingAssets/Ambient` contains your background loops.
   - Ensure `Somatic Landscapes_Data/StreamingAssets/Active` contains your performance videos.
4. **Run**: Launch `Somatic Landscapes.exe`.
5. **Verify**:
   - Press **F2** to see the log overlay.
   - Press **F3** to check FPS.

---

## � System Requirements

- **OS**: Windows 10/11 (x64)
- **GPU**: NVIDIA RTX series (8GB+ VRAM recommended for 5K texture handling)
- **Storage**: Fast SSD (critical for HAP-Q playback)
- **Display**: Supports custom resolutions (e.g., 5280 × 1620)

---

## � Credits

**Rashaad Newsome Studio**
*Built with Unity URP, Klak Hap, and Klak Spout.*
