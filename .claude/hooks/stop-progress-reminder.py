#!/usr/bin/env python3
"""Informative, diff-sensitive validation at Stop; never blocks or re-enters."""
import json
import os
import shutil
import subprocess
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def safe_session_id(value):
    return "".join(ch if ch.isalnum() or ch in "._-" else "_" for ch in str(value))


def read_scope(payload, repo_root):
    session_id = payload.get("session_id")
    ledger = None
    if session_id:
        ledger = os.path.join(repo_root, ".claude", f".session-touched-{safe_session_id(session_id)}")
    if ledger and os.path.isfile(ledger):
        try:
            with open(ledger, encoding="utf-8") as handle:
                return sorted({line.strip().replace("\\", "/") for line in handle if line.strip()}), ledger, False
        except OSError as error:
            print(f"[stop-validation] aviso: no se pudo leer el ledger: {error}")

    print("[stop-validation] ledger ausente; usando git diff --name-only (alcance potencialmente impreciso).")
    try:
        result = subprocess.run(
            ["git", "diff", "--name-only"], cwd=repo_root,
            capture_output=True, text=True, timeout=30,
        )
        if result.returncode == 0:
            return sorted({line.strip().replace("\\", "/") for line in result.stdout.splitlines() if line.strip()}), None, True
        print(f"[stop-validation] aviso: git diff fallo: {result.stderr.strip()}")
    except (OSError, subprocess.TimeoutExpired) as error:
        print(f"[stop-validation] aviso ambiental al leer git diff: {error}")
    return [], None, True


def run_check(label, command, repo_root, timeout):
    try:
        result = subprocess.run(command, cwd=repo_root, capture_output=True, text=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as error:
        print(f"[stop-validation] {label}: OMITIDO por entorno: {error}")
        return False
    if result.returncode == 0:
        print(f"[stop-validation] {label}: OK")
        return True
    output = (result.stdout + "\n" + result.stderr).strip()
    print(f"[stop-validation] {label}: FALLO (exit {result.returncode})")
    if output:
        print(output[-4000:])
    return False


def report_startup_budget(repo_root):
    """Aviso NO bloqueante del presupuesto de arranque de sesion.

    El gate de verdad es /checkpoint; esto solo pone la cifra delante para que nadie descubra que
    STATE.md ha vuelto a crecer tres semanas despues.
    """
    script = os.path.join(repo_root, "tools", "dev", "CheckStateBudget.py")
    if not os.path.isfile(script):
        return
    python = shutil.which("python") or shutil.which("python3")
    if not python:
        return
    environment = dict(os.environ, PYTHONIOENCODING="utf-8")
    try:
        result = subprocess.run(
            [python, script], cwd=repo_root, capture_output=True, text=True, timeout=30, env=environment,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        print(f"[stop-validation] presupuesto de arranque: OMITIDO por entorno: {error}")
        return
    for line in result.stdout.splitlines():
        if line.startswith(("lineas ", "bytes  ", "SUMA", "ROJO:", "  - ")):
            print(f"[stop-validation] arranque: {line}")
    if result.returncode != 0:
        print("[stop-validation] arranque: en ROJO. Lo bloquea /checkpoint, no este aviso.")


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        payload = {}
    if payload.get("stop_hook_active"):
        return 0

    repo_root = os.environ.get("CLAUDE_HOOK_REPO_ROOT", REPO_ROOT)
    paths, ledger, _ = read_scope(payload, repo_root)
    try:
        if any(path.startswith("docs/") or path == "CLAUDE.md" for path in paths):
            report_startup_budget(repo_root)
        rust_touched = any(path.endswith(".rs") for path in paths)
        csharp_paths = [path for path in paths if path.endswith(".cs")]
        if not rust_touched and not csharp_paths:
            print("[stop-validation] solo docs/config/tooling; no se ejecutan suites de codigo.")
            return 0

        # Solo fmt. clippy y cargo test viven en /checkpoint, que SI bloquea; aqui corrian en
        # CADA Stop del modelo con timeout de 600 s -- diez paradas eran hasta diez suites enteras
        # para un aviso que nunca bloquea. Mismo motivo para el compile-check de C#.
        if rust_touched:
            prefix = ["cargo", "+stable-x86_64-pc-windows-gnu"]
            run_check(
                "Rust fmt", prefix + ["fmt", "--manifest-path", "backend/Cargo.toml", "--all", "--", "--check"],
                repo_root, 120,
            )
            print("[stop-validation] clippy y tests: NO se ejecutan aqui; son el gate de /checkpoint.")
        if csharp_paths:
            print("[stop-validation] C#: compile-check y format son el gate de /checkpoint.")
        return 0
    finally:
        if ledger:
            try:
                os.remove(ledger)
                print(f"[stop-validation] ledger consumido: {os.path.basename(ledger)}")
            except OSError as error:
                print(f"[stop-validation] aviso: no se pudo borrar el ledger: {error}")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"[stop-validation] aviso inesperado; Stop no bloqueado: {error}")
    sys.exit(0)
