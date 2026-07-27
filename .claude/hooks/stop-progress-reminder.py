#!/usr/bin/env python3
"""Stop hook: avisa (no bloquea) si docs/STATE.md -- el progress file del
proyecto -- no se toco hoy. Guardia stop_hook_active para no crear un bucle
de Stop hooks. Informativo solo: nunca decision=block (bloquear aqui
gastaria otro turno completo, justo lo que este piloto quiere evitar).
"""
import datetime
import json
import os
import sys

STATE_FILE = "docs/STATE.md"


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        payload = {}

    if payload.get("stop_hook_active"):
        return 0  # ya estamos en una re-entrada de Stop; no repetir el aviso

    repo_root = payload.get("cwd") or os.getcwd()
    state_path = os.path.join(repo_root, STATE_FILE)

    if not os.path.isfile(state_path):
        print(f"[stop-progress-reminder] aviso: no encuentro {STATE_FILE}.")
        return 0

    mtime = datetime.date.fromtimestamp(os.path.getmtime(state_path))
    today = datetime.date.today()

    if mtime != today:
        print(
            f"[stop-progress-reminder] {STATE_FILE} no se ha tocado hoy "
            f"({today.isoformat()}; ultima modificacion {mtime.isoformat()}). "
            "Si cerraste un incremento del piloto ADR-024, actualiza "
            "'Ultima sesion' / 'Proximo paso' antes de terminar la sesion."
        )

    return 0


if __name__ == "__main__":
    sys.exit(main())
