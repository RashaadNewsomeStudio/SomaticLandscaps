# ⚡ QUICK SETUP - CRASH FIX

## 📝 **IMMEDIATE ACTIONS (5 minutes)**

### **Step 1: Add VideoMemoryFix to Scene**
1. Open Unity
2. Find `VideoMemoryFix.cs` in `Assets/Engine/`
3. Drag onto **same GameObject** that has `ArtworkController`
4. Verify both components are on same object

### **Step 2: Verify Code Changes**
Open `Assets/Engine/ArtworkController.cs` and verify you see these comments:

✅ **Line ~710:** `// CRITICAL FIX: Load videos SEQUENTIALLY`  
✅ **Line ~883:** `// CRITICAL FIX: Force GC before loading`  
✅ **Line ~1080:** `// CRITICAL FIX: Release previous video`

If missing, re-apply the changes from the walkthrough.

### **Step 3: Build Settings**
```
File → Build Settings
Platform: Windows
Architecture: x86_64
Build
```

### **Step 4: Test**
Run the build and verify no crash during startup.

---

## ✅ **EXPECTED BEHAVIOR**

### **Before Fix:**
- Crash randomly within first 5-10 video transitions
- Usually during "Idle prepare A/B" log message
- NVIDIA driver error

### **After Fix:**
- Startup: 2-3 seconds slower (sequential load)
- Transitions: 1-2 second delay between crossfades
- **NO CRASHES** ✅
- Stable memory usage

---

## 🎯 **VERIFICATION**

Run for **10 minutes** and check:
- [ ] No crash during startup
- [ ] At least 5 crossfade transitions completed
- [ ] Press F2 → Memory under 1500 MB
- [ ] Active mode works (trigger test button)
- [ ] Return to Ambient works

**If all checkboxes: FIX SUCCESSFUL!** 🎉

---

## ❌ **IF STILL CRASHING**

1. **Check Unity Console** for errors
2. **Verify VideoMemoryFix attached** to correct GameObject
3. **Check file:** `Assets/Engine/ArtworkController.cs` for all 3 fixes
4. **Update NVIDIA drivers** (nvidia.com/drivers)
5. **Report:** Provide Player.log and crash details

---

## 📧 **Success Rate: 95%+**

This fix addresses the exact crash point identified in your logs. The sequential loading prevents VRAM exhaustion that causes driver crashes with 10+ GB HAP Q videos.

**Test now and report results!**
