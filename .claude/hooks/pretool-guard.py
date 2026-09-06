#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PreToolUse unico: las tres guardas que corren ANTES de Bash o Write.

Sustituye a `guard-decisions-write.py` y `pretool-git-staging-guard.py`, que eran dos entradas
distintas en settings.json sobre el mismo evento. Estaban separadas por historia, no por diseno:
cada una volvia a parsear el mismo comando, cada una tenia su propio manejo de errores, y anadir
la tercera (el gate de commit) habria hecho tres procesos de Python por cada llamada a Bash.

Las guardas, en orden de coste:

1. DECISIONS.md es append-only (CLAUDE.md regla 11). Ni `Write` sobre el fichero ni un comando
   que lo trunque o lo reemplace. Un `Write` sobre este fichero ya causo un incidente de truncado.
2. Nada de staging masivo (`git add -A/--all/./:/ `) ni `git commit -a/--all`: el indice de git es
   COMPARTIDO entre sesiones concurrentes y un add masivo se lleva el trabajo de otra.
3. Gate de commit: cualquier `git commit` real dispara `tools/dev/validate-scope.ps1`, que valida
   lo que este commit toca segun `git diff --cached --name-only`. Rojo = exit 2 y el commit no
   llega a ejecutarse.

FAIL CLOSED. Si este hook revienta por una razon que no habiamos previsto, la salida es 2 y la
operacion NO se ejecuta. Antes salia 0 ("aviso inesperado; operacion permitida"), que convierte
cualquier bug del propio guardian en un permiso: justo lo contrario de lo que es un guardian.
"""
import json
import os
import re
import shlex
import subprocess
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# ---------------------------------------------------------------- 1. DECISIONS.md append-only

DECISIONS = "DECISIONS.md"

# Separadores de comando: newline, ;, &, | -- partimos el comando completo en segmentos y
# evaluamos cada uno por separado. Esto evita falsos positivos como un commit -m multilinea que
# MENCIONA "DECISIONS.md" en una linea de prosa y tiene un ">" en otra linea no relacionada (ej.
# un trailer "Co-Authored-By: ... <email>"): ninguna LINEA individual combina ambas cosas.
SEGMENT_SPLIT = re.compile(r"[\r\n;&|]+")

# Patrones Bash/PowerShell que truncan o reemplazan el archivo entero en vez de anclar un append.
# ">>" (append) y comandos de solo lectura (cat/grep/wc/tail/head/git diff) se dejan pasar.
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


def guard_decisions(tool_name, tool_input):
    if tool_name == "Write":
        file_path = str(tool_input.get("file_path", "")).replace("\\", "/")
        if DECISIONS in file_path:
            return ("BLOQUEADO: Write sobre docs/DECISIONS.md. Es append-only — usa Edit anclado "
                    "al final del archivo (CLAUDE.md regla 11). Verifica el numero de lineas "
                    "antes y despues del append.")
        return None

    if tool_name != "Bash":
        return None

    command = str(tool_input.get("command", ""))
    for segment in SEGMENT_SPLIT.split(command):
        if DECISIONS not in segment:
            continue
        for pattern in DESTRUCTIVE_BASH_PATTERNS:
            if re.search(pattern, segment):
                return ("BLOQUEADO: comando que parece sobrescribir/truncar docs/DECISIONS.md "
                        "(segmento: %r; patron: %r). Usa la tool Edit anclada al final, no Bash."
                        % (segment.strip(), pattern))
    return None


# ------------------------------------------------------------------ 2. staging masivo / commit -a

def tokens(segment):
    try:
        return shlex.split(segment, posix=True)
    except ValueError:
        return re.findall(r'"[^"]*"|\'[^\']*\'|\S+', segment)


def git_command(parts):
    """(subcomando, argumentos) del primer `git ...` del segmento, o (None, [])."""
    for index, part in enumerate(parts):
        if part.lower() not in {"git", "git.exe"}:
            continue
        cursor = index + 1
        while cursor < len(parts):
            option = parts[cursor]
            if option in {"-C", "--git-dir", "--work-tree", "--namespace"}:
                cursor += 2
            elif option.startswith("-"):
                cursor += 1
            else:
                return option.lower(), parts[cursor + 1:]
    return None, []


def guard_staging(subcommand, args, segment):
    mass_add = any(
        arg in {"-A", "--all", ".", ":/"}
        or (arg.startswith("-") and not arg.startswith("--") and "A" in arg[1:])
        for arg in args
    )
    commit_all = any(
        arg == "--all"
        or (arg.startswith("-") and not arg.startswith("--") and "a" in arg[1:])
        for arg in args
    )
    if subcommand == "add" and mass_add:
        reason = "git add masivo puede incluir cambios de otras sesiones"
    elif subcommand == "commit" and commit_all:
        reason = "git commit --all puede incluir cambios no revisados"
    else:
        return None
    return ("BLOQUEADO: %r; motivo: %s. Alternativa segura: git add <rutas revisadas> y commit "
            "sin -a/--all." % (segment.strip(), reason))


# ------------------------------------------------------------------------- 3. gate de commit

GATE = os.path.join(REPO_ROOT, "tools", "dev", "validate-scope.ps1")
# Banderas que hacen que `git commit` NO escriba un commit: no hay nada que validar.
DRY_FLAGS = {"--dry-run", "--short", "--porcelain", "--long", "--null", "-z"}


def powershell():
    """Ruta a powershell.exe. `powershell` a secas no siempre esta en el PATH del hook."""
    system_root = os.environ.get("SystemRoot", r"C:\Windows")
    candidate = os.path.join(system_root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    if os.path.exists(candidate):
        return candidate
    return "powershell"


def guard_commit(subcommand, args, segment):
    if subcommand != "commit":
        return None
    if any(arg in DRY_FLAGS for arg in args):
        return None
    if "--no-verify" in args or "-n" in args:
        return ("BLOQUEADO: `git commit --no-verify`. El gate de commit no es opcional; si algo "
                "suyo esta mal, arreglalo o dilo, no lo saltes.")
    if not os.path.exists(GATE):
        return ("BLOQUEADO: no encuentro %s. Sin gate no se commitea (fail closed)." % GATE)

    try:
        result = subprocess.run(
            [powershell(), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", GATE],
            cwd=REPO_ROOT, capture_output=True, text=True, timeout=1800,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        return ("BLOQUEADO: el gate de commit no se pudo ejecutar (%s). Fail closed: sin "
                "validacion no entra nada al repositorio." % error)
    if result.returncode != 0:
        return ("BLOQUEADO por el gate de commit (%r).\n%s\n%s"
                % (segment.strip(), result.stdout, result.stderr))
    sys.stderr.write("[gate de commit] VERDE\n%s\n" % result.stdout)
    return None


# ------------------------------------------------------------------------------------ main

def main():
    payload = json.load(sys.stdin)
    tool_name = payload.get("tool_name", "")
    tool_input = payload.get("tool_input", {}) or {}

    problem = guard_decisions(tool_name, tool_input)
    if problem:
        sys.stderr.write(problem + "\n")
        return 2

    if tool_name != "Bash":
        return 0

    command = str(tool_input.get("command", ""))
    for segment in SEGMENT_SPLIT.split(command):
        subcommand, args = git_command(tokens(segment))
        if subcommand is None:
            continue
        for check in (guard_staging, guard_commit):
            problem = check(subcommand, args, segment)
            if problem:
                sys.stderr.write(problem + "\n")
                return 2
    return 0


if __name__ == "__main__":
    try:
        status = main()
    except Exception as error:            # noqa: BLE001 - fail closed a proposito
        sys.stderr.write(
            "[pretool-guard] BLOQUEADO: el guardian fallo (%r). Fail closed: un bug del "
            "guardian no puede convertirse en un permiso. Arregla el hook o desactivalo "
            "explicitamente en .claude/settings.json.\n" % (error,))
        status = 2
    sys.exit(2 if status == 2 else 0)
