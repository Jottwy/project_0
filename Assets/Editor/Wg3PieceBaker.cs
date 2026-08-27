#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// El horno visto desde el menú: escribe en el asset lo que <see cref="Wg3PieceBake"/> tradujo.
    ///
    /// Aquí NO hay geometría, a propósito. La conversión vive en runtime porque
    /// <c>EditModeTests.asmdef</c> no puede referenciar <c>Assembly-CSharp-Editor</c>, y una
    /// conversión encerrada en la carpeta Editor sería una conversión sin un solo test. Lo que
    /// queda en este fichero es lo único que de verdad necesita al editor: seleccionar, guardar y
    /// contarlo.
    /// </summary>
    public static class Wg3PieceBaker
    {
        /// <summary>
        /// Hornea la pieza y la guarda. Devuelve los motivos por los que NO se pudo; vacío = hecha.
        ///
        /// El asset se escribe SOLO si la traducción salió entera (R6). Escribir lo que se pudo
        /// dejaría la huella nueva con las bocas viejas: una pieza que el compositor coloca
        /// convencido y que el jugador se encuentra sellada.
        /// </summary>
        public static List<string> Bake(Wg3PieceAsset asset)
        {
            if (asset == null) return new List<string> { "no hay pieza que hornear" };

            string who = string.IsNullOrWhiteSpace(asset.pieceId) ? asset.name : asset.pieceId;
            Wg3PieceBake.Result baked = Wg3PieceBake.From(asset.sourceDefinition, who);

            if (baked.windows > 0)
                Debug.Log($"[WG3] {who}: {baked.windows} agujero(s) por encima del suelo tratados " +
                          "como ventanas, no como bocas.", asset);

            if (!baked.Ok) return baked.issues;

            asset.sizeX = baked.sizeX;
            asset.sizeZ = baked.sizeZ;
            asset.heightMeters = baked.heightMeters;
            asset.sockets = baked.sockets;
            asset.volumes = baked.volumes;

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            Debug.Log($"[WG3] pieza «{who}» horneada: {baked.sizeX:0.00} × {baked.sizeZ:0.00} × " +
                      $"{baked.heightMeters:0.00} m, {baked.sockets.Length} bocas, " +
                      $"{baked.volumes.Length} cajas.", asset);
            return baked.issues;
        }

        [MenuItem("Backrooms/WorldGen3/Hornear pieza seleccionada")]
        public static void BakeSelected()
        {
            var asset = Selection.activeObject as Wg3PieceAsset;
            if (asset == null)
            {
                Debug.LogError("[WG3] selecciona un asset de pieza autorada en el proyecto.");
                return;
            }
            Report(Bake(asset), asset.name);
        }

        [MenuItem("Backrooms/WorldGen3/Hornear biblioteca")]
        public static void BakeLibrary()
        {
            Wg3PieceLibrary library = Wg3PieceLibrary.Load();
            if (library == null)
            {
                Debug.LogError($"[WG3] no hay biblioteca en Resources/{Wg3PieceLibrary.ResourcePath}.");
                return;
            }

            // Se hornea la biblioteca ENTERA aunque una pieza falle, y se cuenta todo junto: parar
            // en la primera obligaría a dar una vuelta al editor por cada fallo, y los fallos de un
            // lote de piezas recién dibujadas vienen en grupo.
            var all = new List<string>();
            foreach (Wg3PieceAsset asset in library.pieces)
                all.AddRange(Bake(asset));

            Report(all, $"biblioteca ({library.pieces.Length} piezas)");
        }

        private static void Report(List<string> issues, string what)
        {
            if (issues.Count == 0)
            {
                Debug.Log($"[WG3] {what}: horneado OK.");
                return;
            }
            Debug.LogError($"[WG3] {what}: {issues.Count} motivo(s), NO se horneó:\n" +
                           string.Join("\n", issues));
        }
    }
}
#endif
