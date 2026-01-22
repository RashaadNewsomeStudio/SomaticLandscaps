# GitHub Project Preparation Report (Updated)

This document outlines the final file structure for the **Somatic Landscapes** repository.

## ✅ Files to be PUSHED (Included)

### 📄 Documentation
- **`README.md`** (The ONLY documentation file included)

### 🧩 Unity Project Source
- **`Assets/`**
  - Scripts (`*.cs`)
  - Materials, Shaders, Prefabs
  - `Assets/Engine/`
- **`Packages/`** (Unity dependencies)
- **`ProjectSettings/`** (Input, Tags, Layers, Physics settings)

---

## ⛔ Files to be IGNORED (Excluded)

### � Excluded Documentation (Local/Dev Only)
- `SETUP_GUIDE.md`
- `Documentation.md`
- `PROJECT_OVERVIEW.md`
- `Somatic_Landscapes_QuickStart.md`
- `Fix1.md`, `Solution.md`, `Potential-Fixes.md`
- `Recomendation.md`

### 🚫 Helper Tools (Excluded)
- `WatchdogGUI/` (WPF Application)
- `SomaticStatusServer/` (Node.js Server)
- `Web-Crash-Viewer/`
- `switch_cuda.bat`
- `convert_icon.ps1`

### 🚫 Local Configuration
- `config1.json`
- `launcher_config.json`
- `Config/*.json` (JSON configs are ignored, safely keep template folder structure if empty)

### 📦 Large Media & Assets
- `Assets/StreamingAssets/Ambient/*.mov`
- `Assets/StreamingAssets/Active/*.mov`
- `Assets/Music/*.wav`

### 🏭 Build Artifacts & Logs
- `Library/`, `Temp/`, `Obj/`
- `Logs/`, `Logs2/`, `LogsII/`
- `*.exe`, `*_Data/`
- `WatchdogGUI.zip`
- `*.log`

### 💻 System & IDE
- `.vs/`, `.idea/`
- `*.sln`, `*.csproj`
