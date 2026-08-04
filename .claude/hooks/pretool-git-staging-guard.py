#!/usr/bin/env python3
"""Block mass staging and commit-all commands before Bash runs them."""
import json
import re
import shlex
import sys


def tokens(segment):
    try:
        return shlex.split(segment, posix=True)
    except ValueError:
        return re.findall(r'"[^"]*"|\'[^\']*\'|\S+', segment)


def git_command(parts):
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
                return option.lower(), parts[cursor + 1 :]
    return None, []


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0
    if payload.get("tool_name") != "Bash":
        return 0

    command = str((payload.get("tool_input") or {}).get("command", ""))
    for segment in re.split(r"[\r\n;&|]+", command):
        subcommand, args = git_command(tokens(segment))
        if subcommand == "add" and any(arg in {"-A", "--all", ".", ":/"} for arg in args):
            reason = "git add masivo puede incluir cambios de otras sesiones"
        elif subcommand == "commit" and any(arg in {"-a", "--all"} for arg in args):
            reason = "git commit --all puede incluir cambios no revisados"
        else:
            continue
        sys.stderr.write(
            f"BLOQUEADO: {segment.strip()!r}; motivo: {reason}. "
            "Alternativa segura: git add <rutas revisadas> y commit sin -a/--all.\n"
        )
        return 2
    return 0


if __name__ == "__main__":
    try:
        status = main()
    except Exception as error:
        sys.stderr.write(f"[pretool-git-staging-guard] aviso inesperado; comando permitido: {error}\n")
        status = 0
    sys.exit(2 if status == 2 else 0)
