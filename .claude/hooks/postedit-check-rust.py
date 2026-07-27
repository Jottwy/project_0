#!/usr/bin/env python3
"""PostToolUse check, lado Rust del piloto ADR-024. Corre cargo fmt --check +
cargo clippy solo si el archivo editado es uno de los 6 archivos de la
superficie de pose relay (ver .claude/rules/pose-relay-wire-rust.md). Fuera
de ese alcance, no-op silencioso. Cero llamadas a modelo.
"""
import json
import os
import subprocess
import sys

SCOPE_FILES = {
    "backend/src/player/mod.rs",
    "backend/src/player/session.rs",
    "backend/src/ipc/mod.rs",
    "backend/src/network/protocol.rs",
    "backend/src/network/peer.rs",
    "backend/src/network/sync.rs",
    "backend/src/game_loop.rs",
}


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    if payload.get("tool_name") != "Edit":
        return 0

    tool_input = payload.get("tool_input", {}) or {}
    file_path = str(tool_input.get("file_path", "")).replace("\\", "/")

    if not any(file_path.endswith(scoped) for scoped in SCOPE_FILES):
        return 0  # fuera del piloto, no-op

    repo_root = payload.get("cwd") or os.getcwd()
    backend_dir = os.path.join(repo_root, "backend")
    if not os.path.isdir(backend_dir):
        sys.stderr.write(f"[postedit-check-rust] aviso: no encuentro {backend_dir}, salto el check.\n")
        return 0

    fmt = subprocess.run(
        ["cargo", "fmt", "--check"],
        cwd=backend_dir,
        capture_output=True,
        text=True,
        timeout=120,
    )
    if fmt.returncode != 0:
        sys.stderr.write(
            f"[postedit-check-rust] cargo fmt --check fallo:\n{fmt.stdout}\n{fmt.stderr}\n"
            "Corre `cargo fmt` en backend/ antes de cerrar.\n"
        )
        return 2

    clippy = subprocess.run(
        ["cargo", "clippy", "--quiet", "--all-targets", "--", "-D", "warnings"],
        cwd=backend_dir,
        capture_output=True,
        text=True,
        timeout=300,
    )
    if clippy.returncode != 0:
        sys.stderr.write(
            f"[postedit-check-rust] cargo clippy encontro warnings/errores:\n{clippy.stdout}\n{clippy.stderr}\n"
        )
        return 2

    return 0


if __name__ == "__main__":
    sys.exit(main())
