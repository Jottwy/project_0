<#
.SYNOPSIS
    Cambia el App ID de Steam de un build YA HECHO, sin recompilar.

.DESCRIPTION
    Escribe `steam_appid.txt` junto al ejecutable. Es el escalón intermedio de
    `SteamAppConfig`: gana a la constante compilada y pierde contra `BS_STEAM_APPID`.

    Para qué sirve: el mismo build sirve para playtest (Spacewar, 480) y para producción
    (5072740). Sin esto habría que recompilar para cambiar de id, que es exactamente el
    acoplamiento que se quitó del código.

    Ojo con el orden: si en la sesión que lanza el juego hay un `BS_STEAM_APPID` puesto, ése
    manda y este fichero no se lee. El script avisa si lo detecta.

.PARAMETER BuildFolder
    Carpeta que contiene el .exe. Por defecto, el build más reciente bajo Builds/.

.PARAMETER AppId
    480 (Spacewar / desarrollo) o 5072740 (producción). También acepta cualquier otro entero.

.EXAMPLE
    ./tools/dev/SetBuildSteamAppId.ps1 -AppId 480
    ./tools/dev/SetBuildSteamAppId.ps1 -BuildFolder Builds/Build_0.0.1.0e -AppId 5072740
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 4294967295)]
    [uint32] $AppId,

    [string] $BuildFolder
)

$ErrorActionPreference = 'Stop'

if (-not $BuildFolder) {
    $candidate = Get-ChildItem -Path 'Builds' -Directory -ErrorAction SilentlyContinue |
        Where-Object { Get-ChildItem -Path $_.FullName -Filter '*.exe' -File -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw "No se encontró ninguna carpeta de build con un .exe bajo Builds/. Pasa -BuildFolder."
    }
    $BuildFolder = $candidate.FullName
    Write-Output "Build elegido por fecha: $BuildFolder"
}

if (-not (Test-Path -LiteralPath $BuildFolder -PathType Container)) {
    throw "No existe la carpeta $BuildFolder"
}

$exe = Get-ChildItem -Path $BuildFolder -Filter '*.exe' -File |
    Where-Object { $_.Name -notmatch 'UnityCrashHandler' } |
    Select-Object -First 1
if (-not $exe) {
    throw "En $BuildFolder no hay ejecutable del juego; steam_appid.txt ahí no lo lee nadie."
}

# El nativo tiene que estar, o el App ID da igual: SteamClient.Init lanza DllNotFoundException
# antes de mirarlo. Es el fallo que dejó el camino Steam muerto en todos los builds hasta
# 2026-08-31.
$dataFolder = Join-Path $BuildFolder ([System.IO.Path]::GetFileNameWithoutExtension($exe.Name) + '_Data')
$nativePluginPath = Join-Path $dataFolder 'Plugins\x86_64\steam_api64.dll'
$nativeBesideExe = Join-Path $BuildFolder 'steam_api64.dll'
if (-not (Test-Path -LiteralPath $nativePluginPath) -and -not (Test-Path -LiteralPath $nativeBesideExe)) {
    Write-Warning "steam_api64.dll NO está en este build ($nativePluginPath). Steam no inicializará: sin lobby, sin invitaciones y sin navegador. Rehaz el build con el proyecto actualizado."
}

$target = Join-Path $BuildFolder 'steam_appid.txt'
# Sin BOM y sin salto final: el cliente de Steam parsea este fichero por su cuenta.
[System.IO.File]::WriteAllText($target, [string]$AppId, [System.Text.UTF8Encoding]::new($false))

$label = switch ($AppId) {
    480     { '480 (Spacewar / desarrollo)' }
    5072740 { '5072740 (producción)' }
    default { [string]$AppId }
}
Write-Output "steam_appid.txt = $label  ->  $target"

if ($env:BS_STEAM_APPID) {
    Write-Warning "BS_STEAM_APPID=$($env:BS_STEAM_APPID) está puesto en ESTA sesión y gana al fichero. Quítalo (`Remove-Item Env:BS_STEAM_APPID`) o lánzalo desde una consola limpia."
}
