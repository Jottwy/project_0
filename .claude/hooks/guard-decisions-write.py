#!/usr/bin/env python3
"""PreToolUse guard: DECISIONS.md solo se amplia con Edit anclado, nunca Write
ni una reescritura completa via Bash. Determinista, sin llamadas a modelo.
Ver CLAUDE.md regla #11 y memoria del proyecto (decisions-md-append-hazard).
"""
import json
import re
import sys

TARGET = "DECISIONS.md"

# Separadores de comando: newline, ;, &, | -- partimos el comando completo en
# segmentos y evaluamos cada uno por separado. Esto evita falsos positivos
# como un commit -m multilinea que MENCIONA "DECISIONS.md" en una linea de
# prosa y tiene un ">" en otra linea no relacionada (ej. un trailer
# "Co-Authored-By: ... <email>"): ninguna LINEA individual combina ambas
# cosas, asi que ningun segmento dispara.
SEGMENT_SPLIT = re.compile(r"[\r\n;&|]+")

# Patrones Bash/PowerShell que truncan o reemplazan el archivo entero en vez
# de anclar un append. ">>" (append) y comandos de solo lectura (cat/grep/
# wc/tail/head/git diff) se dejan pasar.
DESTRUCTIVE_BASH_PATTERNS = [
    r"(?<!>)>(?!>)",       # redirect simple ">" (trunca), pero no ">>"
    r"\bcp\b",
    r"\bmv\b",
    r"\brm\b",
    r"Set-Content",
    r"Out-File(?!.*-Append)",
    r"Copy-Item",
    r"Move-Item",
    r"Remove-Item",
    r"truncate\b",
]


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    tool_name = payload.get("tool_name", "")
    tool_input = payload.get("tool_input", {}) or {}

    if tool_name == "Write":
        file_path = str(tool_input.get("file_path", ""))
        if TARGET in file_path.replace("\\", "/"):
            sys.stderr.write(
                "BLOQUEADO: Write sobre docs/DECISIONS.md. Es append-only — "
                "usa Edit anclado al final del archivo (ver CLAUDE.md regla #11). "
                "Verifica `wc -l docs/DECISIONS.md` antes y despues del append.\n"
            )
            return 2
        return 0

    if tool_name == "Bash":
        command = str(tool_input.get("command", ""))
        for segment in SEGMENT_SPLIT.split(command):
            if TARGET not in segment:
                continue
            for pattern in DESTRUCTIVE_BASH_PATTERNS:
                if re.search(pattern, segment):
                    sys.stderr.write(
                        "BLOQUEADO: comando Bash/PowerShell que parece "
                        "sobrescribir/truncar docs/DECISIONS.md "
                        f"(segmento: {segment.strip()!r}; patron detectado: {pattern!r}). "
                        "Usa la tool Edit anclada al final del archivo, no Bash.\n"
                    )
                    return 2
        return 0

    return 0


if __name__ == "__main__":
    sys.exit(main())
