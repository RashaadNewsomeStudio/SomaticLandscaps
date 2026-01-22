# 🌿 Somatic Landscapes — Interactive Artwork System 

## 🧭 Overview  

**Somatic Landscapes** is an immersive Unity-based video installation designed for panoramic projection environments (native resolution **5280 × 1620**).  
It transitions seamlessly between calm **ambient cinematic loops** and **triggered active performances** with synchronized audio.  
This system was developed for museum-scale use and operates autonomously once installed.

▶️ How the Application Runs (Operator View)

1_Launch

Double-click Somatic Landscapes.exe.

The Intro Panel fades in with text, then fades out automatically.

2_Standby / Screen Adjustment

A short Standby panel appears (“Standby…”), then fades away.

3_Main Menu

The Menu Panel appears with a PLAY button.

Click PLAY to begin the artwork.

4_Ambient Mode (Idle)

The system enters Ambient Mode, looping HAP-Q .mov landscape videos using two synchronized video players (A and B) with smooth crossfades.

This mode runs continuously and can transition into Active Mode at any time.

5_Trigger Active Mode

While Ambient Mode is running, click anywhere in the middle of the screen to trigger Active Mode — the central on-screen button covers that area.

Alternatively, send an OSC message (/artwork/triggerActive) from an external control app to start the Active sequence.

When triggered:

The ambient visuals fade out.

The Active video (fractal/performance) fades in.

The music starts and ramps up smoothly.

6_Active Playback

The Active clip plays fully, synchronized with its music track.

When finished, both fade out and the system automatically returns to Ambient Mode.

7_Automatic Return

After each Active sequence, the system fades back to the ambient landscapes and continues looping indefinitely.

8_Optional Controls

Press F1 → Show or hide the Settings Panel (used for fine-tuning fade times, music levels, etc.).

Press F2 → Show or hide the Runtime Log Overlay, which mirrors the Unity console for diagnostics.

---

## ⚙️ Core Modes  

| Mode | Description |
|------|--------------|
| **Ambient Mode** | Continuous looping playback of HAP-Q `.mov` landscape videos from the `Ambient/` folder, using dual-layer crossfades for seamless transitions. |
| **Active Mode**  | Triggered manually or via OSC. Plays a fractal or performance sequence with synchronized music, then fades smoothly back to Ambient Mode. |

---

## 🧩 System Components  

| Component | Purpose |
|------------|----------|
| **ArtworkController.cs** | Core video engine — handles Idle/Active crossfades, fade timing, and synchronized audio based on `ArtworkConfig.json`. |
| **ControllerMain.cs** | Global configuration, logging, and **on-screen runtime log overlay** (toggle F2). Manages folders, log rotation, and Unity console mirroring. |
| **ArtworkModeController.cs** | Listens for OSC or external triggers that start Active Mode. |
| **HapPlayer (Klak.Hap)** | GPU-accelerated HAP-Q video decoder ensuring smooth, frame-accurate playback. |
| **SpoutSender (Klak.Spout)** | Real-time GPU texture output — broadcasts Unity’s rendered image to external apps such as TouchDesigner or Resolume under the sender name **“Artwork-Spoot.”** |

---

## 🧠 Runtime Folder Architecture (Final .exe Build)

```
Somatic Landscapes/
├── Somatic Landscapes/
│   ├── Somatic Landscapes.exe
│   ├── Config/
│   │   ├── ControllerMain.json      ← Global paths, logging options, overlay keys  
│   │   └── ArtworkConfig.json       ← Video lists, fade durations, audio levels  
│   ├── Logs/
│   │   ├── runtime_YYYY-MM-DD_HH-mm-ss.log  
│   │   └── runtime.log  
│   └── Somatic Landscapes_Data/
│       └── StreamingAssets/
│           ├── Ambient/   ← looping landscape videos (.mov HAP-Q)  
│           ├── Active/    ← fractal / performance videos (.mov HAP-Q)  
│            
```


## 🔧 Configuration Files  

### 1️⃣ ControllerMain.json  
Defines global paths, logging, and UI overlay keys.  
Automatically created on first launch.  

**Location:** `Somatic Landscapes/Config/ControllerMain.json`

```json
{
  "EnableThis": true,
  "DebugMode": false,

  "AmbientStreamPath": "",
  "ActiveStreamPath": "",
  "AudioPath": "",

  "MaxLogDays": 14,
  "LogRootMode": "ExeDirectory",
  "LogFileName": "runtime.log",
  "LogRotateEachRun": true,
  "LogKeepLatestAlias": true,
  "LogAbsoluteDir": "",
  "LogAbsoluteFile": "",

  "UILogOverlayEnabled": true,
  "UILogMaxLines": 500,
  "UIOverlayMessage": "",
  "UIToggleKey": "F2"
}
```

### 🧱 About the Logging Setup

The system is configured so logs always appear **in the same directory where the game (.exe) runs**.  
This setup is completely portable — no matter which drive (C:, D:, E:, etc.) you launch from, logs are created beside the executable.

| Launch Path | Logs Will Be Created In |
|--------------|--------------------------|
| `C:\Somatic Landscapes\` | `C:\Somatic Landscapes\Logs\` |
| `D:\Installations\Somatic Landscapes\` | `D:\Installations\Somatic Landscapes\Logs\` |

**No code changes are required.**  
`ControllerMain.cs` already contains this logic — when `LogAbsoluteDir` and `LogAbsoluteFile` are empty, and `LogRootMode` is `"ExeDirectory"`, Unity automatically creates a `Logs` folder next to the executable.

### ✅ Recommended Portable Configuration

```json
"LogAbsoluteDir": "",
"LogAbsoluteFile": "",
"LogRootMode": "ExeDirectory"
```

This ensures the system dynamically logs beside the `.exe`, making it fully portable for installations and museum setups.

---

### 2️⃣ ArtworkConfig.json  

Specifies which media files are used and their playback behavior.  

**Location:** `C:/Somatic Landscapes/Config/ArtworkConfig.json`

```json
{
  "AmbientVideos": [
    "Somatic Landscapes_Data/StreamingAssets/Ambient/Ambiant mode 1 (Redwoods)2-008.mov",
    "Somatic Landscapes_Data/StreamingAssets/Ambient/Ambiant mode 2 (YosemiteNationalPark)-005.mov"
  ],
  "ActiveVideos": [
    "Somatic Landscapes_Data/StreamingAssets/Active/EC_031.mov",
    "Somatic Landscapes_Data/StreamingAssets/Active/EC_046.mov"
  ],
  "MusicTracks": [
    "Somatic Landscapes_Data/StreamingAssets/Music/1.1.wav",
    "Somatic Landscapes_Data/StreamingAssets/Music/2.1.wav"
  ],
  "MusicFadeInSeconds": 3.0,
  "MusicVolume": 0.85
}
```

📝 *Editable in any text editor — relaunch the application to apply changes.*

---

## 🎮 Controls Summary  

| Key | Action |
|-----|--------|
| **F1** | Show / Hide Settings Menu |
| **F2** | Show / Hide Log Overlay |

These on-screen overlays assist during setup, calibration, or debugging.  
*(You can replace the F1/F2 visual graphic here in-app.)*

---

## 🖥️ Runtime Log Overlay  

Built-in UI panel mirrors Unity Console output in real time.  

**Features:**  
- Live timestamps (`[HH:mm:ss]`).  
- Toggle with F2 or on-screen button.  
- Adjustable buffer (`UILineLimit`).  
- Auto-creates if no Canvas exists.  

---

## 🔗 OSC Integration  

`ArtworkModeController.cs` receives OSC messages from TouchDesigner / Max / sensor systems.  

| Address | Action |
|----------|---------|
| `/artwork/triggerActive` | Immediately starts Active Mode. |

---

## 🧩 Spout Integration (Live Video Output)  

**Spout** enables zero-latency GPU texture sharing with creative software.  

**Sender Name:** `Artwork-Spoot`

**Typical workflow**  
1. Launch *Somatic Landscapes.exe* — Spout broadcast starts.  
2. In *TouchDesigner*, add a *Spout In TOP* → select `Artwork-Spoot`.  
3. Video feed appears instantly for remapping or color grading.  

💡 Notes: Windows-only, DirectX 11+, NVIDIA/AMD GPU required.  

## 🎛 OSC Sender Software (External Control Application)

The project includes a small external tool named **OSC Sender + Mobile Bridge**, which allows manual triggering of Active or Ambient modes via OSC.

### 📂 Location
Find the executable on the Google Drive:  
```
Somatic Landscapes > OSC Sender > OscSenderGUI.exe
```

### ▶️ Usage
1. Open the folder and double-click `OscSenderGUI.exe` to launch it.  
2. The interface includes fields for IP, Port, and Address. Default settings:  
   - IP: 127.0.0.1  
   - Port: 7000  
   - Address: /artwork/active  
3. Click **Send 1 (Active)** to switch the Unity app to *Active Mode*.  
   This sends an OSC message `/artwork/active` to trigger the fractal sequence with music.  
4. Click **Send 0 (Ambient)** to return to *Ambient Mode*.  

Each action is logged in the sender’s console window, for example:  
```
Sent 1 → 127.0.0.1:7000 /artwork/active (= int32)
Sent 0 → 127.0.0.1:7000 /artwork/active (= int32)
```

The OSC Sender also runs a local HTTP bridge (`http://localhost:3001/`), so it can be accessed from another device on the same Wi‑Fi network (for example: `http://192.168.1.105:3001/`).

### 💡 Integration
Unity scripts **OSCDispatcher.cs** and **ArtworkModeController.cs** listen for these messages.  
When `/artwork/active` is received, the app transitions to *Active Mode*; when `/artwork/ambient` or `0` is received, it switches back to *Ambient Mode*.  

This bridge allows flexible triggering — via the desktop tool, sensors, or third-party OSC software such as TouchDesigner or Max/MSP.

---

## 💻 Hardware Requirements  

| Component | Recommended |
|------------|-------------|
| **OS** | Windows 10/11 (x64) |
| **GPU** | NVIDIA RTX 8 GB VRAM + |
| **CPU** | Intel i7 / Ryzen 7 + |
| **RAM** | 16 GB + |
| **Storage** | SSD (for HAP-Q files) |
| **Codec** | HAP-Q `.mov` |
| **Display** | 5280 × 1620 (3 × 1920 × 1080 projection or LED wall) |

---

## 🚀 Installation & Deployment  

1. **Create a base folder on your C: drive or D: drive**  
   Before installing, first create this directory:  
   ```
   C:\Somatic Landscapes\
   ```

2. Inside that directory, **place (drag and drop)** your complete *Somatic Landscapes* build folder — the one that contains the `.exe`.  
   The final structure should look like this:
   ```
   Somatic Landscapes/
│
├─ Somatic Landscapes.exe                ← main executable
├─ UnityCrashHandler64.exe               ← Unity crash handler
├─ UnityPlayer.dll                       ← Unity player runtime
│
├─ Somatic Landscapes_Data/              ← Unity data folder
│   └─ StreamingAssets/                  ← media content folder
│       ├─ Active/                       ← Active-mode (fractal) videos
│       └─ Ambient/                      ← Ambient-mode (landscape) videos
│
├─ Config/                               ← configuration folder
│   ├─ ControllerMain.json
│   └─ ArtworkConfig.json
│
├─ Logs/                                 ← runtime log files (auto-generated)
│
├─ D3D12/                                ← DirectX runtime dependencies
│
└─ MonoBleedingEdge/                     ← Unity .NET runtime dependencies

   ```

   ✅ **Where to Add or Replace Videos**  
   all playable media must be placed here:  
   ```
   C:\Somatic Landscapes\Somatic Landscapes\Somatic Landscapes_Data\StreamingAssets\
   ```
   Inside this folder you will find:  
   - `Ambient/` → for idle landscape loops  HapQ
   - `Active/` → for fractal / performance sequences  HapQ

   Unity loads directly from these folders at runtime. Adding or replacing files here will automatically update the available videos in-game.

3. Confirm these files exist:  
   - `Config/ControllerMain.json`  
   - `Config/ArtworkConfig.json`  

4. Run `Somatic Landscapes.exe`. Missing folders are created automatically.  

5. In *TouchDesigner* (or other Spout receiver), connect to the sender **Artwork-Spoot**.  

6. Use **F1** for the settings menu or **F2** for the log overlay.  

📝 To apply configuration changes, edit the JSON files and restart the application.  

---

## 🧰 Maintenance  

- Logs auto-clean after `MaxLogDays`.  
- Config files editable without rebuild.  
- Logs are created in the same directory as the `.exe`.  
- Media files must be placed under `Somatic Landscapes_Data/StreamingAssets/`.  
- Store all HAP-Q assets on SSD for smooth playback.  
- Safe to rename or relocate the main folder (logs follow the executable).  

---

## 🧾 Versioning Snapshot  

| File | Description |
|-------|--------------|
| `ControllerMain.cs` | System root – config, logging, UI overlay |
| `ArtworkController.cs` | Video engine and timing logic |
| `ArtworkModeController.cs` | OSC trigger handler |
| `SpoutSender.cs` | GPU output via Spout |
| `ArtworkConfig.json` | Artwork-level media settings |
| `runtime.log` | Live diagnostic output |

**Version:** 2025.10 — Production Build Documentation  

---

## 📧 Technical Contact  

**Somatic Landscapes — Interactive Development Team**  
📩 rashaadnewsomestudio@gmail.com   

© 2025 Rashaad Studio — All Rights Reserved
