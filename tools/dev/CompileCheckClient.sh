#!/usr/bin/env bash
# Headless compile-check of a Unity assembly using Unity's bundled Roslyn — no editor launch.
#
#   bash tools/dev/CompileCheckClient.sh                       # all four assemblies
#   bash tools/dev/CompileCheckClient.sh BackroomsSurvival     # just one
#
# Exit 0 / "errors: 0" is the client-side gate (the counterpart of `cargo test` for the backend).
# Run it from the repo root. Artifacts land in Temp/ (gitignored).
#
# Why this exists: the editor has to be CLOSED for -executeMethod, and opening it just to learn
# whether the code compiles costs minutes. This answers the same question in seconds.
#
# Notes that cost real debugging time, do not "simplify" them away:
#  - <ProjectReference> entries are NOT HintPaths; they must be resolved to
#    Library/ScriptAssemblies/<base>.dll or you get ~50 phantom CS0234/CS0246 for
#    UnityEngine.UI / EventSystems / TMPro.
#  - When checking an assembly that DEPENDS on one you just edited, pass the freshly built dll as
#    an extra -r: and set SKIP_REF to its name, so the stale ScriptAssemblies copy is dropped —
#    otherwise the same types exist twice and csc reports CS0433.
#  - git-bash mangles leading-slash args, hence MSYS_NO_PATHCONV=1; the @response path must then be
#    Windows-style or csc resolves it against the drive root (CS2011).
#  - csc output here is Spanish-localized, so we grep 'error CS', not ': error'.
#  - BackroomsSurvival's <Compile Include> list is a stale snapshot that omits just-added files;
#    glob Assets/Scripts instead.
set -uo pipefail

# Windows-style root (csc needs J:/… , not the msys /j/… form).
repo_root() { cd "$(dirname "${BASH_SOURCE[0]}")/../.." || return 1; pwd -W 2>/dev/null || pwd; }
PROJ="${PROJ:-$(repo_root)}"
UNITY="${UNITY_DATA:-C:/UnityInstall/6000.0.71f1/Editor/Data}"
DOTNET="$UNITY/NetCoreRuntime/dotnet.exe"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"

check_one() {
  local ASM="$1"; shift
  local OUT="$PROJ/Temp/cc_$ASM"
  local RSP="$OUT/build.rsp"
  local CSPROJ="$PROJ/$ASM.csproj"

  [ -f "$CSPROJ" ] || { echo "[$ASM] MISSING csproj: $CSPROJ"; return 2; }
  mkdir -p "$OUT"
  : > "$RSP"

  grep -oP '(?<=<HintPath>)[^<]+' "$CSPROJ" | sed 's#\\#/#g' | while read -r r; do
    printf -- '-r:"%s"\n' "$r" >> "$RSP"
  done

  grep -oP '(?<=<ProjectReference Include=")[^"]+' "$CSPROJ" | sed 's#\\#/#g' | while read -r p; do
    base="$(basename "$p" .csproj)"
    [ -n "${SKIP_REF:-}" ] && [ "$base" = "$SKIP_REF" ] && continue
    dll="$PROJ/Library/ScriptAssemblies/$base.dll"
    [ -f "$dll" ] && printf -- '-r:"%s"\n' "$dll" >> "$RSP"
  done

  for extra in "$@"; do printf -- '-r:"%s"\n' "$extra" >> "$RSP"; done

  local DEF
  DEF="$(grep -oP '(?<=<DefineConstants>)[^<]+' "$CSPROJ" | head -1)"
  [ -n "$DEF" ] && printf -- '-define:%s\n' "$DEF" >> "$RSP"

  case "$ASM" in
    BackroomsSurvival) find "$PROJ/Assets/Scripts" -name '*.cs' ;;
    *) grep -oP '(?<=<Compile Include=")[^"]+' "$CSPROJ" | sed 's#\\#/#g' \
         | while read -r s; do case "$s" in /*|?:*) echo "$s";; *) echo "$PROJ/$s";; esac; done ;;
  esac | sed 's#\\#/#g' | while read -r s; do printf '"%s"\n' "$s" >> "$RSP"; done

  printf -- '-target:library\n-nostdlib+\n-langversion:9.0\n-unsafe-\n-out:"%s/%s_check.dll"\n' "$OUT" "$ASM" >> "$RSP"

  cd "$PROJ" || return 2
  MSYS_NO_PATHCONV=1 "$DOTNET" "$CSC" -noconfig "@$(cygpath -m "$RSP" 2>/dev/null || echo "$RSP")" 2>&1 \
    | grep -E 'error CS' | sort -u > "$OUT/errors.txt"
  local N; N=$(wc -l < "$OUT/errors.txt")
  echo "[$ASM] errors: $N"
  [ "$N" -gt 0 ] && head -25 "$OUT/errors.txt"
  return "$N"
}

if [ "$#" -gt 0 ]; then
  check_one "$@"
  exit $?
fi

# Default sweep: the root assembly first, then the three that depend on it, each checked against
# the FRESH build rather than the stale ScriptAssemblies copy.
FAIL=0
check_one BackroomsSurvival || FAIL=1
FRESH="$PROJ/Temp/cc_BackroomsSurvival/BackroomsSurvival_check.dll"
for A in EditModeTests Assembly-CSharp Assembly-CSharp-Editor; do
  SKIP_REF=BackroomsSurvival check_one "$A" "$FRESH" || FAIL=1
done
exit "$FAIL"
