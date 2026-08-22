#!/usr/bin/env bash
# PolishSweep.sh — barrido de higiene SIN lógica: todo lo que se puede arreglar en bucle sin tocar
# gameplay, worldgen, red ni persistencia. Imprime un informe por sección y sale con el número de
# secciones con hallazgos (0 = limpio). Es la contraparte de CompileCheckClient.sh para "pulir":
# primero se corre, se arregla lo que liste, se vuelve a correr hasta que salga 0.
#
#   bash tools/dev/PolishSweep.sh            # todo (Rust + C# + repo), ~3-4 min en frío
#   bash tools/dev/PolishSweep.sh --quick    # solo repo (sin cargo ni csc), segundos
#   bash tools/dev/PolishSweep.sh --no-cs    # Rust + repo
#   bash tools/dev/PolishSweep.sh --no-rust  # C# + repo
#
# Secciones:
#   rust-fmt       cargo fmt --check
#   rust-clippy    cargo clippy --all-targets (warnings + errors)
#   rust-test      cargo test (BACKROOMS_VERBOSE_LOG=1, el test de lanzamiento lo exige)
#   cs-compile     CompileCheckClient.sh (4 asambleas, errors: 0)
#   cs-warnings    warnings csc salvo CS0649/CS0169 (Unity ya los silencia para [SerializeField])
#   meta           .meta huérfanos o ausentes entre los ficheros TRACKEADOS de Assets/
#   doc-links      enlaces relativos rotos en docs/**/*.md
#   index          ficheros docs/*.md sin entrada en docs/INDEX.md
#   trailing-ws    espacios colgantes en fuentes propias (backend/src, Assets/Scripts, _Migration,
#                  Editor, Tests)
#   bare-ignore    `#[ignore]` sin motivo en backend/src (las sondas deben decir que lo son)
#
# Lo que NO hace, a propósito: tocar nada. Solo lista. Cada arreglo es un commit propio (regla #5).

set -u
PROJ="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$PROJ" || exit 99

RUN_RUST=1; RUN_CS=1
for a in "$@"; do
  case "$a" in
    --quick) RUN_RUST=0; RUN_CS=0 ;;
    --no-cs) RUN_CS=0 ;;
    --no-rust) RUN_RUST=0 ;;
    *) echo "arg desconocido: $a" >&2; exit 99 ;;
  esac
done

FAIL=0
section() { printf '\n== %s\n' "$1"; }
flag()    { FAIL=$((FAIL+1)); }

OWN_CS=(Assets/Scripts Assets/_Migration Assets/Editor Assets/Tests)

# ───────────────────────────── Rust ─────────────────────────────
if [ "$RUN_RUST" = 1 ]; then
  section rust-fmt
  if ! (cd backend && cargo fmt --check >/dev/null 2>&1); then
    (cd backend && cargo fmt --check 2>&1 | grep '^Diff in' | sed 's/ at line.*//' | sort -u | head -20)
    flag
  else echo ok; fi

  section rust-clippy
  # clippy cachea: si no hay cambios no reimprime nada, así que se toca main.rs para forzar.
  touch backend/src/main.rs
  CL="$(cd backend && cargo clippy --all-targets --message-format short 2>&1 | grep -E ': (warning|error)' | sort -u)"
  if [ -n "$CL" ]; then echo "$CL" | head -40; echo "... $(echo "$CL" | wc -l) líneas"; flag; else echo ok; fi

  section rust-test
  TL="$(cd backend && BACKROOMS_VERBOSE_LOG=1 cargo test 2>&1 | grep -E '^test result|FAILED|^error' | tail -5)"
  echo "$TL"
  echo "$TL" | grep -qE 'FAILED|^error' && flag
fi

# ───────────────────────────── C# ─────────────────────────────
if [ "$RUN_CS" = 1 ]; then
  section cs-compile
  CC="$(bash tools/dev/CompileCheckClient.sh 2>&1)"
  echo "$CC" | grep -E '^\['
  echo "$CC" | grep -qE 'errors: [1-9]' && flag

  section cs-warnings
  DOTNET="C:/UnityInstall/6000.0.71f1/Editor/Data/NetCoreRuntime/dotnet.exe"
  CSC="C:/UnityInstall/6000.0.71f1/Editor/Data/DotNetSdkRoslyn/csc.dll"
  W=""
  for a in BackroomsSurvival EditModeTests Assembly-CSharp Assembly-CSharp-Editor; do
    RSP="$PROJ/Temp/cc_$a/build.rsp"
    [ -f "$RSP" ] || continue
    W+="$(MSYS_NO_PATHCONV=1 "$DOTNET" "$CSC" -noconfig "@$(cygpath -m "$RSP" 2>/dev/null || echo "$RSP")" 2>&1 \
         | grep -E 'warning CS' | grep -vE 'CS0649|CS0169' | sort -u | sed "s#^$PROJ/##; s#^[A-Za-z]:[\\\\/].*Assets#Assets#")"$'\n'
  done
  W="$(printf '%s' "$W" | sed '/^$/d')"
  if [ -n "$W" ]; then echo "$W"; flag; else echo ok; fi
fi

# ───────────────────────────── Repo ─────────────────────────────
section meta
# Solo lo que está EN DISCO: un borrado a medias de otra sesión (fichero y meta fuera del árbol
# pero aún trackeados) no es un huérfano, es un commit pendiente ajeno.
M="$( { git ls-files 'Assets/**/*.meta' 'Assets/*.meta' | while read -r m; do [ -e "$m" ] && [ ! -e "${m%.meta}" ] && echo "huérfano: $m"; done;
       git ls-files Assets | grep -v '\.meta$' | while read -r f; do [ -e "$f" ] && [ ! -e "$f.meta" ] && echo "sin meta: $f"; done; } | head -30)"
if [ -n "$M" ]; then echo "$M"; flag; else echo ok; fi

section doc-links
DL="$(for f in $(git ls-files 'docs/*.md' 'docs/**/*.md'); do
        grep -oE '\]\(([^)#]+)' "$f" | sed 's/](//' | grep -vE '^https?://|^mailto:' | while read -r l; do
          t="$(dirname "$f")/$l"; [ -e "$t" ] || [ -e "$l" ] || echo "$f -> $l"; done; done | sort -u | head -30)"
if [ -n "$DL" ]; then echo "$DL"; flag; else echo ok; fi

section index
IX="$(cd docs && for f in *.md; do [ "$f" = INDEX.md ] && continue; grep -q "$f" INDEX.md || echo "no indexado: docs/$f"; done)"
if [ -n "$IX" ]; then echo "$IX"; flag; else echo ok; fi

section trailing-ws
TW="$(grep -rln '[[:space:]]$' backend/src --include=*.rs "${OWN_CS[@]}" --include=*.cs 2>/dev/null | head -30)"
if [ -n "$TW" ]; then echo "$TW"; flag; else echo ok; fi

section bare-ignore
BI="$(grep -rn $'#\\[ignore\\]\r\\?$' backend/src | head -30)"
if [ -n "$BI" ]; then echo "$BI"; flag; else echo ok; fi

printf '\n== resumen: %d secciones con hallazgos\n' "$FAIL"
exit "$FAIL"
