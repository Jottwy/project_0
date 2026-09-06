#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PostToolUse unico: ledger de ficheros tocados + `cargo fmt --check` si el edit fue Rust.

Sustituye a `postedit-check-csharp.py` y `postedit-check-rust.py`. Eran dos entradas de
settings.json sobre el mismo evento y el mismo matcher (`Edit|Write`), con la MISMA funcion
`append_ledger` copiada en las dos: el ledger se escribia dos veces por cada edicion, con las dos
rutas duplicadas dentro. El de C# no hacia nada mas que eso, asi que aqui no se pierde nada.

Que hace, en este orden:

1. Apunta las rutas editadas en `.claude/.session-touched-<session_id>`. Sirve para saber que tocO
   ESTA sesion. NO es el alcance de un commit — eso sale de `git diff --cached` (ver
   `tools/dev/validate-scope.ps1`).
2. Si alguna ruta es `.rs`, corre `cargo fmt --all -- --check`. Es el unico gate lo bastante
   barato para correr por edicion; clippy y los tests corren en el gate de commit.

FAIL CLOSED en lo inesperado: si el hook revienta, salida 2. Un fallo ambiental conocido (cargo no
esta, se agota el tiempo) SI deja pasar la edicion con un aviso: bloquear ahi solo impediria
trabajar en una maquina sin toolchain, y el gate de commit vuelve a mirarlo antes de que nada
entre al repositorio.
"""
import json
import os
import subprocess
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LEDGER_ROOT = os.environ.get("CLAUDE_HOOK_LEDGER_ROOT", REPO_ROOT)

FMT_COMMAND = [
    "cargo", "+stable-x86_64-pc-windows-gnu", "fmt",
    "--manifest-path", "backend/Cargo.toml", "--all", "--", "--check",
]


def edited_paths(tool_input):
    paths = []
    if tool_input.get("file_path"):
        paths.append(tool_input["file_path"])
    for edit in tool_input.get("edits", []) or []:
        if edit.get("file_path"):
            paths.append(edit["file_path"])
    return [str(path).replace("\\", "/") for path in paths]


def append_ledger(session_id, paths):
    if not session_id:
        return
    safe_id = "".join(ch if ch.isalnum() or ch in "._-" else "_" for ch in str(session_id))
    ledger = os.path.join(LEDGER_ROOT, ".claude", ".session-touched-%s" % safe_id)
    try:
        with open(ledger, "a", encoding="utf-8") as handle:
            for path in paths:
                handle.write(path + "\n")
    except OSError as error:
        sys.stderr.write("[postedit-check] aviso: no se pudo escribir el ledger: %s\n" % error)


def check_rust_format():
    """0 si el formato esta bien o no se pudo comprobar; 2 si `cargo fmt` dice que no."""
    try:
        result = subprocess.run(FMT_COMMAND, cwd=REPO_ROOT, capture_output=True,
                                text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as error:
        sys.stderr.write("[postedit-check] aviso ambiental, se omite cargo fmt: %s\n" % error)
        return 0
    if result.returncode != 0:
        sys.stderr.write(
            "[postedit-check] cargo fmt --all -- --check fallo:\n%s\n%s\n"
            "Corrige el formato antes de cerrar.\n" % (result.stdout, result.stderr))
        return 2
    return 0


def main():
    payload = json.load(sys.stdin)
    if payload.get("tool_name") not in {"Edit", "Write"}:
        return 0

    paths = edited_paths(payload.get("tool_input", {}) or {})
    if not paths:
        return 0

    # El ledger se escribe SIEMPRE y ANTES del gate: si cargo fmt bloquea, la ruta editada sigue
    # siendo parte del alcance de la sesion y tiene que constar.
    append_ledger(payload.get("session_id"), paths)

    if any(path.endswith(".rs") for path in paths):
        return check_rust_format()
    return 0


if __name__ == "__main__":
    try:
        status = main()
    except Exception as error:            # noqa: BLE001 - fail closed a proposito
        sys.stderr.write(
            "[postedit-check] BLOQUEADO: el hook fallo (%r). Fail closed: un bug del hook no "
            "puede convertirse en un permiso.\n" % (error,))
        status = 2
    sys.exit(2 if status == 2 else 0)
