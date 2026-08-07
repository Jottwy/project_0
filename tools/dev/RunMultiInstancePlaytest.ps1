# RunMultiInstancePlaytest.ps1
#
# Levanta N instancias locales del build standalone con identidad, puertos y log
# separados por proceso. Arranca sin clicks: SESSION_MODE/CONNECT_TO disparan
# host/join automaticamente (JoinSessionUI.cs:91-114), asi que no hace falta
# tocar la UI de ninguna ventana.
#
# Por que existe: PlayerPrefs en Windows vive en el registro por company+product,
# COMPARTIDO entre todas las instancias del mismo build en la misma maquina — sin
# PLAYER_IDENTITY_KEY distinta por instancia, las N ventanas resuelven la MISMA
# identity_key (ADR-045), solo una gana el lock exclusivo del fichero de jugador,
# y las demas degradan en silencio a "sin persistencia esta sesion". Este script
# fija esa env var por instancia ANTES de lanzar el proceso.
#
# Logs: separados via `-logFile` (flag nativo de Unity, aplica a builds standalone,
# no solo al editor). El backend Rust NO tiene fichero de log propio — escribe a
# stdout/stderr, que Unity captura y mete en SU log via Debug.Log($"[Backend] {line}")
# (NetworkInitializer.cs:616-630) — separar el log de Unity separa el del backend
# gratis, sin tocar el backend.
#
# LIMITACION CONOCIDA (no se puede arreglar sin tocar codigo de produccion): el seed
# del mundo NO se puede fijar via env para el camino de auto-host. JoinSessionUI.cs:103
# llama StartAsHost(playerName) sin seed, y NetworkInitializer solo lee el seed como
# parametro C#, nunca de una env var propia — asi que -WorldSeed aqui es puramente
# informativo (se imprime, se manda igualmente por si algun dia se lee, pero HOY no
# tiene efecto). El seed real efectivo siempre es 42 salvo que alguien lo cambie a
# mano en la UI.
#
# NUEVO 2026-08-07 — todo opt-in, el comportamiento por defecto (sin parametros) NO
# cambia un bit respecto a antes de estas tres piezas:
#
#   -BuildUnity   Build headless del Player ANTES de lanzar (Step 2b). Usa el flag
#                 nativo `-buildWindows64Player <ruta>` de Unity — probado a mano en
#                 esta misma rama, no hace falta ([Editor]/[MenuItem] ni un metodo
#                 estatico propio: ese flag ya construye sin script adicional). Exige
#                 el Editor CERRADO (se comprueba el lock real de Temp/UnityLockfile,
#                 no solo tasklist — un Editor recien cerrado puede tardar en soltarlo,
#                 y tasklist puede no mostrar el proceso aunque el lock siga vivo,
#                 lecciones de esta sesion). Si el build falla — exit code, ausencia de
#                 "Build Finished, Result: Success" en el log, o el .exe no aparece
#                 despues — ABORTA con el error visible. Nunca sigue con un exe viejo:
#                 exactamente el fallo que mas corridas de playtest ha costado.
#   -UnityExe     Override de la ruta a Unity.exe. Por defecto se deriva de
#                 ProjectSettings/ProjectVersion.txt + el patron de instalacion de esta
#                 maquina (C:\UnityInstall\<version>\Editor\Unity.exe).
#   -ResetSaves   ARCHIVA (nunca borra) la carpeta Saves de persistentDataPath a un
#                 directorio hermano Saves_backup_<timestamp>. Borrar destruye evidencia
#                 de bugs sin diagnosticar — ya paso una vez esta sesion.
#   -Force        Salta la confirmacion interactiva del aviso de exe obsoleto (mas
#                 abajo). Sin esto, un exe mas viejo que el ultimo commit sobre Assets/
#                 para y pregunta antes de lanzar nada.
#
# AVISO DE EXE OBSOLETO (siempre activo, sin parametro que lo desactive del todo):
# Step 3 compara la fecha del .exe de Unity contra la del ultimo commit que toca
# Assets/. Mas de una corrida de esta sesion se perdio midiendo contra un build de
# semanas antes sin que nadie mirara la fecha que el script ya imprimia.

param(
    [int]$InstanceCount = 4,
    [int]$HostIndex = 1,
    [int]$WorldSeed = 42,
    [string]$BuildExe = "",
    [string]$IdentityPrefix = "playtest",
    [int]$WaitSecondsAfterHost = 5,
    [int]$BaseIpcPort = 7777,
    [int]$BaseNetPort = 7778,
    [int]$PortStride = 10,
    [switch]$SkipClean,
    [switch]$SkipBackendCopy,
    [switch]$NoWindowedMode,
    [int]$ScreenWidth = 800,
    [int]$ScreenHeight = 600,
    [switch]$BuildUnity,
    [string]$UnityExe = "",
    [switch]$ResetSaves,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition))

if ($InstanceCount -lt 1) { throw "InstanceCount debe ser >= 1" }
if ($HostIndex -lt 1 -or $HostIndex -gt $InstanceCount) { throw "HostIndex debe estar entre 1 y $InstanceCount" }

# --- Step 1: limpiar procesos previos ---
if (-not $SkipClean) {
    Write-Host "=== Step 1: limpiando procesos previos ===" -ForegroundColor Cyan
    & (Join-Path $projectRoot "tools\dev\CleanBackroomsProcesses.ps1")
    Start-Sleep -Seconds 1
} else {
    Write-Host "=== Step 1: SkipClean, no se mata nada previo ===" -ForegroundColor Yellow
}

# --- Step 1b: archivar saves (opt-in, -ResetSaves) ---
if ($ResetSaves) {
    Write-Host ""
    Write-Host "=== Step 1b: archivando saves ===" -ForegroundColor Cyan
    # persistentDataPath real de este proyecto (company=JottwyGames, product=
    # BackroomsSurvivalMMO) — confirmado contra disco esta sesion, no del
    # ProjectSettings.asset a secas (ahi aparece con distinta capitalizacion;
    # Windows es case-insensitive para rutas asi que da igual, pero esta es la
    # que existe de verdad).
    $savesDir = Join-Path $env:USERPROFILE "AppData\LocalLow\JOTTWYGAMES\BackroomsSurvivalMMO\Saves"
    if (Test-Path $savesDir) {
        $saveStamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $backupDir = Join-Path (Split-Path -Parent $savesDir) "Saves_backup_$saveStamp"
        # Move-Item, NUNCA Remove-Item — un save que archivamos por error sigue siendo
        # evidencia de un bug sin diagnosticar. Borrar lo destruye sin remedio.
        Move-Item -Path $savesDir -Destination $backupDir -Force
        Write-Host "  Saves archivados en: $backupDir" -ForegroundColor Green
    } else {
        Write-Host "  No hay Saves que archivar en $savesDir (nada que hacer)." -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "=== Step 1b: -ResetSaves no activado, saves existentes se conservan ===" -ForegroundColor Yellow
}

# --- Step 2: backend fresco ---
if (-not $SkipBackendCopy) {
    Write-Host ""
    Write-Host "=== Step 2: copiando backend release a Builds/ ===" -ForegroundColor Cyan
    & (Join-Path $projectRoot "tools\dev\CopyReleaseBackendToBuild.ps1")

    # BUG encontrado 2026-08-07: comprobar $LASTEXITCODE aqui daba falso negativo SIEMPRE
    # (CopyReleaseBackendToBuild.ps1 no llamaba `exit 0` en su camino de exito -> el valor
    # stale de un .exe nativo anterior en la sesion colaba como "fallo"). Corregido en la
    # raiz (ese script ya llama exit 0), pero la verificacion de VERDAD que importa aqui es
    # el fichero en si, no un exit code de un proceso intermedio -- asi que se comprueba
    # directamente, con un par de reintentos cortos por si un AV retiene el .exe recien
    # escrito una fraccion de segundo (transitorio, no un fallo real).
    $copiedBackend = Join-Path $projectRoot "Builds\Backend\backrooms_server.exe"
    $backendReady = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if (Test-Path $copiedBackend) {
            $copiedSize = (Get-Item $copiedBackend).Length
            if ($copiedSize -gt 1MB) {
                $backendReady = $true
                break
            }
        }
        if ($attempt -lt 3) { Start-Sleep -Milliseconds 500 }
    }

    if (-not $backendReady) {
        if (-not (Test-Path $copiedBackend)) {
            Write-Host "Copia de backend fallo: $copiedBackend no existe tras la copia." -ForegroundColor Red
        } else {
            $badSize = (Get-Item $copiedBackend).Length
            Write-Host "Copia de backend fallo: $copiedBackend existe pero pesa solo $badSize bytes (se esperaba >1MB) - posible copia truncada o antivirus reteniendolo." -ForegroundColor Red
        }
        Write-Host "Abortando." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host ""
    Write-Host "=== Step 2: SkipBackendCopy, se usa el .exe ya desplegado ===" -ForegroundColor Yellow
    Write-Host "  OJO: si el backend desplegado es mas viejo que tus commits, esto invalida" -ForegroundColor Yellow
    Write-Host "  la medicion entera (fue exactamente lo primero que se descarto en el" -ForegroundColor Yellow
    Write-Host "  diagnostico anterior). Verifica con grep -aoc sobre el .exe si dudas." -ForegroundColor Yellow
}

# --- Step 2b: build de Unity (opt-in, -BuildUnity) ---
if ($BuildUnity) {
    Write-Host ""
    Write-Host "=== Step 2b: build headless de Unity ===" -ForegroundColor Cyan

    if ($UnityExe -eq "") {
        $versionFile = Join-Path $projectRoot "ProjectSettings\ProjectVersion.txt"
        if (-not (Test-Path $versionFile)) {
            Write-Host "ERROR: no se encontro $versionFile para derivar la version de Unity. Usa -UnityExe." -ForegroundColor Red
            exit 1
        }
        $versionLine = Get-Content $versionFile | Where-Object { $_ -match '^m_EditorVersion:\s*(\S+)' } | Select-Object -First 1
        if (-not $versionLine -or $versionLine -notmatch '^m_EditorVersion:\s*(\S+)') {
            Write-Host "ERROR: no se pudo leer m_EditorVersion de $versionFile. Usa -UnityExe." -ForegroundColor Red
            exit 1
        }
        $unityVersion = $Matches[1]
        $UnityExe = "C:\UnityInstall\$unityVersion\Editor\Unity.exe"
    }
    if (-not (Test-Path $UnityExe)) {
        Write-Host "ERROR: Unity.exe no encontrado en $UnityExe. Usa -UnityExe para indicar la ruta real." -ForegroundColor Red
        exit 1
    }

    # El Editor tiene que estar CERRADO — un build headless contra el mismo proyecto
    # con el Editor abierto choca. tasklist puede no mostrar Unity.exe aunque el lock
    # siga vivo (visto esta sesion); la prueba de verdad es intentar abrir el lockfile
    # en exclusiva.
    $lockPath = Join-Path $projectRoot "Temp\UnityLockfile"
    if (Test-Path $lockPath) {
        $editorOpen = $true
        try {
            $fs = [System.IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None')
            $fs.Close()
            $editorOpen = $false
        } catch {
            $editorOpen = $true
        }
        if ($editorOpen) {
            Write-Host "ERROR: el Editor de Unity esta abierto (Temp\UnityLockfile en uso). Cierralo antes de -BuildUnity - un build headless no puede correr contra el mismo proyecto a la vez." -ForegroundColor Red
            exit 1
        }
    }

    $buildTarget = if ($BuildExe -ne "") { $BuildExe } else { Join-Path $projectRoot "Builds\BackroomsSurvivalMMO.exe" }
    $unityBuildLogDir = Join-Path $projectRoot "Builds\PlaytestLogs"
    New-Item -ItemType Directory -Force -Path $unityBuildLogDir | Out-Null
    $unityBuildLog = Join-Path $unityBuildLogDir ("unity_build_{0}.log" -f (Get-Date -Format "yyyyMMdd_HHmmss"))

    Write-Host "  Unity.exe:    $UnityExe"
    Write-Host "  Build target: $buildTarget"
    Write-Host "  Log:          $unityBuildLog"
    Write-Host "  (puede tardar varios minutos)"

    # Capturado ANTES de invocar: es la referencia para probar que el .exe se
    # REGENERO de verdad (ver leccion de abajo), no que ya estaba ahi de una
    # corrida anterior.
    $buildStartTime = Get-Date

    & $UnityExe -batchmode -quit -nographics -projectPath $projectRoot -buildWindows64Player $buildTarget -logFile $unityBuildLog
    $unityExitCode = $LASTEXITCODE

    # BUG encontrado 2026-08-07: el criterio original exigia "Build Finished, Result:
    # Success" en el log -- en un build real y correcto (exit 0, .exe generado, tamano
    # normal) esa cadena NUNCA aparecio: el -logFile de Unity se corta a mitad de
    # "Unloading Unused Serialized files", sin llegar a flushear la linea de cierre.
    # Grepear una cadena que Unity no escribe de forma fiable con -batchmode -quit da
    # falso negativo SIEMPRE, exit code 0 o no. La prueba que realmente demuestra que
    # hubo un build (no solo que el proceso salio limpio) es que el .exe se REESCRIBIO:
    # exit code 0 + existe + tamano razonable + timestamp POSTERIOR al arranque del
    # build (un exe viejo que sobrevive intacto no pasaria este ultimo check).
    $exeReady = (Test-Path $buildTarget) -and ((Get-Item $buildTarget).Length -gt 100KB)
    $exeIsFresh = $exeReady -and ((Get-Item $buildTarget).LastWriteTime -gt $buildStartTime)

    if ($unityExitCode -ne 0 -or -not $exeReady -or -not $exeIsFresh) {
        Write-Host ""
        Write-Host "ERROR: build de Unity fallo. Abortando - nunca se lanza con un exe viejo." -ForegroundColor Red
        Write-Host "  Unity exit code: $unityExitCode"
        Write-Host "  .exe presente y con tamano razonable: $exeReady"
        Write-Host "  .exe con timestamp posterior al inicio del build ($buildStartTime): $exeIsFresh"
        if (Test-Path $unityBuildLog) {
            Write-Host ""
            Write-Host "  Ultimas 30 lineas de $unityBuildLog :" -ForegroundColor Yellow
            Get-Content $unityBuildLog -Tail 30 | ForEach-Object { Write-Host "    $_" }
        }
        exit 1
    }

    Write-Host "  Build OK: $buildTarget" -ForegroundColor Green
    $BuildExe = $buildTarget
} else {
    Write-Host ""
    Write-Host "=== Step 2b: -BuildUnity no activado, se usa el .exe que ya haya en Builds/ ===" -ForegroundColor Yellow
}

# --- Step 3: localizar el build standalone ---
if ($BuildExe -eq "") {
    $candidates = @(
        (Join-Path $projectRoot "Builds\BackroomsSurvivalMMO.exe"),
        (Join-Path $projectRoot "Builds\Windows\BackroomsSurvivalMMO.exe"),
        (Join-Path $projectRoot "Build\BackroomsSurvivalMMO.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $BuildExe = $c; break }
    }
}
if ($BuildExe -eq "" -or -not (Test-Path $BuildExe)) {
    Write-Host ""
    Write-Host "ERROR: build standalone no encontrado. Usa -BuildExe para indicarlo." -ForegroundColor Red
    exit 1
}

$buildExeTime = (Get-Item $BuildExe).LastWriteTime
$backendExe = Join-Path $projectRoot "Builds\Backend\backrooms_server.exe"
$backendExeTime = if (Test-Path $backendExe) { (Get-Item $backendExe).LastWriteTime } else { $null }

Write-Host ""
Write-Host "=== Step 3: build encontrado ===" -ForegroundColor Cyan
Write-Host "  Unity exe:  $BuildExe (modificado $buildExeTime)"
if ($backendExeTime) {
    Write-Host "  Backend exe: $backendExe (modificado $backendExeTime)"
} else {
    Write-Host "  Backend exe: NO ENCONTRADO en $backendExe" -ForegroundColor Red
}

# AVISO DE EXE OBSOLETO — siempre activo, sin parametro que lo desactive del todo
# (-Force solo salta la confirmacion interactiva, no el aviso en si). Compara la
# fecha del .exe de Unity contra el ultimo commit que toca Assets/. Este es el
# fallo que mas corridas de playtest ha costado esta sesion: el script ya imprimia
# la fecha del exe en la linea de arriba, y nadie la miraba.
try {
    $assetsCommitDateRaw = git -C $projectRoot log -1 --date=iso-strict --format=%cd -- Assets/ 2>$null
} catch {
    $assetsCommitDateRaw = $null
}
if ($assetsCommitDateRaw) {
    $assetsCommitTime = [DateTime]::Parse($assetsCommitDateRaw)
    if ($buildExeTime -lt $assetsCommitTime) {
        Write-Host ""
        Write-Host "################################################################" -ForegroundColor Red
        Write-Host "#  AVISO: EL .EXE DE UNITY ES ANTERIOR AL ULTIMO CAMBIO EN Assets/  #" -ForegroundColor Red
        Write-Host "################################################################" -ForegroundColor Red
        Write-Host "  Exe:                  $buildExeTime" -ForegroundColor Red
        Write-Host "  Ultimo commit Assets/: $assetsCommitTime" -ForegroundColor Red
        Write-Host "  Este build casi seguro NO tiene tu ultimo cambio de codigo." -ForegroundColor Red
        Write-Host "  Relanza con -BuildUnity para reconstruirlo, o confirma que sabes" -ForegroundColor Red
        Write-Host "  lo que haces." -ForegroundColor Red
        Write-Host ""
        if (-not $Force) {
            $resp = Read-Host "Continuar de todas formas con este exe? (s/N)"
            if ($resp -notmatch '^[sS]') {
                Write-Host "Abortando." -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host "  -Force activo, continuando pese al aviso." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  (no se pudo determinar la fecha del ultimo commit sobre Assets/ - aviso de exe obsoleto omitido)" -ForegroundColor DarkGray
}

# --- Step 4: preparar carpeta de logs para esta corrida ---
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logDir = Join-Path $projectRoot "Builds\PlaytestLogs\$stamp"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

Write-Host ""
Write-Host "=== Step 4: logs de esta corrida en ===" -ForegroundColor Cyan
Write-Host "  $logDir"

if ($WorldSeed -ne 42) {
    Write-Host ""
    Write-Host "AVISO: -WorldSeed $WorldSeed no tiene efecto hoy (ver cabecera del script) -" -ForegroundColor Yellow
    Write-Host "el host arrancara con seed 42 salvo que cambies el seed a mano en su UI." -ForegroundColor Yellow
}

# --- Step 5: construir la config de cada instancia ---
$instances = @()
for ($i = 1; $i -le $InstanceCount; $i++) {
    $isHost = ($i -eq $HostIndex)
    $ipcPort = $BaseIpcPort + ($i - 1) * $PortStride
    $netPort = $BaseNetPort + ($i - 1) * $PortStride
    $identityKey = "uuid:$IdentityPrefix-$i"
    $logFile = Join-Path $logDir ("instance{0}_{1}.log" -f $i, $(if ($isHost) { "host" } else { "joiner" }))

    $instances += [PSCustomObject]@{
        Index       = $i
        IsHost      = $isHost
        NetId       = $i
        IpcPort     = $ipcPort
        NetPort     = $netPort
        IdentityKey = $identityKey
        LogFile     = $logFile
        PlayerName  = "$IdentityPrefix$i"
    }
}

$hostInstance = $instances | Where-Object { $_.IsHost }

Write-Host ""
Write-Host "=== Plan de lanzamiento ===" -ForegroundColor Cyan
$instances | Format-Table Index, @{L='Rol';E={if($_.IsHost){'HOST'}else{'joiner'}}}, NetId, IpcPort, NetPort, IdentityKey, LogFile -AutoSize | Out-String | Write-Host

# --- Step 6: lanzar. Host primero, siempre. ---
function Start-BackroomsInstance {
    param($inst, $connectTo)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $BuildExe
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path -Parent $BuildExe

    $argList = @("-logFile", "`"$($inst.LogFile)`"")
    if (-not $NoWindowedMode) {
        $argList += @("-screen-width", "$ScreenWidth", "-screen-height", "$ScreenHeight", "-screen-fullscreen", "0")
    }
    $psi.Arguments = ($argList -join " ")

    # EnvironmentVariables ya viene pre-poblado con el entorno actual del proceso
    # padre (PATH, etc.) — solo hace falta sobreescribir lo especifico de esta
    # instancia, igual que hace NetworkInitializer.cs con el backend hijo.
    $psi.EnvironmentVariables["PLAYER_IDENTITY_KEY"] = $inst.IdentityKey
    $psi.EnvironmentVariables["NET_ID"] = "$($inst.NetId)"
    $psi.EnvironmentVariables["IPC_PORT"] = "$($inst.IpcPort)"
    $psi.EnvironmentVariables["NET_PORT"] = "$($inst.NetPort)"
    $psi.EnvironmentVariables["WORLD_SEED"] = "$WorldSeed"

    if ($inst.IsHost) {
        $psi.EnvironmentVariables["SESSION_MODE"] = "host"
        $psi.EnvironmentVariables["NET_NAME"] = $inst.PlayerName
    } else {
        $psi.EnvironmentVariables["CONNECT_TO"] = $connectTo
        $psi.EnvironmentVariables.Remove("SESSION_MODE") | Out-Null
    }

    $proc = [System.Diagnostics.Process]::Start($psi)
    return $proc
}

Write-Host ""
Write-Host "=== Step 6: lanzando ===" -ForegroundColor Cyan

Write-Host ("  [HOST]   instance{0}  identity={1}  ipc={2}  net={3}" -f $hostInstance.Index, $hostInstance.IdentityKey, $hostInstance.IpcPort, $hostInstance.NetPort) -ForegroundColor Green
$hostProc = Start-BackroomsInstance -inst $hostInstance -connectTo $null
Write-Host ("           PID={0}  log={1}" -f $hostProc.Id, $hostInstance.LogFile)

Write-Host ""
Write-Host "  Esperando $WaitSecondsAfterHost s a que el host bindee sus puertos..."
Start-Sleep -Seconds $WaitSecondsAfterHost

$connectTo = "127.0.0.1:$($hostInstance.NetPort)"
foreach ($inst in ($instances | Where-Object { -not $_.IsHost })) {
    Write-Host ("  [joiner] instance{0}  identity={1}  ipc={2}  net={3}  -> connect_to={4}" -f $inst.Index, $inst.IdentityKey, $inst.IpcPort, $inst.NetPort, $connectTo) -ForegroundColor Green
    $proc = Start-BackroomsInstance -inst $inst -connectTo $connectTo
    Write-Host ("           PID={0}  log={1}" -f $proc.Id, $inst.LogFile)
    Start-Sleep -Milliseconds 800
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "  $InstanceCount instancias lanzadas." -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Identidades de esta corrida (fijas por -IdentityPrefix, se repiten si" -ForegroundColor Cyan
Write-Host "  vuelves a correr el script con el mismo prefijo -> sirve para probar" -ForegroundColor Cyan
Write-Host "  persistencia entre reinicios):"
foreach ($inst in $instances) {
    $role = if ($inst.IsHost) { "HOST  " } else { "joiner" }
    Write-Host ("    instance{0} [{1}]  {2}" -f $inst.Index, $role, $inst.IdentityKey)
}
Write-Host ""
Write-Host "  Logs (uno por proceso, Unity + backend ya mezclados con prefijo [Backend]):"
Write-Host "    $logDir"
Write-Host ""
Write-Host "  Para cerrar todo: .\tools\dev\CleanBackroomsProcesses.ps1"
Write-Host "============================================" -ForegroundColor Yellow
