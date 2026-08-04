#!/usr/bin/env python3
"""Record edited C# paths; complete validation runs at Stop/checkpoint."""
import json
import os
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LEDGER_ROOT = os.environ.get("CLAUDE_HOOK_LEDGER_ROOT", REPO_ROOT)


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
    ledger = os.path.join(LEDGER_ROOT, ".claude", f".session-touched-{safe_id}")
    try:
        with open(ledger, "a", encoding="utf-8") as handle:
            for path in paths:
                handle.write(path + "\n")
    except OSError as error:
        sys.stderr.write(f"[postedit-check-csharp] aviso: no se pudo escribir el ledger: {error}\n")


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0
    if payload.get("tool_name") not in {"Edit", "Write"}:
        return 0

    paths = edited_paths(payload.get("tool_input", {}) or {})
    if paths:
        append_ledger(payload.get("session_id"), paths)
    return 0


if __name__ == "__main__":
    try:
        status = main()
    except Exception as error:
        sys.stderr.write(f"[postedit-check-csharp] aviso inesperado; se permite la edicion: {error}\n")
        status = 0
    sys.exit(2 if status == 2 else 0)
