# pull_scan.ps1 — pulls the NEWEST room scan from the Quest and opens the photos.
# Run by double-clicking pull_scan_run.bat (which calls this), or:
#   powershell -ExecutionPolicy Bypass -File pull_scan.ps1

$adb    = "C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
$devDir = "/sdcard/Android/data/cz.fitvut.fat/files"
$outDir = Join-Path $PSScriptRoot "pulled_scans"

Write-Host ""
Write-Host "=== Pulling the newest room scan from the headset ===" -ForegroundColor Cyan
Write-Host ""

# Check headset connected
& $adb get-state 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: No headset detected. Connect the Quest via USB and allow USB debugging." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

# List dataset_* folders, pick the newest (names are sorted timestamps)
$folders = & $adb shell ls $devDir 2>$null |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -like "dataset_*" } |
    Sort-Object

if (-not $folders) {
    Write-Host "ERROR: No dataset_ folders found on the headset. Did you run a scan?" -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

$newest = $folders[-1]
Write-Host "Newest scan on headset: $newest"
Write-Host "Pulling to: $outDir\$newest"
Write-Host ""

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

& $adb pull "$devDir/$newest" $outDir
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "ERROR: Pull failed." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

$framesPath = Join-Path $outDir "$newest\frames"
Write-Host ""
Write-Host "=== Done. Opening the photos folder ===" -ForegroundColor Green
Start-Process explorer.exe $framesPath

Write-Host ""
Write-Host "Pulled to: $outDir\$newest"
Read-Host "Press Enter to close"
