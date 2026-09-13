<#
.SYNOPSIS
  Driver de la herramienta de interacción mano-objeto (Tools > Interaction Authoring) para Claude y scripts.

.DESCRIPTION
  Construye una petición JSON para BackroomsSurvival.EditorTools.HandInteraction.HandInteractionApi y la
  ejecuta por la vía que haya:
    - Editor de Unity ABIERTO sobre este proyecto (Temp/UnityLockfile bloqueado): puente de ficheros
      Logs/HandInteraction/inbox -> outbox. Hace falta que el editor esté compilado y sin Play.
    - Editor CERRADO: Unity -batchmode -executeMethod ...HandInteractionApi.RunCli (sin -nographics,
      para que las capturas no salgan negras). Arrancar Unity cuesta 1-3 min: usar -RequestFile con
      {"requests":[...]} para encadenar varios comandos en una sola arrancada.
  Imprime la respuesta JSON y sale con 0 si ok, 2 si la herramienta devolvió ok=false, 1 si no hubo respuesta.

.EXAMPLE
  .\tools\dev\HandInteraction.ps1 -Command find -Query linterna
  .\tools\dev\HandInteraction.ps1 -Command prepare -Prefab Assets/Prefabs/Wieldables/BR_Wieldable_CrankFlashlight.prefab -Kind TwoHand
  .\tools\dev\HandInteraction.ps1 -Command bake -Profile Assets/Data/HandInteraction/BR_Wieldable_CrankFlashlight_Hands.asset -Capture
  .\tools\dev\HandInteraction.ps1 -Command nudge -Profile <ruta> -Hand L -Direction forward -Millimeters 20 -Bake -Capture
  .\tools\dev\HandInteraction.ps1 -RequestFile Temp/HandInteraction/batch.json
#>
param(
    [string]$Command,
    [string]$Query,
    [string]$Prefab,
    [string]$Profile,
    [string]$Kind,
    [switch]$Overwrite,
    [string]$Patch,
    [string]$Hand,
    [string]$Direction,
    [double]$Millimeters = 0,
    [double]$Degrees = 0,
    [switch]$Capture,
    [switch]$Bake,
    [switch]$Force,
    [string]$RequestFile,
    [switch]$Headless,
    [int]$TimeoutSec = 900
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# Logs/ y no Temp/: Unity limpia Temp/ al cerrar y se llevaba peticiones y capturas (medido 2026-09-13).
$work = Join-Path $root 'Logs\HandInteraction'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($RequestFile) {
    $json = [IO.File]::ReadAllText((Resolve-Path $RequestFile).Path)
} else {
    if (-not $Command) { throw 'Falta -Command (find, list, inspect, prepare, get, set, nudge, preview, bake, validate, capture) o -RequestFile' }
    $req = [ordered]@{
        id = [guid]::NewGuid().ToString('N'); command = $Command; query = $Query; prefab = $Prefab; profile = $Profile;
        kind = $Kind; overwrite = [bool]$Overwrite; patch = $Patch; hand = $Hand; direction = $Direction;
        millimeters = $Millimeters; degrees = $Degrees; capture = [bool]$Capture; bake = [bool]$Bake; force = [bool]$Force
    }
    $json = $req | ConvertTo-Json -Compress
}

function Test-EditorOpen {
    $lock = Join-Path $root 'Temp\UnityLockfile'
    if (-not (Test-Path $lock)) { return $false }
    try { $fs = [IO.File]::Open($lock, 'Open', 'ReadWrite', 'None'); $fs.Close(); return $false } catch { return $true }
}

$name = 'req_' + [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff') + '.json'
if ($Headless -and (Test-EditorOpen)) {
    Write-Error 'Hay un editor de Unity abierto sobre este proyecto: -Headless no puede abrirlo otra vez (se quedaría bloqueado). Quitar -Headless para usar el puente.'
    exit 1
}
if (-not $Headless -and (Test-EditorOpen)) {
    $inbox = Join-Path $work 'inbox'; $outbox = Join-Path $work 'outbox'
    New-Item -ItemType Directory -Force -Path $inbox, $outbox | Out-Null
    $tmp = Join-Path $inbox ($name + '.part')
    [IO.File]::WriteAllText($tmp, $json, $utf8)
    Move-Item -Force $tmp (Join-Path $inbox $name)
    $out = Join-Path $outbox $name
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path $out)) {
        if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) {
            Write-Error "Sin respuesta del editor en $TimeoutSec s (¿compilando, en Play o sin foco?). La petición sigue en $inbox."
            exit 1
        }
        Start-Sleep -Milliseconds 500
    }
    $response = [IO.File]::ReadAllText($out)
    Remove-Item $out
} else {
    $unity = if ($env:UNITY_EXE) { $env:UNITY_EXE } else { 'C:\UnityInstall\6000.0.71f1\Editor\Unity.exe' }
    $reqPath = Join-Path $work $name
    $resPath = Join-Path $work ($name -replace '^req_', 'res_')
    $log = Join-Path $work ($name -replace '^req_', 'log_' -replace '\.json$', '.log')
    [IO.File]::WriteAllText($reqPath, $json, $utf8)
    $unityArgs = @('-batchmode', '-projectPath', "`"$root`"", '-executeMethod',
        'BackroomsSurvival.EditorTools.HandInteraction.HandInteractionApi.RunCli',
        '-hiRequest', "`"$reqPath`"", '-hiResponse', "`"$resPath`"", '-logFile', "`"$log`"")
    $p = Start-Process -FilePath $unity -ArgumentList $unityArgs -PassThru
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { Write-Error "Unity headless no terminó en $TimeoutSec s (log: $log)"; exit 1 }
    if (-not (Test-Path $resPath)) {
        Write-Error "Unity terminó sin respuesta (código $($p.ExitCode)). Errores de compilación: $log"
        Select-String -Path $log -Pattern 'error CS' | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }
        exit 1
    }
    $response = [IO.File]::ReadAllText($resPath)
}

$response
# El `ok` de NIVEL SUPERIOR de cada respuesta: JsonUtility escribe también bloques vacíos (p. ej. una
# `validation` sin usar con "ok":false), así que buscar el texto daría fallo en falso.
try {
    $parsed = $response | ConvertFrom-Json
    $all = if ($parsed.PSObject.Properties.Name -contains 'responses') { @($parsed.responses) } else { @($parsed) }
    if (@($all | Where-Object { -not $_.ok }).Count -gt 0) { exit 2 }
} catch { exit 2 }
exit 0
