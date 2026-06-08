# RunTwoClientNetworkTest.ps1
# Launches two game clients for testing the RemotePlayers=1 gate.
# Semi-assisted: opens the builds and prints instructions for Host/Join.

param(
    [string]$BuildExe = "",
    [int]$WaitSeconds = 4
)

$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition))

# --- Step 1: Kill previous processes ---
Write-Host "=== Step 1: Cleaning previous processes ===" -ForegroundColor Cyan
& (Join-Path $projectRoot "tools\dev\CleanBackroomsProcesses.ps1")
Start-Sleep -Seconds 1

# --- Step 2: Copy backend to Builds ---
Write-Host ""
Write-Host "=== Step 2: Copying backend to Builds ===" -ForegroundColor Cyan
& (Join-Path $projectRoot "tools\dev\CopyReleaseBackendToBuild.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "Backend copy failed. Aborting." -ForegroundColor Red
    exit 1
}

# --- Step 3: Locate game build ---
if ($BuildExe -eq "") {
    $candidates = @(
        (Join-Path $projectRoot "Builds\Windows\BackroomsSurvivalMMO.exe"),
        (Join-Path $projectRoot "Builds\BackroomsSurvivalMMO.exe"),
        (Join-Path $projectRoot "Build\BackroomsSurvivalMMO.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $BuildExe = $c
            break
        }
    }
}

if ($BuildExe -eq "" -or -not (Test-Path $BuildExe)) {
    Write-Host ""
    Write-Host "ERROR: Game build not found." -ForegroundColor Red
    Write-Host "Searched:" -ForegroundColor Yellow
    foreach ($c in $candidates) { Write-Host "  $c" }
    Write-Host ""
    Write-Host "Use -BuildExe to specify the path:" -ForegroundColor Yellow
    Write-Host '  .\RunTwoClientNetworkTest.ps1 -BuildExe "C:\path\to\BackroomsSurvivalMMO.exe"'
    exit 1
}

Write-Host ""
Write-Host "=== Step 3: Launching two clients ===" -ForegroundColor Cyan
Write-Host "Build: $BuildExe"

# --- Launch Window A (Host) ---
Write-Host ""
Write-Host "Launching Window A (Host)..." -ForegroundColor Green
Start-Process -FilePath $BuildExe
Write-Host "Waiting $WaitSeconds seconds before launching Window B..."
Start-Sleep -Seconds $WaitSeconds

# --- Launch Window B (Joiner) ---
Write-Host "Launching Window B (Joiner)..." -ForegroundColor Green
Start-Process -FilePath $BuildExe

# --- Step 4: Print manual instructions ---
Write-Host ""
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "  TWO CLIENTS LAUNCHED - MANUAL STEPS:" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Window A (Host):" -ForegroundColor Cyan
Write-Host "    1. Click [Host Session]"
Write-Host "    2. Wait for 'UDP bound on 0.0.0.0:7778' in backend logs"
Write-Host ""
Write-Host "  Window B (Joiner):" -ForegroundColor Cyan
Write-Host "    1. Set IP   = 127.0.0.1"
Write-Host "    2. Set Port = 7778"
Write-Host "    3. Click [Join Session]"
Write-Host ""
Write-Host "  Expected result:" -ForegroundColor Green
Write-Host "    Both clients show RemotePlayers = 1"
Write-Host ""
Write-Host "  Logs to watch (backend console):" -ForegroundColor Magenta
Write-Host "    - 'Sending handshake to ...'"
Write-Host "    - 'Received handshake from ...'"
Write-Host "    - 'Peer connected id=...'"
Write-Host "    - 'WorldState remote_players=1'"
Write-Host ""
Write-Host "  See docs/REMOTEPLAYERS_GATE.md for full diagnostic checklist."
Write-Host "============================================" -ForegroundColor Yellow
