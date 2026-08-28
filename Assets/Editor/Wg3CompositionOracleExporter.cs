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
    /// Vuelca el ORÁCULO DE COMPOSICIÓN: el mundo entero que produce `Wg3Composer` para un puñado
    /// de semillas, pieza a pieza.
    ///
    /// POR QUÉ. El compositor se porta a Rust en F4, y ese port es la pieza grande que queda. Sin un
    /// oráculo, "el port es correcto" solo se puede comprobar mirando plantas y opinando; con él, es
    /// una aserción: **la misma semilla tiene que dar la misma lista de colocaciones**, y cualquier
    /// divergencia sale como un test rojo en la primera pieza que difiere, no como un mundo que se
    /// siente raro cincuenta piezas después.
    ///
    /// Es el mismo mecanismo que el oráculo de rotación, subido un nivel: allí se ataba una función
    /// pura entre dos idiomas, aquí se ata un algoritmo entero.
    ///
    /// RESOLUCIÓN DE CENTÍMETROS, y es una decisión. C# compone en `float` y el wire viaja en
    /// centímetros enteros; exigir igualdad bit a bit entre dos implementaciones en coma flotante
    /// sería exigir que Rust reprodujera también el orden exacto de las sumas, lo que ataría el port
    /// a la forma del original en vez de a su resultado. Al centímetro —que es la resolución que de
    /// verdad viaja y la que el ráster de 0,5 m puede distinguir— la comparación es estricta donde
    /// importa y tolerante donde no dice nada.
    /// </summary>
    public static class Wg3CompositionOracleExporter
    {
        private const string RelativeFolder = "../backend/tests/fixtures";
        private const string FileName = "wg3_composition_oracle.json";

        /// <summary>Semillas del oráculo. Fijas y variadas —positiva, pequeña, grande, NEGATIVA— y
        /// la negativa está a propósito: es donde un `%` que trunca hacia cero en vez de un módulo
        /// euclídeo produce otro mundo, y es un fallo que este proyecto ya ha pagado dos veces.</summary>
        private static readonly int[] Seeds = { 42, 7, 1337, -19, 900001 };

        [System.Serializable]
        private sealed class OraclePlacement
        {
            public int piece;
            public int rotation;
            public int origin_x_cm;
            public int origin_z_cm;
            public int origin_y_cm;
            public int depth;
        }

        [System.Serializable]
        private sealed class OracleWorld
        {
            public int seed;
            public int budget;
            public string signature;
            public int caps;
            public int forced_caps;
            public int rejected_by_overlap;
            public OraclePlacement[] placements;
        }

        [System.Serializable]
        private sealed class Oracle
        {
            public string digest;
            /// <summary>Los ajustes con los que se compuso. Sin ellos, el port no sabría contra qué
            /// configuración compara y podría estar reproduciendo otro mundo perfectamente.</summary>
            public float deliberate_cap_chance;
            public int cap_grace_count;
            public float scale_exact_bonus;
            public float scale_near_bonus;
            public float scale_far_bonus;
            public float repeat_parent_penalty;
            public float repeat_grandparent_penalty;
            public OracleWorld[] worlds;
        }

        [MenuItem("Backrooms/WorldGen3/Exportar oráculo de composición")]
        public static void Export()
        {
            List<Wg3Piece> catalog = Wg3Catalog.Build();
            List<string> issues = Wg3Validator.ValidateCatalog(catalog);
            if (issues.Count > 0)
            {
                Debug.LogError("[WG3] catálogo inválido, no se exporta el oráculo:\n" +
                               string.Join("\n", issues));
                return;
            }

            var settings = new Wg3ComposerSettings { budget = 30 };
            var worlds = new List<OracleWorld>(Seeds.Length);

            foreach (int seed in Seeds)
            {
                Wg3World world = Wg3Composer.Compose(seed, catalog, settings);
                var placements = new OraclePlacement[world.placements.Count];
                for (int i = 0; i < world.placements.Count; i++)
                {
                    Wg3Placement p = world.placements[i];
                    placements[i] = new OraclePlacement
                    {
                        piece = catalog.IndexOf(p.piece),
                        rotation = p.rotation,
                        // Al centímetro, redondeando al más cercano: es la resolución que viaja por
                        // el wire y la única a la que dos implementaciones en coma flotante pueden
                        // prometer coincidir.
                        origin_x_cm = Mathf.RoundToInt(p.originX * 100f),
                        origin_z_cm = Mathf.RoundToInt(p.originZ * 100f),
                        origin_y_cm = Mathf.RoundToInt(p.originY * 100f),
                        depth = p.depth
                    };
                }

                worlds.Add(new OracleWorld
                {
                    seed = seed,
                    budget = settings.budget,
                    signature = world.Signature(),
                    caps = world.caps.Count,
                    forced_caps = world.forcedCaps,
                    rejected_by_overlap = world.rejectedByOverlap,
                    placements = placements
                });
            }

            var oracle = new Oracle
            {
                digest = Wg3Manifest.FromCatalog(catalog).digest,
                deliberate_cap_chance = settings.deliberateCapChance,
                cap_grace_count = settings.capGraceCount,
                scale_exact_bonus = settings.scaleExactBonus,
                scale_near_bonus = settings.scaleNearBonus,
                scale_far_bonus = settings.scaleFarBonus,
                repeat_parent_penalty = settings.repeatParentPenalty,
                repeat_grandparent_penalty = settings.repeatGrandparentPenalty,
                worlds = worlds.ToArray()
            };

            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, RelativeFolder));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName);
            File.WriteAllText(path, JsonUtility.ToJson(oracle, true), new UTF8Encoding(false));

            int total = 0;
            foreach (OracleWorld w in oracle.worlds) total += w.placements.Length;
            Debug.Log($"[WG3] oráculo de composición en {path}: {oracle.worlds.Length} semillas, " +
                      $"{total} colocaciones, digest {oracle.digest.Substring(0, 12)}…");
        }
    }
}
#endif
