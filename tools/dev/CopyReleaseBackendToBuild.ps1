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

Copy-Item -Path $source -Destination $dest -Force

$srcSize = (Get-Item $source).Length
$dstSize = (Get-Item $dest).Length
$sizeMB = [math]::Round($dstSize / 1MB, 2)

Write-Host ""
Write-Host "Backend copied successfully." -ForegroundColor Green
Write-Host "  Source:      $source"
Write-Host "  Destination: $dest"
Write-Host "  Size:        $sizeMB MB ($dstSize bytes)"

if ($srcSize -ne $dstSize) {
    Write-Host "WARNING: Size mismatch between source and destination!" -ForegroundColor Red
}
