# Reproduccion del mecanismo del huerfano, a nivel de proceso y sin Unity.
#
# HIPOTESIS: el backend NO se apaga cuando su cliente IPC local se cae. Si Unity abandona la
# sesion sin llamar a KillBackend (que es lo que hacia el `Quit to Menu` del vendor), el proceso
# se queda vivo ocupando su TCP y su UDP.
#
# Este script hace exactamente eso: lanza un backend host, se conecta a su IPC, cierra el socket,
# espera, y comprueba si sigue vivo.

$ErrorActionPreference = 'Stop'
$root = 'j:\Unity\BackroomsSurvivalMMO'
$exe  = Join-Path $root 'Builds\Backend\backrooms_server.exe'
$ipc  = 17777
$net  = 17778

Write-Host "== antes =="
@(Get-Process backrooms_server -ErrorAction SilentlyContinue).Count

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.WorkingDirectory = $env:TEMP
$psi.EnvironmentVariables.Clear()
$psi.EnvironmentVariables['SystemRoot'] = $env:SystemRoot
$psi.EnvironmentVariables['IPC_PORT']   = "$ipc"
$psi.EnvironmentVariables['IPC_ADDR']   = "127.0.0.1:$ipc"
$psi.EnvironmentVariables['NET_PORT']   = "$net"
$psi.EnvironmentVariables['NET_ID']     = '1'
$psi.EnvironmentVariables['NET_NAME']   = 'ReproHost'
$psi.EnvironmentVariables['WORLD_SEED'] = '42'
$psi.EnvironmentVariables['RUST_LOG']   = 'info'
$psi.EnvironmentVariables['BACKROOMS_WG3'] = '1'
$psi.EnvironmentVariables['BACKROOMS_WG3_MANIFEST'] = (Join-Path $root 'Assets\StreamingAssets\wg3_manifest.json')

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "lanzado PID $($p.Id)"

# Esperar a que el IPC escuche.
$client = $null
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 200
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', $ipc)
        break
    } catch { $client = $null }
}
if ($null -eq $client) { Write-Host 'FALLO: el IPC nunca escucho'; $p.Kill(); exit 2 }
Write-Host "IPC conectado tras $([math]::Round($i*0.2,1))s"

# Y ahora lo que hacia el `Quit to Menu`: soltar el IPC y no matar nada.
$client.Close()
Write-Host 'IPC cerrado (Unity "vuelve al menu" sin matar el backend)'
Start-Sleep -Seconds 5

$alive = -not $p.HasExited
Write-Host "backend vivo 5 s despues del cierre del IPC: $alive"
if ($alive) {
    $tcp = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $p.Id })
    $udp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $p.Id })
    foreach ($c in $tcp) { Write-Host ("  sigue escuchando TCP {0}:{1}" -f $c.LocalAddress, $c.LocalPort) }
    foreach ($c in $udp) { Write-Host ("  sigue ocupando   UDP {0}:{1}" -f $c.LocalAddress, $c.LocalPort) }
    Write-Host 'CONFIRMADO: huerfano. Solo desaparece si alguien lo mata explicitamente.'
    $p.Kill()
    $p.WaitForExit(5000)
} else {
    Write-Host 'El backend se apago solo: la hipotesis era falsa.'
}

Write-Host '== despues (tras la limpieza) =='
@(Get-Process backrooms_server -ErrorAction SilentlyContinue).Count
