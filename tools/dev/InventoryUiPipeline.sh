#!/usr/bin/env bash
# Tubería de la UI del inventario, de una pasada y sin editor abierto:
#   1. compile check con Roslyn (los cuatro ensamblados; el editor con los ficheros nuevos a mano,
#      porque el .csproj es una FOTO y no los conoce)
#   2. Unity headless: tema + variante + captura (BackroomsUiPipeline.RunAll, SIN -nographics)
#   3. Unity headless: tests EditMode de la UI (BackroomsUiThemeTests, BackroomsInventoryVariantTests)
#   4. limpieza de lo que deja el headless en un worktree (csproj copiados, junction, Materials/ del
#      pack de supermercado bajo StaticMeshes, ProjectSettings que cambian solos)
# Uso, desde la raíz del worktree:   bash tools/dev/InventoryUiPipeline.sh
# Sale con 1 si algo se pone rojo. La captura queda en Builds/Captures/inventario_tab.png.
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ROOTW="$(cd "$ROOT" && pwd -W 2>/dev/null || echo "$ROOT")"
MAIN="J:/Unity/BackroomsSurvivalMMO"
UNITY="C:/UnityInstall/6000.0.71f1/Editor/Unity.exe"
DOTNET="C:/UnityInstall/6000.0.71f1/Editor/Data/NetCoreRuntime/dotnet.exe"
CSC="C:/UnityInstall/6000.0.71f1/Editor/Data/DotNetSdkRoslyn/csc.dll"
PS="/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"   # powershell.exe no está en el PATH de git-bash
LOGDIR="${INVENTORY_UI_LOGDIR:-$ROOT/Builds/uipipeline}"   # NO bajo Temp/: el headless lo borra al arrancar
FILTER="BackroomsSurvival.Tests.BackroomsUiThemeTests|BackroomsSurvival.Tests.BackroomsInventoryVariantTests"
cd "$ROOT" || exit 1

# El lockfile es la verdad para el editor ABIERTO; pero una pasada headless matada a medias deja
# uno huérfano en Temp/ y ningún proceso. Sin Unity.exe vivo, el lock es basura y se quita.
if [ -f "$MAIN/Temp/UnityLockfile" ] || [ -f "$ROOT/Temp/UnityLockfile" ]; then
  if "$PS" -NoProfile -Command "if (Get-Process -Name Unity -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"; then
    echo "[pipeline] el editor está ABIERTO (UnityLockfile + proceso): headless imposible. Usa el menú Backrooms/UI/Run Pipeline."
    exit 1
  fi
  echo "[pipeline] lockfile huérfano sin proceso Unity: lo retiro"
  rm -f "$MAIN/Temp/UnityLockfile" "$ROOT/Temp/UnityLockfile"
fi

harness_up() {
  [ -e "$ROOT/Library" ] || "$PS" -NoProfile -Command "New-Item -ItemType Junction -Path '$ROOTW\\Library' -Target '$MAIN\\Library' | Out-Null"
  cp "$MAIN"/*.csproj "$ROOT"/ 2>/dev/null
  [ -f "$MAIN/Assets/Editor/_ClaudeSessionRunner.cs" ] && cp "$MAIN/Assets/Editor/_ClaudeSessionRunner.cs" "$ROOT/Assets/Editor/"
}
harness_down() {
  "$PS" -NoProfile -Command "if (Test-Path '$ROOTW\\Library') { (Get-Item '$ROOTW\\Library').Delete() }" 2>/dev/null
  rm -f "$ROOT"/*.csproj "$ROOT/Assets/Editor/_ClaudeSessionRunner.cs" "$ROOT/Assets/Editor/_ClaudeSessionRunner.cs.meta"
  find "$ROOT/Assets/GroceryStorePropsCollection/StaticMeshes" -type d -name Materials -prune -exec rm -rf {} + 2>/dev/null
  find "$ROOT/Assets/GroceryStorePropsCollection/StaticMeshes" -name "Materials.meta" -delete 2>/dev/null
  git -C "$ROOT" checkout -q -- ProjectSettings/Packages/com.unity.testtools.codecoverage/Settings.json ProjectSettings/ShaderGraphSettings.asset 2>/dev/null
  git -C "$ROOT" checkout -q -- Assets/Art/Fonts/SDF 2>/dev/null   # el atlas dinámico se rellena con cada captura: ruido
  # La captura dibuja el muñeco del vendor: URP rellena _BaseMap en tres .mat suyos y marca la RenderTexture. No es nuestro.
  git -C "$ROOT" checkout -q -- Assets/PolymindGames/STP/Art/Models/Characters/MaleSurvivor/Materials Assets/PolymindGames/STP/Art/Textures/STP_CharacterPreview.renderTexture 2>/dev/null
}
trap harness_down EXIT
mkdir -p "$LOGDIR"

echo "[pipeline] 1/4 compile check"
harness_up
bash tools/dev/CompileCheckClient.sh > "$LOGDIR/compile.log" 2>&1
grep -E "^\[" "$LOGDIR/compile.log"
# El .csproj es una foto: un .cs recién creado no está en ella y "errors: 0" sería vacuo.
# Se recompilan el editor y los tests con TODOS los ficheros de sus carpetas.
recheck() {  # $1 ensamblado, $2 carpeta de fuentes
  local RSP="$ROOT/Temp/cc_$1/build.rsp"
  [ -f "$RSP" ] || { echo "[pipeline] ROJO: sin $RSP"; exit 1; }
  grep -v "BackroomsOfficeHarvestAssetsCreator.cs\|_ClaudeCaptureRunner.cs\|_ClaudeWatcherPreview.cs" "$RSP" > "$RSP.2"
  for f in "$ROOT"/$2/*.cs; do grep -qF "$(basename "$f")" "$RSP.2" || echo "\"$ROOTW/$2/$(basename "$f")\"" >> "$RSP.2"; done
  MSYS_NO_PATHCONV=1 "$DOTNET" "$CSC" -noconfig @"$ROOTW/Temp/cc_$1/build.rsp.2" > "$LOGDIR/compile-$1.log" 2>&1
  if grep -q "error CS" "$LOGDIR/compile-$1.log"; then grep "error CS" "$LOGDIR/compile-$1.log" | head; echo "[pipeline] ROJO: $1"; exit 1; fi
  echo "[$1+nuevos] errors: 0"
}
recheck Assembly-CSharp-Editor Assets/Editor
recheck EditModeTests Assets/Tests/EditMode
for a in BackroomsSurvival Assembly-CSharp; do
  grep -q "^\[$a\] errors: 0" "$LOGDIR/compile.log" || { echo "[pipeline] ROJO: $a no compila (ver $LOGDIR/compile.log)"; exit 1; }
done

echo "[pipeline] 2/4 unity: tema + variante + captura"
"$UNITY" -batchmode -quit -projectPath "$ROOTW" -executeMethod BackroomsSurvival.EditorTools.BackroomsUiPipeline.RunAll -logFile "$LOGDIR/unity-build.log"
grep -n "\[UiPipeline\]\|\[InventoryUiBuilder\]\|\[InventarioShot\]\|\[BackroomsUiThemeBuilder\]" "$LOGDIR/unity-build.log" | sed 's/^[0-9]*://'
if ! grep -q "\[UiPipeline\] OK" "$LOGDIR/unity-build.log"; then
  grep -n "error CS\|Exception\|Error" "$LOGDIR/unity-build.log" | grep -v "canalizaci" | head -12
  echo "[pipeline] ROJO: la pasada de Unity no llegó a OK (ver $LOGDIR/unity-build.log)"; exit 1
fi

echo "[pipeline] 3/4 unity: tests"
"$UNITY" -runTests -batchmode -nographics -projectPath "$ROOTW" -testPlatform EditMode -testFilter "$FILTER" -testResults "$LOGDIR/tests.xml" -logFile "$LOGDIR/unity-tests.log"
SUMMARY=$(grep -o 'total="[0-9]*" passed="[0-9]*" failed="[0-9]*"' "$LOGDIR/tests.xml" | head -1)
echo "[pipeline] tests: $SUMMARY"
grep -B1 -A8 'result="Failed"' "$LOGDIR/tests.xml" | grep -E "name=|<message" | head -20
echo "$SUMMARY" | grep -q 'failed="0"' || { echo "[pipeline] ROJO: tests"; exit 1; }

echo "[pipeline] 4/4 limpieza"
harness_down; trap - EXIT
echo "[pipeline] VERDE. Captura: $ROOT/Builds/Captures/inventario_tab.png"
