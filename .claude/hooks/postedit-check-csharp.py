#!/usr/bin/env python3
"""PostToolUse check, lado C# del piloto ADR-024. Corre dotnet format en modo
verificacion (no reescribe) solo sobre el archivo tocado, solo si cae dentro
de Assets/_Migration/STPIntegration/RemoteAvatar/. Fuera de ese alcance,
no-op silencioso. Cero llamadas a modelo.
"""
import json
import os
import subprocess
import sys

SCOPE = "Assets/_Migration/STPIntegration/RemoteAvatar/"
CSPROJ = "Assembly-CSharp.csproj"


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    if payload.get("tool_name") != "Edit":
        return 0

    tool_input = payload.get("tool_input", {}) or {}
    file_path = str(tool_input.get("file_path", "")).replace("\\", "/")

    if SCOPE not in file_path or not file_path.endswith(".cs"):
        return 0  # fuera del piloto, no-op

    repo_root = payload.get("cwd") or os.getcwd()
    if not os.path.isfile(os.path.join(repo_root, CSPROJ)):
        sys.stderr.write(f"[postedit-check-csharp] aviso: no encuentro {CSPROJ} en {repo_root}, salto el check.\n")
        return 0

    rel_path = file_path
    result = subprocess.run(
        ["dotnet", "format", CSPROJ, "--verify-no-changes", "--include", rel_path],
        cwd=repo_root,
        capture_output=True,
        text=True,
        timeout=180,
    )

    if result.returncode != 0:
        sys.stderr.write(
            f"[postedit-check-csharp] dotnet format detecto problemas en {rel_path}:\n"
            f"{result.stdout}\n{result.stderr}\n"
            "Corrige el formato (o corre `dotnet format Assembly-CSharp.csproj --include <archivo>` sin --verify-no-changes) antes de cerrar.\n"
        )
        return 2

    return 0


if __name__ == "__main__":
    sys.exit(main())
