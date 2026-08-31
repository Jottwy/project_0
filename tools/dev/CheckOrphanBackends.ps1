<#
.SYNOPSIS
  Cuenta procesos backrooms_server.exe vivos y los puertos que ocupan.

.DESCRIPTION
  La sonda de la invariante "abandonar una sesion no deja backend huerfano".

  POR QUE HACE FALTA UNA SONDA EXTERNA. Un huerfano es INVISIBLE desde dentro del juego: el
  backend no se apaga solo cuando su IPC local se cae (game_loop.rs guarda el mundo en
  `local_disconnect_rx` y SIGUE corriendo, a proposito — una recompilacion del editor rebota esa
  conexion y no es motivo para matar la partida). Y como NetworkInitializer.SelectLaunchConfig
  busca un puerto libre cuando el suyo esta ocupado, la sesion SIGUIENTE arranca sin un solo
  error: el sintoma no es un fallo, es un proceso de mas por cada vuelta al menu, y un joiner que
  teclea 7778 aterrizando en el backend de la partida ANTERIOR.

  Uso tipico, alrededor de un ciclo completo:

      tools/dev/CheckOrphanBackends.ps1 -Label antes
      # ... Host -> jugar -> Quit to Menu ...
      tools/dev/CheckOrphanBackends.ps1 -Label despues -ExpectCount 0

  Con -ExpectCount devuelve codigo de salida 1 si no cuadra, para encadenarlo en un script.

.PARAMETER Label
  Etiqueta que se imprime con el recuento. Sin identidad propia, dos medidas del mismo dia se
  confunden (leccion de run-t0-probes.ps1, que llego a sobrescribir su propio fichero de T0).

.PARAMETER ExpectCount
  Numero esperado de backends vivos. -1 (por defecto) = solo informar, nunca fallar.

.PARAMETER Kill
  Mata lo que encuentre. Para limpiar entre reproducciones manuales; NO usar durante una partida.
#>
[CmdletBinding()]
param(
    [string]$Label = "",
    [int]$ExpectCount = -1,
    [switch]$Kill
)

$ErrorActionPreference = 'Stop'

$procs = @(Get-Process -Name 'backrooms_server' -ErrorAction SilentlyContinue)
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$tag = if ([string]::IsNullOrWhiteSpace($Label)) { '' } else { " [$Label]" }

Write-Host "== backrooms_server$tag @ $stamp =="
Write-Host "vivos: $($procs.Count)"

foreach ($p in $procs) {
    $started = try { $p.StartTime.ToString('HH:mm:ss') } catch { '?' }
    Write-Host ("  PID {0,-7} desde {1}" -f $p.Id, $started)
}

# Los puertos que ocupan. Es la mitad que importa: un huerfano sin puertos es inofensivo, uno
# sentado en el 7778 se come el Join de la sesion siguiente.
if ($procs.Count -gt 0) {
    $ids = $procs | ForEach-Object { $_.Id }
    $tcp = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
             Where-Object { $ids -contains $_.OwningProcess })
    $udp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
             Where-Object { $ids -contains $_.OwningProcess })

    foreach ($c in $tcp) { Write-Host ("  TCP  {0}:{1}  PID {2}" -f $c.LocalAddress, $c.LocalPort, $c.OwningProcess) }
    foreach ($c in $udp) { Write-Host ("  UDP  {0}:{1}  PID {2}" -f $c.LocalAddress, $c.LocalPort, $c.OwningProcess) }
}

if ($Kill -and $procs.Count -gt 0) {
    foreach ($p in $procs) {
        Write-Host "  matando PID $($p.Id)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
}

if ($ExpectCount -ge 0 -and $procs.Count -ne $ExpectCount) {
    Write-Host "FALLO: se esperaban $ExpectCount y hay $($procs.Count)."
    exit 1
}

exit 0
