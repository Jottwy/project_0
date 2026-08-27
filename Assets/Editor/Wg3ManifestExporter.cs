#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Exporta el manifiesto de WG3 a <c>Assets/StreamingAssets/wg3_manifest.json</c>: la vía por la
    /// que la geometría autorada llegará al backend en F2.
    ///
    /// POR QUÉ UN FICHERO Y NO EL WIRE: el catálogo viaja en el build y no cambia por partida, así
    /// que mandarlo por conexión sería pagarlo una vez por cliente y por sesión.
    ///
    /// POR QUÉ NO AL REVÉS —que el cliente se lo mande al servidor al conectar—: eso haría al
    /// cliente autoridad de la geometría de COLISIÓN del servidor. Un cliente modificado borraría
    /// paredes, y un joiner no puede aportarlo. Es la misma alternativa que ADR-083 ya rechazó para
    /// las salas autoradas, y por el mismo motivo.
    ///
    /// F1 EXPORTA SIN CONSUMIDOR, a propósito. El fichero se puede escribir, leer, firmar y testear
    /// antes de que exista una sola línea de Rust que lo mire — así el formato se cierra contra un
    /// test en vez de contra un parser a medio escribir.
    /// </summary>
    public static class Wg3ManifestExporter
    {
        private const string StreamingFolder = "Assets/StreamingAssets";
        private const string ManifestPath = StreamingFolder + "/wg3_manifest.json";

        [MenuItem("Backrooms/WorldGen3/Exportar manifiesto")]
        public static void Export()
        {
            // La biblioteca autorada SUSTITUYE al catálogo de código, no se suma a él. Sumarlos
            // colocaría las piezas autoradas detrás de las de código, y como el índice entra en el
            // hash de colocación, cada pieza nueva reescribiría el mundo entero. Sustituir tiene el
            // mismo efecto UNA vez, el día que la biblioteca deja de estar vacía, y a partir de ahí
            // añadir al final es estable.
            //
            // Un mundo de dos piezas autoradas no llega a exportarse por accidente: el validador de
            // catálogo exige tapón por cada tipo de boca, y una biblioteca a medio llenar no lo
            // cumple.
            Wg3PieceLibrary library = Wg3PieceLibrary.Load();
            bool authored = library != null && library.pieces.Length > 0;

            if (authored)
            {
                List<string> libraryIssues = library.Validate();
                if (libraryIssues.Count > 0)
                {
                    Debug.LogError($"[WG3] biblioteca inválida, {libraryIssues.Count} motivos — NO " +
                                   "se exporta:\n" + string.Join("\n", libraryIssues), library);
                    return;
                }
            }

            List<Wg3Piece> catalog = authored ? library.BuildCatalog() : Wg3Catalog.Build();
            string source = authored
                ? $"biblioteca autorada ({library.pieces.Length} piezas)"
                : "catálogo de código";

            // REGLA R6 — una pieza que no valida no existe, así que tampoco se exporta. Exportar un
            // catálogo inválido es la receta del fallo silencioso: el backend coloca lo que puede,
            // descarta el resto sin decir nada, y el síntoma es un mundo al que le falta contenido.
            List<string> issues = Wg3Validator.ValidateCatalog(catalog);
            if (issues.Count > 0)
            {
                Debug.LogError($"[WG3] catálogo inválido, {issues.Count} motivos — NO se exporta:\n" +
                               string.Join("\n", issues));
                return;
            }

            Wg3Manifest manifest = Wg3Manifest.FromCatalog(catalog);
            string json = manifest.ToJson();

            if (!AssetDatabase.IsValidFolder(StreamingFolder))
                AssetDatabase.CreateFolder("Assets", "StreamingAssets");

            // UTF8 SIN BOM: un BOM al principio hace que `serde_json` se atragante con el primer
            // carácter y el error que da no menciona el BOM por ningún lado.
            File.WriteAllText(ManifestPath, json, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ManifestPath);

            int sockets = 0, boxes = 0;
            foreach (Wg3ManifestPiece p in manifest.pieces)
            {
                sockets += p.sockets.Length;
                boxes += p.collision.Length;
            }

            Debug.Log($"[WG3] manifiesto v{manifest.version} desde {source} exportado a {ManifestPath}: " +
                      $"{manifest.pieces.Length} piezas, {sockets} bocas, {boxes} cajas de " +
                      $"colisión, {json.Length} bytes. digest {manifest.digest.Substring(0, 12)}…");
        }
    }
}
#endif
