# CheckRegressionChecklist.ps1
# Non-invasive regression checklist printer.
# Checks what it can automatically, prints manual steps for the rest.

param(
    [switch]$Verbose
)

$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition))

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  REGRESSION CHECKLIST" -ForegroundColor Cyan
Write-Host "  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

$passCount = 0
$failCount = 0
$manualCount = 0

function Check-Auto {
    param([string]$Label, [bool]$Pass, [string]$Detail)
    if ($Pass) {
        Write-Host "  [PASS] $Label" -ForegroundColor Green
        if ($Verbose -and $Detail) { Write-Host "         $Detail" -ForegroundColor DarkGray }
        $script:passCount++
    } else {
        Write-Host "  [FAIL] $Label" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor Yellow }
        $script:failCount++
    }
}

function Check-Manual {
    param([string]$Label, [string]$Instruction)
    Write-Host "  [    ] $Label" -ForegroundColor White
    if ($Instruction) { Write-Host "         -> $Instruction" -ForegroundColor DarkGray }
    $script:manualCount++
}

# ─── Section 1: Build Checks ───
Write-Host "--- Build & Process Checks ---" -ForegroundColor Yellow

# Backend exe exists
$backendExe = Join-Path $projectRoot "Builds\Backend\backrooms_server.exe"
$backendRelease = Join-Path $projectRoot "backend\target\release\backrooms_server.exe"
Check-Auto "Backend exe in Builds/Backend" `
    (Test-Path $backendExe) `
    $backendExe

Check-Auto "Backend release build exists" `
    (Test-Path $backendRelease) `
    $backendRelease

# Check if release is newer than Builds copy
if ((Test-Path $backendRelease) -and (Test-Path $backendExe)) {
    $relTime = (Get-Item $backendRelease).LastWriteTime
    $buildTime = (Get-Item $backendExe).LastWriteTime
    Check-Auto "Builds copy is up to date with release" `
        ($buildTime -ge $relTime) `
        "Release: $relTime | Builds: $buildTime"
}

# Check for stale processes
$serverProcs = Get-Process -Name "backrooms_server" -ErrorAction SilentlyContinue
$gameProcs = Get-Process -Name "BackroomsSurvivalMMO" -ErrorAction SilentlyContinue
Check-Auto "No stale backrooms_server processes" `
    ($null -eq $serverProcs -or $serverProcs.Count -eq 0) `
    $(if ($serverProcs) { "Found $($serverProcs.Count) running" } else { "Clean" })

Check-Auto "No stale BackroomsSurvivalMMO processes" `
    ($null -eq $gameProcs -or $gameProcs.Count -eq 0) `
    $(if ($gameProcs) { "Found $($gameProcs.Count) running" } else { "Clean" })

# Port availability
$port7777 = netstat -ano 2>$null | Select-String ":7777 " | Select-String "LISTEN"
$port7778 = netstat -ano 2>$null | Select-String ":7778 " | Select-String "LISTEN"
Check-Auto "IPC port 7777 available" `
    ($null -eq $port7777) `
    $(if ($port7777) { "Port 7777 in use" } else { "Available" })

Check-Auto "NET port 7778 available" `
    ($null -eq $port7778) `
    $(if ($port7778) { "Port 7778 in use" } else { "Available" })

Write-Host ""

# ─── Section 2: Manual — RemotePlayers ───
Write-Host "--- RemotePlayers = 1 ---" -ForegroundColor Yellow
Check-Manual "Host: WorldState remote_players=1" `
    "Check host backend logs for 'WorldState remote_players=1 ids=[...]'"
Check-Manual "Joiner: WorldState remote_players=1" `
    "Check joiner backend logs for 'WorldState remote_players=1 ids=[1]'"
Check-Manual "Unity Host: Parsed remote_players count=1" `
    "Check Unity console for '[IPCClient] Parsed remote_players count=1'"
Check-Manual "Unity Joiner: Parsed remote_players count=1" `
    "Check Unity console for '[IPCClient] Parsed remote_players count=1'"
Check-Manual "Host: RemotePlayerManager spawned joiner" `
    "Check for '[RemotePlayerManager] spawned id=<joiner_id>'"
Check-Manual "Joiner: RemotePlayerManager spawned host" `
    "Check for '[RemotePlayerManager] spawned id=1'"
Write-Host ""

# ─── Section 3: Manual — Transform Sync ───
Write-Host "--- Transform Sync V0 ---" -ForegroundColor Yellow
Check-Manual "Move host -> joiner's capsule moves" `
    "Walk around as host, verify joiner sees capsule moving"
Check-Manual "Move joiner -> host's capsule moves" `
    "Walk around as joiner, verify host sees capsule moving"
Check-Manual "No teleporting/snapping" `
    "Movement should be smooth interpolation, not jumps"
Check-Manual "MPTRACE step=R send_player_update present" `
    "Check backend logs for MPTRACE step=R"
Check-Manual "MPTRACE step=S receive_player_update present" `
    "Check backend logs for MPTRACE step=S"
Check-Manual "MPTRACE step=T worldstate_remote_transform present" `
    "Check backend logs for MPTRACE step=T"
Check-Manual "MPTRACE step=U remote_transform_apply present" `
    "Check Unity logs for MPTRACE step=U"
Write-Host ""

# ─── Section 4: Manual — Shared World Sync ───
Write-Host "--- Shared World Sync V0 ---" -ForegroundColor Yellow
Check-Manual "Joiner sees same chunks as host" `
    "Compare visible chunks in both clients"
Check-Manual "Joiner sees same entities as host" `
    "Entities should be at same positions in both clients"
Check-Manual "Joiner sees same items as host" `
    "Items should be at same positions in both clients"
Check-Manual "MPTRACE step=W host_world_snapshot_created" `
    "Host backend log"
Check-Manual "MPTRACE step=X send_world_snapshot" `
    "Host backend log"
Check-Manual "MPTRACE step=Y receive_world_snapshot" `
    "Joiner backend log"
Check-Manual "MPTRACE step=Z apply_world_snapshot" `
    "Joiner backend log"
Check-Manual "MPTRACE step=AA unity_parse_world_snapshot" `
    "Joiner Unity log"
Write-Host ""

# ─── Section 5: Manual — World Interaction ───
Write-Host "--- World Interaction Authority V0 ---" -ForegroundColor Yellow
Check-Manual "Host picks up item -> item disappears for both" `
    "Press E near item as host; verify removed in both clients"
Check-Manual "Joiner picks up item -> item disappears for both" `
    "Press E near item as joiner; verify host authority processes it"
Check-Manual "Double pickup attempt fails gracefully" `
    "Two players press E on same item simultaneously; only one succeeds"
Check-Manual "MPTRACE step=AI unity_interact_attempt" `
    "Unity log on E press"
Check-Manual "MPTRACE step=AJ unity_interact_sent" `
    "Unity log after IPC send"
Write-Host ""

# ─── Section 6: Manual — Disconnect ───
Write-Host "--- Disconnect / Cleanup ---" -ForegroundColor Yellow
Check-Manual "Close joiner -> host's remote capsule disappears" `
    "Should disappear within ~8s (5s timeout + 3s grace)"
Check-Manual "Close host -> joiner's remote capsule disappears" `
    "Should disappear within ~8s"
Check-Manual "No crashes or exceptions on disconnect" `
    "Check Unity console for red errors after disconnect"
Check-Manual "Reconnect after disconnect works" `
    "Close joiner, relaunch, join again; should work cleanly"
Write-Host ""

# ─── Section 7: Vendor patches ───
#
# Todo lo que este proyecto escribió DENTRO de Assets/PolymindGames/. Un reimport del
# .unitypackage del vendor lo borra en silencio y el síntoma aparece días después como una
# regresión sin causa. El inventario, con qué se pierde en cada caso, está en
# docs/systems/vendor-patches.md — si añades un parche allí, añade su comprobación aquí.
#
# Son comprobaciones de PRESENCIA de la marca, no de corrección del parche: lo que un reimport
# destruye es justo la presencia.
Write-Host "--- Parches en territorio vendor (los borra un reimport) ---" -ForegroundColor Yellow

$vendorRoot = Join-Path $projectRoot "Assets\PolymindGames"
if (-not (Test-Path $vendorRoot)) {
    Check-Auto "Arbol del vendor presente" $false $vendorRoot
} else {
    # Una sola pasada por el arbol: recorrerlo seis veces cuesta segundos por nada.
    $vendorCs = @(Get-ChildItem -Path $vendorRoot -Filter *.cs -Recurse -File -ErrorAction SilentlyContinue)

    function Get-MarkedFiles {
        param([string]$Pattern)
        return @($vendorCs | Select-String -Pattern $Pattern -SimpleMatch |
                 Select-Object -ExpandProperty Path -Unique)
    }

    # 1 — Hooks ADR-009: Rust es la unica fuente de verdad (guardado STP apagado,
    #     reconciliacion de pose, freno del drenaje de stamina). Baseline: 7 ficheros.
    $adr009 = Get-MarkedFiles "ADR-009"
    Check-Auto "Hooks ADR-009 en codigo de vendor" `
        ($adr009.Count -ge 7) `
        "$($adr009.Count) ficheros con la marca (baseline 7)"

    # 2 — Gate de arranque (enmienda ADR-025). DOS comprobaciones y no una: el parche de
    #     GameMode.cs no lleva marca propia, solo la llamada, asi que con mirar un lado una
    #     restauracion a medias pasaria por buena.
    $bootGate = Join-Path $vendorRoot "FPSCore\Code\Runtime\Core\GameBootGate.cs"
    $gameMode = Join-Path $vendorRoot "FPSCore\Code\Runtime\Core\GameMode.cs"
    $gameModeCalls = $false
    if (Test-Path $gameMode) {
        $gameModeCalls = @(Select-String -Path $gameMode -Pattern "GameBootGate" -SimpleMatch).Count -gt 0
    }
    Check-Auto "Gate de arranque sin timeout (ADR-025)" `
        ((Test-Path $bootGate) -and $gameModeCalls) `
        $(if (Test-Path $bootGate) { "GameBootGate.cs ok; GameMode lo llama: $gameModeCalls" } else { "falta GameBootGate.cs" })

    # 3 — DoF sin rama URP (ADR-065): sin el, el libro vuelve a salir borroso.
    $dof = Get-MarkedFiles "PARCHE LOCAL (ADR-065"
    Check-Auto "Parche DoF sin rama URP (ADR-065)" `
        ($dof.Count -ge 1) `
        "$($dof.Count) fichero(s)"

    # 4 — Traza MPTRACE de impactos, commit 458263a. Es TEMPORAL a proposito: cuando el
    #     diagnostico de atribucion PvP cierre, sale por decision. Mientras siga, un reimport
    #     no puede llevarsela sin que nadie se entere.
    $mptrace = Get-MarkedFiles "MPTRACE"
    Check-Auto "Traza MPTRACE de impactos (temporal)" `
        ($mptrace.Count -ge 4) `
        "$($mptrace.Count) ficheros (baseline 4)"

    # 5 — Alta del bote como wieldable en el prefab del jugador. Sin ella el bote se recoge,
    #     se ve en el inventario y NO se equipa, sin ningun error.
    #     Cura: menu Backrooms/Spray/Registrar bote en el jugador.
    $playerPrefab = Join-Path $vendorRoot "FPSCore\Prefabs\Core\FPS_Player.prefab"
    $sprayRegistered = $false
    if (Test-Path $playerPrefab) {
        $sprayRegistered = @(Select-String -Path $playerPrefab -Pattern "BR_Wieldable_SprayCan" -SimpleMatch).Count -gt 0
    }
    Check-Auto "Bote de spray dado de alta en FPS_Player" `
        $sprayRegistered `
        $(if ($sprayRegistered) { "BR_Wieldable_SprayCan presente" } else { "reejecuta: Backrooms/Spray/Registrar bote en el jugador" })

    # 6 — Reverb por zona: los 7 parametros expuestos del mixer. Si faltan, ReverbMixerDriver
    #     se declara mudo y el reverb se apaga en SILENCIO. Ojo: renombrarlos editando el YAML
    #     del .mixer NO llega al runtime, hay que hacerlo desde el editor.
    $mixer = Join-Path $vendorRoot "FPSCore\Data\Audio\FPS_AudioMixer.mixer"
    $rvb = 0
    if (Test-Path $mixer) {
        $rvb = @(Select-String -Path $mixer -Pattern "Rvb" -SimpleMatch).Count
    }
    Check-Auto "Reverb por zona expuesto en FPS_AudioMixer" `
        ($rvb -ge 7) `
        "$rvb parametros Rvb* (baseline 7)"
}

Write-Host ""

# ─── Summary ───
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  SUMMARY" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Auto checks:   $passCount passed, $failCount failed" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Green" })
Write-Host "  Manual checks: $manualCount items to verify" -ForegroundColor Yellow
Write-Host ""

if ($failCount -gt 0) {
    Write-Host "  Fix the FAIL items before testing." -ForegroundColor Red
    Write-Host "  Run: .\tools\dev\CopyReleaseBackendToBuild.ps1" -ForegroundColor Yellow
    Write-Host "  Run: .\tools\dev\CleanBackroomsProcesses.ps1" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  To run two-client test:" -ForegroundColor DarkGray
Write-Host "    .\tools\dev\RunTwoClientNetworkTest.ps1" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  To collect logs after test:" -ForegroundColor DarkGray
Write-Host "    .\tools\dev\CollectNetworkLogs.ps1" -ForegroundColor DarkGray
Write-Host ""
