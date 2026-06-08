# CollectNetworkLogs.ps1
# Reads host.log and joiner.log from Builds/ and extracts network diagnostic info.
# Output: Builds/network_diagnostic_summary.txt

param(
    [string]$BuildsDir = "",
    [string]$HostLog = "",
    [string]$JoinerLog = ""
)

$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition))

if ($BuildsDir -eq "") {
    $BuildsDir = Join-Path $projectRoot "Builds"
}

if ($HostLog -eq "") { $HostLog = Join-Path $BuildsDir "host.log" }
if ($JoinerLog -eq "") { $JoinerLog = Join-Path $BuildsDir "joiner.log" }

$outputFile = Join-Path $BuildsDir "network_diagnostic_summary.txt"

# MPTRACE steps we care about
$traceSteps = @(
    "step=H",   # build_world_state
    "step=I",   # ipc_serialize_world_state
    "step=J",   # unity_parse_world_state
    "step=K",   # remote_player_manager_receive / spawn
    "step=U",   # remote_transform_apply
    "step=W",   # host_world_snapshot_created
    "step=X",   # send_world_snapshot
    "step=Y",   # receive_world_snapshot
    "step=Z",   # apply_world_snapshot
    "step=AA",  # unity_parse_world_snapshot
    "step=AB"   # future steps
)

# Additional keywords to filter
$keywords = @(
    "RemotePlayers",
    "remote_players",
    "WorldSync",
    "WorldState",
    "world_sync",
    "world_state"
)

$errorPatterns = @(
    "(?i)error",
    "(?i)exception",
    "(?i)panic",
    "(?i)WARN",
    "(?i)failed"
)

function Filter-LogFile {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path $Path)) {
        Write-Host "  [SKIP] $Label not found: $Path" -ForegroundColor Yellow
        return @("=== $Label === (FILE NOT FOUND: $Path)", "")
    }

    $lines = Get-Content $Path -ErrorAction SilentlyContinue
    if ($null -eq $lines -or $lines.Count -eq 0) {
        Write-Host "  [SKIP] $Label is empty: $Path" -ForegroundColor Yellow
        return @("=== $Label === (EMPTY FILE)", "")
    }

    Write-Host "  [READ] $Label : $($lines.Count) lines" -ForegroundColor Green

    $results = @()
    $results += "=== $Label === ($Path, $($lines.Count) lines)"
    $results += ""

    # Section 1: MPTRACE lines
    $results += "--- MPTRACE Steps ---"
    $traceCount = 0
    foreach ($line in $lines) {
        foreach ($step in $traceSteps) {
            if ($line -match "MPTRACE" -and $line -match [regex]::Escape($step)) {
                $results += $line
                $traceCount++
                break
            }
        }
    }
    if ($traceCount -eq 0) { $results += "(none found)" }
    $results += ""

    # Section 2: RemotePlayers / WorldSync keywords
    $results += "--- RemotePlayers / WorldSync ---"
    $kwCount = 0
    foreach ($line in $lines) {
        if ($line -match "MPTRACE") { continue }  # already captured above
        foreach ($kw in $keywords) {
            if ($line -match [regex]::Escape($kw)) {
                $results += $line
                $kwCount++
                break
            }
        }
    }
    if ($kwCount -eq 0) { $results += "(none found)" }
    $results += ""

    # Section 3: Errors / Exceptions
    $results += "--- Errors / Exceptions / Warnings ---"
    $errCount = 0
    foreach ($line in $lines) {
        foreach ($pat in $errorPatterns) {
            if ($line -match $pat) {
                $results += $line
                $errCount++
                break
            }
        }
    }
    if ($errCount -eq 0) { $results += "(none found)" }
    $results += ""

    # Summary
    $results += "--- Summary: $Label ---"
    $results += "  Total lines:    $($lines.Count)"
    $results += "  MPTRACE hits:   $traceCount"
    $results += "  Keyword hits:   $kwCount"
    $results += "  Error hits:     $errCount"
    $results += ""

    return $results
}

# ─── Main ───

Write-Host ""
Write-Host "=== Network Log Diagnostic Collector ===" -ForegroundColor Cyan
Write-Host "  Builds dir: $BuildsDir"
Write-Host ""

$output = @()
$output += "Network Diagnostic Summary"
$output += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$output += "Project: $projectRoot"
$output += ""

# Process host log
$output += Filter-LogFile -Path $HostLog -Label "HOST"

# Process joiner log
$output += Filter-LogFile -Path $JoinerLog -Label "JOINER"

# Also check for Unity player logs (common locations)
$unityLogPaths = @(
    "$env:APPDATA\..\LocalLow\DefaultCompany\BackroomsSurvivalMMO\Player.log",
    "$env:APPDATA\..\LocalLow\DefaultCompany\BackroomsSurvivalMMO\Player-prev.log"
)

foreach ($ulog in $unityLogPaths) {
    $resolved = [System.Environment]::ExpandEnvironmentVariables($ulog)
    if (Test-Path $resolved) {
        Write-Host "  [FOUND] Unity log: $resolved" -ForegroundColor Cyan
        $output += Filter-LogFile -Path $resolved -Label "UNITY ($resolved)"
    }
}

# Write output
if (-not (Test-Path $BuildsDir)) {
    New-Item -ItemType Directory -Force -Path $BuildsDir | Out-Null
}

$output | Out-File -FilePath $outputFile -Encoding UTF8

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "  Output: $outputFile"
Write-Host "  Lines:  $($output.Count)"
Write-Host ""
Write-Host "Tip: Place host.log and joiner.log in $BuildsDir before running." -ForegroundColor Yellow
Write-Host "     You can redirect backend output with:" -ForegroundColor Yellow
Write-Host '     backrooms_server.exe 2>&1 | Tee-Object -FilePath host.log' -ForegroundColor DarkGray
