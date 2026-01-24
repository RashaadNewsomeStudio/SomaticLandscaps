# GameWatchdog - Production Crash Recovery System

## 🎯 Purpose
Automatically restarts the Somatic Landscapes Unity game when it crashes, ensuring 24/7 uptime for LED installations.

## ✅ Features

### 1. **Automatic Restart**
- Monitors game process and restarts on crash
- Intelligent backoff: 2s → 4s → 8s → 16s → 30s
- Resets delay after 60s of stable runtime

### 2. **Crash Loop Prevention**
- Stops after 5 crashes within 120 seconds
- Prevents infinite restart loops
- Logs crash frequency and patterns

### 3. **Player.log Archiving** ⭐
- Archives Unity's `Player.log` after each crash
- Preserves crash forensics
- Keeps timestamped logs in `Logs/PlayerLogs/`
- Maintains `Player_LATEST.log` for quick access

### 4. **Remote Kill Switch** ⭐
- Create `STOP.txt` in watchdog directory to stop gracefully
- Checks before each restart
- Auto-deletes kill switch file on exit

### 5. **Exit Code Discrimination**
- Treats exit code 0 as clean shutdown
- Only non-zero exits count as crashes
- Still restarts even on clean exit (configurable)

## 📁 Files

```
GameWatchdog/
├── bin/Release/net9.0/
│   ├── GameWatchdog.exe          ← Run this
│   └── GameWatchdog.dll
├── Program.cs
└── GameWatchdog.csproj
```

## 🚀 Usage

### **Quick Start**

```powershell
cd "C:\Somatic Landscapes\GameWatchdog\bin\Release\net9.0"
.\GameWatchdog.exe "C:\Path\To\Somatic Landscapes.exe" -force-d3d11
```

### **Interactive Mode**

```powershell
.\GameWatchdog.exe
# Will prompt for game path
```

### **Configuration**

Edit `Program.cs` to change defaults:

```csharp
static string? gameExePath = @"C:\My\Game\Path.exe";  // Hardcode path
static string gameArgs = "-force-d3d11 -screen-fullscreen 1";
static bool autoRestart = true;

// Crash frequency settings
static int initialRestartDelaySeconds = 2;     // First restart delay
static int maxRestartDelaySeconds = 30;        // Maximum backoff
static int crashWindowSeconds = 120;           // Time window for crash counting
static int maxCrashesInWindow = 5;             // Max crashes before stop
```

## 📋 Windows Task Scheduler Setup (Auto-Start on Boot)

1. Open **Task Scheduler** (Win + R → `taskschd.msc`)
2. Click **Create Task** (not Basic Task)
3. **General Tab:**
   - Name: `Somatic Landscapes Watchdog`
   - ☑ Run whether user is logged on or not
   - ☑ Run with highest privileges
4. **Triggers Tab:**
   - New → Begin: **At startup**
   - Delay: 10 seconds (optional)
5. **Actions Tab:**
   - New → Start a program
   - Program: `C:\Somatic Landscapes\GameWatchdog\bin\Release\net9.0\GameWatchdog.exe`
   - Arguments: `"C:\Path\To\Somatic Landscapes.exe" -force-d3d11`
   - Start in: `C:\Somatic Landscapes\GameWatchdog\bin\Release\net9.0`
6. **Settings Tab:**
   - ☐ Stop task if it runs longer than... (uncheck)
   - ☑ If the task fails, restart every: **1 minute**

## 🛑 Stopping the Watchdog

### **Method 1: Kill Switch (Recommended)**
```powershell
# Create STOP.txt in the watchdog directory
New-Item "C:\Somatic Landscapes\GameWatchdog\bin\Release\net9.0\STOP.txt"
```
Watchdog will exit gracefully after the current game session ends.

### **Method 2: Task Manager**
1. Kill the **Somatic Landscapes.exe** process
2. Kill the **GameWatchdog.exe** process

## 📊 Monitoring

### **Watchdog Log**
```
C:\Somatic Landscapes\GameWatchdog\bin\Release\net9.0\watchdog.log
```

Example log:
```
2025-12-19 19:45:00 Watchdog started. Game: C:\...\Somatic Landscapes.exe Args: -force-d3d11
2025-12-19 19:45:02 Game started. PID=12345
2025-12-19 19:47:30 Game exited. PID=12345 ExitCode=-1073741819 Runtime=148.2s
2025-12-19 19:47:30 Archived Player.log → Player_2025-12-19_19-47-30.log
2025-12-19 19:47:30 Detected crash (non-zero exit code). Tracking crash frequency.
2025-12-19 19:47:30 Restarting in 2s... (Crashes in last 120s: 1/5)
```

### **Archived Player.log Files**
```
<Game Directory>/Logs/PlayerLogs/
├── Player_2025-12-19_19-47-30.log
├── Player_2025-12-19_20-15-42.log
├── Player_2025-12-19_21-03-18.log
└── Player_LATEST.log  ← Always the most recent
```

## 🔧 Troubleshooting

### **Watchdog won't start game**
- Check `watchdog.log` for errors
- Verify game path is correct
- Ensure .NET 9.0 runtime is installed

### **Game keeps crashing**
- Check archived `Player.log` files in `Logs/PlayerLogs/`
- Look for patterns (specific time, specific video, etc.)
- Review `ControllerMain` logs in game directory

### **Too many crashes, watchdog stopped**
- Fix root cause using archived Player.log files
- Increase `maxCrashesInWindow` if needed (not recommended)
- Check if `VideoMemoryFix` is properly configured

### **Can't stop watchdog**
- Create `STOP.txt` in watchdog directory
- Wait for current game session to end (or kill manually)
- If scheduled task keeps restarting, disable it in Task Scheduler

## 📦 Deployment Checklist

- [ ] Build watchdog: `dotnet build -c Release`
- [ ] Test manual run with game path
- [ ] Verify `watchdog.log` is created
- [ ] Kill game manually, verify restart
- [ ] Check Player.log archiving works
- [ ] Test kill switch (create STOP.txt)
- [ ] Configure Task Scheduler for auto-start
- [ ] Run 24-hour burn-in test
- [ ] Monitor crash frequency
- [ ] Review archived logs for patterns

## 🎯 Production Strategy

### **Layer 1: Prevention**
- `VideoMemoryFix.cs` in Unity game
- Sequential HAP video loading
- Smart VRAM management

### **Layer 2: Recovery** ← You are here
- GameWatchdog auto-restart
- Player.log archiving
- Crash frequency tracking

### **Layer 3: Monitoring**
- Watch `watchdog.log` for restart patterns
- Review archived `Player.log` files
- Remote access for kill switch

## 🔒 Security Notes

- Runs with system privileges (if scheduled task)
- No network communication
- Local file system only
- Logging may contain file paths

## 📄 License

Part of the Somatic Landscapes installation project.

---

**Version:** 2.0 (Enhanced)  
**Build Date:** 2025-12-19  
**Features:** Player.log archiving, Kill switch, Exit code discrimination
