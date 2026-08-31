# run-t0-probes.ps1 — T0 de PLAN-PLANTAS-ALTAS.md
#
# Ejecuta las tres sondas de medición de población por planta y deja la salida completa en
# docs/measurements/. No toca producción: las sondas son tests `#[ignore]` que imprimen y no exigen.
#
# Uso:  powershell -ExecutionPolicy Bypass -File tools/run-t0-probes.ps1 [-Label T5]

# ETIQUETA en el nombre, y no solo la fecha: sin ella, volver a pasar las sondas el MISMO dia
# sobrescribe la medida anterior — que es justo lo que hace inutil una linea base. Ya paso una vez.
param([string]$Label = 'T0')

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$backend = Join-Path $repo 'backend'
$outDir = Join-Path $repo 'docs/measurements'

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }


$stamp = Get-Date -Format 'yyyy-MM-dd'
$outFile = Join-Path $outDir "$Label-plantas-altas-$stamp.txt"

$probes = @(
    'probe_wg3_storey_to_wg2_layer',
    'probe_population_by_storey',
    'probe_storey_change_evicts_by_layer'
)

Push-Location $backend
try {
    $lines = @()
    $lines += "$Label - PLAN-PLANTAS-ALTAS.md - sondas de poblacion por planta"
    $lines += "Fecha: $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    $lines += "Commit: $(git rev-parse --short HEAD 2>$null)"
    $lines += ('=' * 78)

    foreach ($p in $probes) {
        Write-Host "[T0] ejecutando $p ..." -ForegroundColor Cyan
        $lines += ''
        $lines += ('--- ' + $p + ' ' + ('-' * 40))
        # SIN `2>&1`: en PowerShell 5.1 redirigir el stderr de un .exe envuelve cada linea en un
        # ErrorRecord (NativeCommandError) y pone $? a $false aunque cargo devuelva 0. La salida de
        # los tests va por stdout, asi que no hace falta.
        $out = & cargo test $p -- --ignored --nocapture
        if ($LASTEXITCODE -ne 0) {
            $lines += $out
            $lines | Out-File -FilePath $outFile -Encoding utf8
            throw "la sonda $p fallo (exit $LASTEXITCODE)"
        }
        # Solo las lineas de la sonda: cargo mete ruido de compilacion que envejece mal en un fichero
        # de medidas.
        $lines += ($out | Where-Object { $_ -match '^\[T0\]|^test result:' })
    }

    $lines | Out-File -FilePath $outFile -Encoding utf8
    Write-Host ''
    Write-Host "[T0] resultados en: $outFile" -ForegroundColor Green
}
finally {
    Pop-Location
}
