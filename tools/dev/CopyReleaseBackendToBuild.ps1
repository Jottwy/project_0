# CopyReleaseBackendToBuild.ps1
# Copies the release-built backend server to the Builds directory.

$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition))
$source = Join-Path $projectRoot "backend\target\release\backrooms_server.exe"
$destDir = Join-Path $projectRoot "Builds\Backend"
$dest = Join-Path $destDir "backrooms_server.exe"

if (-not (Test-Path $source)) {
    Write-Host "ERROR: Source not found:" -ForegroundColor Red
    Write-Host "  $source"
    Write-Host ""
    Write-Host "Build the backend first:  cargo build --release  (from backend/)" -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Write-Host "Created directory: $destDir" -ForegroundColor Cyan
}

# BUG encontrado 2026-09-04: este script decia "Backend copied successfully" AUNQUE EL
# COPIADO FALLARA. `Copy-Item -Force` sobre un exe que otro proceso tiene abierto lanza un
# error NO terminante: el mensaje se va por stderr, el script sigue, imprime el OK en verde y
# devuelve exit 0. Un playtest arranca entonces el binario VIEJO y lo que se mide no es lo que
# se compilo. Paso: Unity habia lanzado su propio backrooms_server desde Builds\Backend y lo
# tenia bloqueado; se detecto comparando hashes a mano, porque el script juraba que todo bien.
#
# Se INTENTA copiar primero y se diagnostica despues, no al reves. Un backend vivo no siempre
# tiene bloqueado ESTE fichero -puede estar corriendo desde target/release, o desde una copia-,
# y un guardia que mira la lista de procesos antes de tocar nada rechaza despliegues que iban a
# funcionar. Quien manda es el copiado.
try {
    Copy-Item -Path $source -Destination $dest -Force -ErrorAction Stop
} catch {
    Write-Host ""
    Write-Host "ERROR: no se pudo copiar el backend." -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)"
    # Y quien lo bloquea es casi siempre un backend vivo: decirlo por su nombre ahorra el rato
    # de averiguarlo.
    $holders = @(Get-Process backrooms_server -ErrorAction SilentlyContinue)
    if ($holders.Count -gt 0) {
        Write-Host ""
        Write-Host "Hay $($holders.Count) backrooms_server.exe corriendo:" -ForegroundColor Yellow
        foreach ($p in $holders) {
            Write-Host ("  PID {0}  arrancado {1}" -f $p.Id, $p.StartTime)
        }
        Write-Host "Sal de Play en Unity, o matalos:  tools\dev\CleanBackroomsProcesses.ps1"
    }
    exit 1
}

# **Y se verifica por HASH, no por tamano.** Dos compilaciones del mismo proyecto pesan
# practicamente lo mismo: el tamano habria dado por bueno cualquier binario viejo del dia.
$srcHash = (Get-FileHash $source).Hash
$dstHash = (Get-FileHash $dest).Hash
if ($srcHash -ne $dstHash) {
    Write-Host ""
    Write-Host "ERROR: el destino NO coincide con el binario compilado." -ForegroundColor Red
    Write-Host "  source: $srcHash"
    Write-Host "  dest:   $dstHash"
    exit 1
}

$dstSize = (Get-Item $dest).Length
$sizeMB = [math]::Round($dstSize / 1MB, 2)

Write-Host ""
Write-Host "Backend copied successfully." -ForegroundColor Green
Write-Host "  Source:      $source"
Write-Host "  Destination: $dest"
Write-Host "  Size:        $sizeMB MB ($dstSize bytes)"
Write-Host "  SHA256:      $dstHash"

# BUG encontrado 2026-08-07: este script nunca llamaba `exit 0` en su camino de exito -
# Copy-Item/Get-Item/Write-Host son cmdlets, ninguno toca $LASTEXITCODE, asi que quien lo
# invoque via `&` y mire $LASTEXITCODE despues hereda el valor STALE del ultimo .exe nativo
# que corriera antes en esa sesion (nunca 0 a proposito). RunMultiInstancePlaytest.ps1 lo
# sufrio: imprimia "Backend copied successfully" y acto seguido abortaba por esto. Exit
# explicito para que $LASTEXITCODE sea correcto para CUALQUIER futuro llamante, no solo el
# que lo destapo.
exit 0
