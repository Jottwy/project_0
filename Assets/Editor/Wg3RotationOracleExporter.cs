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
    /// Vuelca el ORÁCULO DE ROTACIÓN: las cajas de colisión de cada pieza, en los cuatro giros y
    /// desde un origen fijo, tal y como las calcula el lado de Unity.
    ///
    /// POR QUÉ EXISTE. La rotación está escrita DOS VECES a propósito: en `Wg3Manifest.PlacedCollision`
    /// (C#) y en `wg3::placement::placed_collision` (Rust). Tiene que estarlo — el cliente dibuja sin
    /// preguntar y el servidor colisiona sin dibujar—, pero dos implementaciones de la misma regla es
    /// exactamente la deuda que este proyecto ya paga cara en otros sitios, y aquí el modo de fallo es
    /// silencioso: nada revienta, simplemente la pared de una pieza girada tapa la puerta de su
    /// vecina, y el síntoma aparece a cien metros de la causa.
    ///
    /// Un test dentro de cada idioma no lo caza: los dos pueden estar internamente consistentes y
    /// diferir entre ellos. Lo único que lo caza es que uno de los dos escriba los números y el otro
    /// los verifique, que es lo que hace este fichero.
    ///
    /// El origen sale en CENTÍMETROS ENTEROS y los dos lados derivan los metros con la misma
    /// operación (`cm * 0.01f`). Escribir `13.37f` a un lado y `1337 * 0.01` al otro daría dos floats
    /// distintos en los últimos bits, y la tolerancia del test estaría tapando una diferencia real de
    /// convención en vez de un redondeo.
    /// </summary>
    public static class Wg3RotationOracleExporter
    {
        /// <summary>Fuera de `Assets/` a propósito: es una fixture de test de Rust, no un asset del
        /// juego. Meterla en `StreamingAssets` la metería en el build del cliente.</summary>
        private const string RelativeFolder = "../backend/tests/fixtures";
        private const string FileName = "wg3_rotation_oracle.json";

        /// <summary>Mismo origen que el helper `at()` de los tests de Rust. Deliberadamente NO
        /// alineado a la rejilla de 0,5 m del ráster: si lo estuviera, un fallo de medio píxel en la
        /// conversión pasaría desapercibido.</summary>
        private const int OriginXCm = 1337;
        private const int OriginZCm = -4271;

        [System.Serializable]
        private sealed class OracleBox
        {
            public float cx, cy, cz;
            public float sx, sy, sz;
            public float yaw;
            public int kind;
        }

        [System.Serializable]
        private sealed class OracleCase
        {
            public int piece;
            public int rotation;
            public OracleBox[] boxes;
        }

        [System.Serializable]
        private sealed class Oracle
        {
            public int origin_x_cm;
            public int origin_z_cm;
            public string digest;
            public OracleCase[] cases;
        }

        [MenuItem("Backrooms/WorldGen3/Exportar oráculo de rotación")]
        public static void Export()
        {
            List<Wg3Piece> catalog = Wg3Catalog.Build();
            List<string> issues = Wg3Validator.ValidateCatalog(catalog);
            if (issues.Count > 0)
            {
                Debug.LogError($"[WG3] catálogo inválido, no se exporta el oráculo:\n" +
                               string.Join("\n", issues));
                return;
            }

            Wg3Manifest manifest = Wg3Manifest.FromCatalog(catalog);
            var cases = new List<OracleCase>(catalog.Count * 4);

            for (int i = 0; i < catalog.Count; i++)
                for (int r = 0; r < 4; r++)
                {
                    var placement = new Wg3Placement
                    {
                        piece = catalog[i],
                        rotation = r,
                        originX = OriginXCm * 0.01f,
                        originZ = OriginZCm * 0.01f,
                        socketState = new byte[catalog[i].sockets.Length]
                    };

                    List<Wg3Volume> volumes = Wg3Manifest.PlacedCollision(manifest.pieces[i], placement);
                    var boxes = new OracleBox[volumes.Count];
                    for (int v = 0; v < volumes.Count; v++)
                        boxes[v] = new OracleBox
                        {
                            cx = volumes[v].center.x,
                            cy = volumes[v].center.y,
                            cz = volumes[v].center.z,
                            sx = volumes[v].size.x,
                            sy = volumes[v].size.y,
                            sz = volumes[v].size.z,
                            yaw = volumes[v].yawDegrees,
                            kind = (int)volumes[v].kind
                        };

                    cases.Add(new OracleCase { piece = i, rotation = r, boxes = boxes });
                }

            var oracle = new Oracle
            {
                origin_x_cm = OriginXCm,
                origin_z_cm = OriginZCm,
                // El digest ata el oráculo AL CATÁLOGO que lo produjo. Sin él, cambiar una pieza y
                // olvidar reexportar deja un test verde comparando contra un mundo que ya no existe
                // — verde por comparar dos cosas viejas, que es la peor forma de estar verde.
                digest = manifest.digest,
                cases = cases.ToArray()
            };

            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, RelativeFolder));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName);
            File.WriteAllText(path, JsonUtility.ToJson(oracle, true), new UTF8Encoding(false));

            int boxCount = 0;
            foreach (OracleCase c in cases) boxCount += c.boxes.Length;
            Debug.Log($"[WG3] oráculo de rotación en {path}: {cases.Count} casos, {boxCount} cajas, " +
                      $"digest {oracle.digest.Substring(0, 12)}…");
        }
    }
}
#endif
