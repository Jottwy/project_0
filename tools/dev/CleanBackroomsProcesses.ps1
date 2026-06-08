# CleanBackroomsProcesses.ps1
# Kills any running BackroomsSurvivalMMO and backrooms_server processes.

$targets = @("BackroomsSurvivalMMO", "backrooms_server")
$killed = @()

foreach ($name in $targets) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($procs) {
        foreach ($p in $procs) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction Stop
                $killed += "$name (PID $($p.Id))"
            } catch {
                Write-Warning "Could not kill $name PID $($p.Id): $_"
            }
        }
    }
}

if ($killed.Count -eq 0) {
    Write-Host "No backrooms processes found running." -ForegroundColor Yellow
} else {
    Write-Host "Killed $($killed.Count) process(es):" -ForegroundColor Green
    foreach ($k in $killed) {
        Write-Host "  - $k"
    }
}
