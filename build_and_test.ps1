# Quick Build and Test Guide - Somatic Landscapes Stability Fix
# Run this from the project root to prepare for building

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  SOMATIC LANDSCAPES - BUILD and TEST PREPARATION" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Verify all changes were saved
Write-Host "STEP 1: Verifying Code Changes" -ForegroundColor Yellow
Write-Host "--------------------------------" -ForegroundColor Gray
Write-Host ""

$changes = @(
    @{File = "Assets\Engine\ArtworkController.cs"; Pattern = "new RenderTexture\(w, h, 24,"; Line = 418 },
    @{File = "Assets\Engine\ArtworkConfigService.cs"; Pattern = "new RenderTexture\(w, h, 24,"; Line = 174 },
    @{File = "Assets\Engine\ActiveFlow.cs"; Pattern = "Manual GC.Collect\(\) causes"; Line = 171 }
)

$allGood = $true
foreach ($change in $changes) {
    $path = Join-Path $PSScriptRoot $change.File
    if (Test-Path $path) {
        $content = Get-Content $path -Raw
        if ($content -match [regex]::Escape($change.Pattern)) {
            Write-Host "  [OK] $($change.File)" -ForegroundColor Green
        }
        else {
            Write-Host "  [FAIL] $($change.File) - Change not found!" -ForegroundColor Red
            $allGood = $false
        }
    }
    else {
        Write-Host "  [FAIL] $($change.File) - File not found!" -ForegroundColor Red
        $allGood = $false
    }
}

if (!$allGood) {
    Write-Host ""
    Write-Host "  Some changes are missing. Please verify files were saved." -ForegroundColor Red
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "[SUCCESS] All code changes verified!" -ForegroundColor Green
Write-Host ""

# Step 2: Check Unity project
Write-Host "STEP 2: Unity Project Check" -ForegroundColor Yellow
Write-Host "----------------------------" -ForegroundColor Gray
Write-Host ""

$projectSettings = "ProjectSettings\ProjectVersion.txt"
if (Test-Path $projectSettings) {
    $version = Get-Content $projectSettings | Select-String "m_EditorVersion:"
    if ($version) {
        Write-Host "  Unity Version: $($version.Line.Split(':')[1].Trim())" -ForegroundColor White
    }
}
else {
    Write-Host "  Could not find ProjectSettings" -ForegroundColor Yellow
}

# Step 3: Build instructions
Write-Host ""
Write-Host "STEP 3: Building the Project" -ForegroundColor Yellow
Write-Host "-----------------------------" -ForegroundColor Gray
Write-Host ""

Write-Host "  Build Instructions:" -ForegroundColor Cyan
Write-Host "     1. Open Unity Editor" -ForegroundColor White
Write-Host "     2. File -> Build Settings" -ForegroundColor White
Write-Host "     3. Select PC, Mac and Linux Standalone" -ForegroundColor White
Write-Host "     4. Target Platform: Windows x64" -ForegroundColor White
Write-Host "     5. Click Build" -ForegroundColor White
Write-Host "     6. Save to: Build\SomaticLandscapes.exe" -ForegroundColor White
Write-Host ""

Write-Host "  TIP: Use these build settings for best performance:" -ForegroundColor Gray
Write-Host "     - Configuration: Release" -ForegroundColor Gray
Write-Host "     - Compression Method: LZ4 (fastest)" -ForegroundColor Gray
Write-Host "     - Development Build: OFF (unless debugging)" -ForegroundColor Gray
Write-Host ""

# Step 4: Post-build testing
Write-Host "STEP 4: After Building - Run Tests" -ForegroundColor Yellow
Write-Host "------------------------------------" -ForegroundColor Gray
Write-Host ""

Write-Host "  Once build completes, run:" -ForegroundColor White
Write-Host "     .\validate_fix.ps1" -ForegroundColor Cyan
Write-Host ""

Write-Host "  Expected result:" -ForegroundColor White
Write-Host "     [OK] Zero depth buffer warnings" -ForegroundColor Green
Write-Host "     [OK] No crash markers" -ForegroundColor Green
Write-Host "     [OK] Successful video loads" -ForegroundColor Green
Write-Host ""

# Step 5: Stress test
Write-Host "STEP 5: Stress Testing (4 or more hours)" -ForegroundColor Yellow
Write-Host "-----------------------------------------" -ForegroundColor Gray
Write-Host ""

Write-Host "  Monitor these metrics:" -ForegroundColor White
Write-Host "     - VRAM usage (Task Manager -> Performance -> GPU)" -ForegroundColor Gray
Write-Host "       Target: under 600 MB (was 1.2 GB before)" -ForegroundColor Gray
Write-Host "     - Frame rate (should maintain 30 FPS or more)" -ForegroundColor Gray
Write-Host "     - No crashes for 4 hours or more" -ForegroundColor Gray
Write-Host "     - Smooth transitions (no stuttering)" -ForegroundColor Gray
Write-Host ""

# Summary
Write-Host ""
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  READY TO BUILD" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host ""

Write-Host "Quick Reference:" -ForegroundColor Cyan
Write-Host "   Documentation: unity_crash_analysis.md" -ForegroundColor White
Write-Host "   Deployment:    deployment_checklist.md" -ForegroundColor White
Write-Host "   Testing:       validate_fix.ps1" -ForegroundColor White
Write-Host ""

Write-Host "Press Enter to continue (will attempt to launch Unity Editor)..." -ForegroundColor Yellow
$null = Read-Host

# Try to launch Unity (if Unity Hub is installed)
$unityExe = "C:\Program Files\Unity\Hub\Editor\*\Editor\Unity.exe"
$unityPath = Get-Item $unityExe -ErrorAction SilentlyContinue | Select-Object -First 1

if ($unityPath) {
    Write-Host "Launching Unity Editor..." -ForegroundColor Green
    Write-Host ""
    Start-Process $unityPath.FullName -ArgumentList "-projectPath `"$PSScriptRoot`""
}
else {
    Write-Host "Could not auto-launch Unity. Please open manually." -ForegroundColor Yellow
    Write-Host ""
}
