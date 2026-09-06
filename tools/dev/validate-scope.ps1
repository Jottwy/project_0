# Gate de commit: valida SOLO lo que este commit toca.
#
#     powershell -ExecutionPolicy Bypass -File tools/dev/validate-scope.ps1
#
# Salida 0 = verde, 2 = rojo. Lo invocan el hook PreToolUse (.claude/hooks/pretool-guard.py,
# que intercepta `git commit` antes de que Bash lo ejecute) y, si se activa, el hook de git
# .githooks/pre-commit.
#
# EL ALCANCE SALE DE `git diff --cached --name-only`, NO DEL LEDGER de sesión
# (`.claude/.session-touched-<id>`). El ledger dice qué tocó ESTA sesión; el commit puede
# llevar más (trabajo de una sesión anterior, un rebase, una resolución de conflicto) o menos
# (ficheros editados y luego descartados). Lo que entra al repositorio es el índice, así que es
# el índice lo que hay que validar. Además el ledger no existe si no hay session_id.
#
# Por lo mismo, los dos gates de documentación corren con `--staged`: miden el contenido del
# índice, no el del árbol de trabajo. Un STATE.md recortado pero sin estacionar, o un
# DECISIONS-INDEX.md regenerado y no añadido, pasarían un chequeo sobre el disco y entrarían al
# repositorio mal igual.
#
# PS 5.1: sin `&&`, sin `?:`, sin `??`. Los encadenamientos van con `if ($LASTEXITCODE -ne 0)`.

$ErrorActionPreference = 'Stop'

$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Gate {
    param(
        [string] $Name,
        [string] $Exe,
        [string[]] $GateArgs
    )
    Write-Host ""
    Write-Host "=== $Name ==="
    & $Exe @GateArgs
    if ($LASTEXITCODE -ne 0) {
        $script:failures.Add("$Name (salida $LASTEXITCODE)")
    }
}

# Raiz del repo: el hook puede invocarnos desde cualquier cwd, y los dos scripts de Python
# resuelven sus rutas en relativo.
$root = (& git rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
    Write-Error "no estoy dentro de un repositorio git"
    exit 2
}
Set-Location -LiteralPath $root

# --diff-filter=ACMR: ficheros que el commit ANADE o DEJA en el arbol. Un fichero borrado (D) no
# se compila ni se mide, pero si puede cambiar el resultado de una suite, asi que su extension
# tambien cuenta para decidir que gate correr.
$staged = @(& git diff --cached --name-only --diff-filter=ACMRD)
if ($staged.Count -eq 0) {
    Write-Host "nada estacionado: el commit fallara por si solo, no hay nada que validar"
    exit 0
}

Write-Host "--- alcance del commit: $($staged.Count) ruta(s) ---"
foreach ($path in $staged) { Write-Host "  $path" }

$touchesRust = @($staged | Where-Object {
    $_ -like 'backend/*.rs' -or $_ -like 'backend/*/*.rs' -or $_ -like 'backend/Cargo.*'
}).Count -gt 0
$touchesCSharp = @($staged | Where-Object { $_ -like 'Assets/*' -and $_ -like '*.cs' }).Count -gt 0

# Los dos gates de documentacion corren SIEMPRE, toque o no el commit a docs/: son baratos
# (milisegundos, sin compilar nada) y su trabajo es que el arranque de sesion siga cabiendo en
# una lectura. Un commit de codigo que no los toca los pasa sin coste.
Invoke-Gate -Name 'presupuesto de arranque (--staged)' -Exe 'python' `
    -GateArgs @('tools/dev/CheckStateBudget.py', '--staged')
Invoke-Gate -Name 'indice de ADR al dia (--check --staged)' -Exe 'python' `
    -GateArgs @('tools/dev/GenDecisionsIndex.py', '--check', '--staged')

if ($touchesRust) {
    Invoke-Gate -Name 'cargo fmt' -Exe 'cargo' -GateArgs @(
        '+stable-x86_64-pc-windows-gnu', 'fmt',
        '--manifest-path', 'backend/Cargo.toml', '--all', '--', '--check')
    Invoke-Gate -Name 'cargo clippy -D warnings' -Exe 'cargo' -GateArgs @(
        '+stable-x86_64-pc-windows-gnu', 'clippy',
        '--manifest-path', 'backend/Cargo.toml', '--all-targets', '--', '-D', 'warnings')
    Invoke-Gate -Name 'cargo test --bin backrooms_server' -Exe 'cargo' -GateArgs @(
        '+stable-x86_64-pc-windows-gnu', 'test',
        '--manifest-path', 'backend/Cargo.toml', '--bin', 'backrooms_server')
} else {
    Write-Host ""
    Write-Host "=== Rust: el commit no toca backend/, se omite ==="
}

if ($touchesCSharp) {
    # CompileCheckClient.sh compila con el Roslyn que trae Unity, sin abrir el editor. OJO: el
    # .csproj del que saca las referencias es una FOTO que Unity regenera al refrescar; si el
    # asmdef cambio y Unity no ha refrescado, este gate puede dar verde y Unity fallar luego con
    # CS0246 (ver la cabecera del propio script).
    Invoke-Gate -Name 'CompileCheckClient (4 asambleas)' -Exe 'bash' `
        -GateArgs @('tools/dev/CompileCheckClient.sh')
} else {
    Write-Host ""
    Write-Host "=== C#: el commit no toca Assets/**.cs, se omite ==="
}

Write-Host ""
Write-Host "--- RESULTADO DEL GATE DE COMMIT ---"
if ($failures.Count -gt 0) {
    Write-Host "ROJO:"
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 2
}
Write-Host "VERDE"
exit 0
