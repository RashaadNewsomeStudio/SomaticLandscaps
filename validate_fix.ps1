# Somatic Landscapes - Stability Fix Validation Script
# Run this after building to verify the RenderTexture depth fix worked

Write-Host "`n=== SOMATIC LANDSCAPES FIX VALIDATION ===" -ForegroundColor Cyan
Write-Host "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n" -ForegroundColor Gray

$logPath = "C:\Somatic Landscapes\Logs\runtime.log"

if (!(Test-Path $logPath)) {
    Write-Host "❌ Log file not found: $logPath" -ForegroundColor Red
    Write-Host "   Please run the game first to generate logs.`n" -ForegroundColor Yellow
    exit
}

Write-Host "📋 Analyzing log file: $logPath`n" -ForegroundColor White

# Check 1: Depth buffer warnings
Write-Host "CHECK 1: RenderTexture Depth Buffer Warnings" -ForegroundColor Cyan
$depthWarnings = Get-Content $logPath | Select-String "depth buffer"
$depthCount = $depthWarnings.Count

if ($depthCount -eq 0) {
    Write-Host "  ✅ PASS: Zero depth buffer warnings found" -ForegroundColor Green
    Write-Host "     The fix is working correctly!`n" -ForegroundColor Green
}
else {
    Write-Host "  ❌ FAIL: Found $depthCount depth buffer warnings" -ForegroundColor Red
    Write-Host "     The fix may not be applied or build is outdated.`n" -ForegroundColor Yellow
}

# Check 2: Crash markers
Write-Host "CHECK 2: Crash Markers" -ForegroundColor Cyan
$crashes = Get-Content $logPath | Select-String "CRASH-MARKER"
$crashCount = $crashes.Count

if ($crashCount -eq 0) {
    Write-Host "  ✅ PASS: No crashes detected`n" -ForegroundColor Green
}
else {
    Write-Host "  ⚠️  WARNING: Found $crashCount crash marker(s)" -ForegroundColor Yellow
    Write-Host "     Review logs for details.`n" -ForegroundColor Yellow
}

# Check 3: Session uptime
Write-Host "CHECK 3: Session Uptime" -ForegroundColor Cyan
$logStart = Get-Content $logPath | Select-String "LOG START" | Select-Object -Last 1
if ($logStart) {
    $startLine = $logStart.Line
    if ($startLine -match '\[(\d{2}:\d{2}:\d{2})\]') {
        $startTime = $matches[1]
        Write-Host "  📊 Session started at: $startTime" -ForegroundColor White
        
        $lastLine = Get-Content $logPath | Select-Object -Last 1
        if ($lastLine -match '\[(\d{2}:\d{2}:\d{2})\]') {
            $endTime = $matches[1]
            Write-Host "  📊 Latest log entry: $endTime`n" -ForegroundColor White
        }
    }
}

# Check 4: Video loading success
Write-Host "CHECK 4: Video Loading" -ForegroundColor Cyan
$videoLoads = Get-Content $logPath | Select-String "SUCCESS" | Select-String "mov"
$loadCount = $videoLoads.Count

if ($loadCount -gt 0) {
    Write-Host "  ✅ Successfully loaded $loadCount video(s)" -ForegroundColor Green
    Write-Host "     HAP decoder is working correctly.`n" -ForegroundColor Green
}
else {
    Write-Host "  ⚠️  No successful video loads detected" -ForegroundColor Yellow
    Write-Host "     Check if videos are present in StreamingAssets.`n" -ForegroundColor Yellow
}

# Summary
Write-Host "`n=== SUMMARY ===" -ForegroundColor Cyan

$allPassed = ($depthCount -eq 0) -and ($crashCount -eq 0) -and ($loadCount -gt 0)

if ($allPassed) {
    Write-Host "✅ ALL CHECKS PASSED - Build is stable!" -ForegroundColor Green
    Write-Host "`nRecommendation: Proceed with 4-hour stress test" -ForegroundColor White
}
elseif ($depthCount -eq 0) {
    Write-Host "✅ PRIMARY FIX VERIFIED (depth buffer fixed)" -ForegroundColor Green
    Write-Host "⚠️  Minor issues detected - review logs" -ForegroundColor Yellow
}
else {
    Write-Host "❌ PRIMARY FIX NOT WORKING" -ForegroundColor Red
    Write-Host "`nPossible causes:" -ForegroundColor Yellow
    Write-Host "  1. Build is outdated (rebuild required)" -ForegroundColor Yellow
    Write-Host "  2. Code changes not saved before build" -ForegroundColor Yellow
    Write-Host "  3. Unity cache needs clearing" -ForegroundColor Yellow
}

Write-Host "`nFor detailed analysis, review:" -ForegroundColor Gray
Write-Host "  - unity_crash_analysis.md" -ForegroundColor Gray
Write-Host "  - deployment_checklist.md`n" -ForegroundColor Gray
