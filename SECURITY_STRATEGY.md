# 🔒 Project Security & IP Protection Strategy

This document outlines how to safely share the **Somatic Landscapes** project with external developers while protecting your full workflow, high-res assets, and core proprietary logic.

## 1. The "Black Box" Strategy (Code Hardening)

You asked about **"encrypting lines"**. In software, the standard way to do this is **Compilation to DLL**.

### How it works:
Instead of giving the developer the raw C# files (`.cs`), you compile your core logic into a **Plugin** (`.dll` file).
1.  **Move Scripts**: Take sensitive scripts like `ControllerMain.cs`, `VideoMemoryFix.cs`, or `ArtworkController.cs` out of the project.
2.  **Compile**: Build them in a separate Visual Studio Class Library project.
3.  **Import Plugin**: valid `SomaticCore.dll` into `Assets/Plugins/` in the Unity project you share.

### The Result:
- **Read-Only**: The developer can *use* your scripts (drag them onto GameObjects), but they **cannot open/edit the code**.
- **No Logic Leaks**: They cannot see *how* you fixed the memory leak or how the watchdog talks to the server.
- **Obfuscation**: You can add an "Obfuscator" step (like *Dotfuscator* or *ConfuserEx*) which scrambles the internal names, making it nearly impossible to reverse-engineer.

> **Recommendation**: Do this for `ControllerMain.cs` and `VideoMemoryFix.cs`. Leave UI scripts open so they can work on the frontend.

---

## 2. Repository Isolation (Already Started)

Your current `.gitignore` is your first line of defense. By excluding entire tools, you prevent the developer from ever having the "Full System".

| Component | Status | Result for Developer |
|-----------|--------|----------------------|
| **Game Launcher** | ⛔ IGNORED | They cannot start the app automatically or see the crash-recovery logic. |
| **Status Server** | ⛔ IGNORED | They cannot see how the remote monitoring works. |
| **Configs** | ⛔ IGNORED | They never see your production IP addresses or museum settings. |

**Action**: Maintain the strict `.gitignore` we created.

---

## 3. Asset Substitution (The "Dummy" Method)

**Never** put your full resolution (5280x1620) Artworks in the shared Git repository.

### The Problem:
If you push the 5K HAP-Q files, the developer has your raw art.

### The Solution:
1.  Create **Low-Res Placeholders**:
    - Resolution: 640x360.
    - Content: A simple text video saying "PLACEHOLDER ART".
    - Filename: MUST match the real files (e.g., `EC_012.mov`).
2.  **Push Placeholders**: Commit *these* small files to Git.
3.  **Local Override**: On your machine, you keep the real 5K files. On their machine, they see the placeholders.

**Result**: They can code and test the *logic* of the video player, but they don't possess the *artwork*.

---

## Summary Checklist for Outsourcing

1.  [ ] **Verify .gitignore**: Ensure `WatchdogGUI` and `SomaticStatusServer` are ignored.
2.  [ ] **Create Placeholders**: Generate dummy low-res videos for the repository.
3.  [ ] **Compile Core**: Move `ControllerMain.cs` to a DLL (Advanced step).
4.  [ ] **Invite**: Give them access only to the "Lite" repository.
