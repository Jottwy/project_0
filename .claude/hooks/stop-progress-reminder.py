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


def validate_csharp(paths, repo_root):
    project = os.path.join(repo_root, "Assembly-CSharp.csproj")
    dotnet = shutil.which("dotnet")
    if not dotnet or not os.path.isfile(project):
        print("[stop-validation] C#: OMITIDO; no hay dotnet + Assembly-CSharp.csproj generado compatible.")
        return
    relative = []
    for path in paths:
        absolute = path if os.path.isabs(path) else os.path.join(repo_root, path)
        relative.append(os.path.relpath(absolute, repo_root))
    command = [dotnet, "format", "Assembly-CSharp.csproj", "--verify-no-changes"]
    for path in relative:
        command.extend(["--include", path])
    run_check("C# dotnet format", command, repo_root, 300)


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        payload = {}
    if payload.get("stop_hook_active"):
        return 0

    repo_root = payload.get("cwd") or REPO_ROOT
    paths, ledger, _ = read_scope(payload, repo_root)
    try:
        rust_touched = any(path.endswith(".rs") for path in paths)
        csharp_paths = [path for path in paths if path.endswith(".cs")]
        if not rust_touched and not csharp_paths:
            print("[stop-validation] solo docs/config/tooling; no se ejecutan suites de codigo.")
            return 0

        if rust_touched:
            prefix = ["cargo", "+stable-x86_64-pc-windows-gnu"]
            run_check(
                "Rust fmt", prefix + ["fmt", "--manifest-path", "backend/Cargo.toml", "--all", "--", "--check"],
                repo_root, 120,
            )
            run_check(
                "Rust clippy", prefix + ["clippy", "--manifest-path", "backend/Cargo.toml", "--all-targets", "--", "-D", "warnings"],
                repo_root, 300,
            )
            run_check(
                "Rust tests", prefix + ["test", "--manifest-path", "backend/Cargo.toml"],
                repo_root, 300,
            )
        if csharp_paths:
            validate_csharp(csharp_paths, repo_root)
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
