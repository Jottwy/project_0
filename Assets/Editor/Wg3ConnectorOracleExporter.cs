#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-098 T2 — vuelca el ORÁCULO DE CONECTORES: un puñado de tramos y las cajas SÓLIDAS que
    /// salen de ellas, caja a caja y en orden.
    ///
    /// POR QUÉ. La expansión de un tramo está escrita dos veces —aquí para dibujar, en
    /// `wg3::segment` para rasterizar la colisión— y dos implementaciones internamente consistentes
    /// pueden diferir sin que nada reviente: el síntoma sería una pared que se ve y no frena, o al
    /// revés. Es la misma partida doble que ya tienen la rotación y el compositor, y se paga igual.
    ///
    /// **Este lado es el AUTOR**, y no al revés, porque es el que fija el ASPECTO: grosor de losa,
    /// grosor de pared y rodapié salen del catálogo de código, y el conector tiene que parecerse a
    /// lo que se le engancha o la junta se lee (R31).
    ///
    /// SOLO LO SÓLIDO. La decoración no cruza la frontera de autoridad (R25): el rodapié de un
    /// conector es asunto del cliente, así que no entra en el fixture y Rust no lo emite.
    ///
    /// AL CENTÍMETRO, como los otros dos oráculos: es la resolución que de verdad viaja y la única a
    /// la que dos implementaciones en coma flotante pueden prometer coincidir sin atar una a la
    /// forma de la otra.
    /// </summary>
    public static class Wg3ConnectorOracleExporter
    {
        private const string RelativeFolder = "../backend/tests/fixtures";
        private const string FileName = "wg3_connector_oracle.json";

        [Serializable]
        private sealed class OracleOpening
        {
            public int side;
            public int offset_cm;
            public int width_cm;
        }

        [Serializable]
        private sealed class OracleBox
        {
            public int cx_cm;
            public int cy_cm;
            public int cz_cm;
            public int sx_cm;
            public int sy_cm;
            public int sz_cm;
            public int kind;
        }

        [Serializable]
        private sealed class OracleSegment
        {
            public string name;
            public int x_cm;
            public int z_cm;
            public int size_x_cm;
            public int size_z_cm;
            public int floor_y_cm;
            public int height_cm;
            public int style;
            public OracleOpening[] openings;
            public OracleBox[] boxes;
        }

        [Serializable]
        private sealed class ConnectorOracle
        {
            /// <summary>En MILÍMETROS y enteros: son constantes de las dos partes y compararlas es
            /// lo que caza que alguien cambie el grosor de pared en un solo idioma.</summary>
            public int slab_thickness_mm;
            public int wall_thickness_mm;
            public OracleSegment[] segments;
        }

        /// <summary>
        /// Los tramos del oráculo. Elegidas para que cada una rompa algo distinto si la expansión se
        /// desvía: boca a todo el ancho (que no debe dejar pared), boca estrecha (que debe dejar las
        /// dos jambas), transición de ancho, quiebro, tres bocas, cota distinta de cero y un tramo
        /// del tamaño del tope.
        /// </summary>
        private static List<KeyValuePair<string, Wg3Segment>> SampleSegments()
        {
            var samples = new List<KeyValuePair<string, Wg3Segment>>();

            samples.Add(new KeyValuePair<string, Wg3Segment>("recto_2m4", new Wg3Segment
            {
                xCm = 0, zCm = 0, sizeXCm = 1000, sizeZCm = 240,
                floorYCm = 0, heightCm = 320,
                openings = new[]
                {
                    new Wg3SegmentOpening(3, 120, 240),
                    new Wg3SegmentOpening(1, 120, 240)
                }
            }));

            samples.Add(new KeyValuePair<string, Wg3Segment>("quiebro", new Wg3Segment
            {
                xCm = -350, zCm = 725, sizeXCm = 240, sizeZCm = 240,
                floorYCm = 0, heightCm = 320,
                openings = new[]
                {
                    new Wg3SegmentOpening(3, 120, 240),
                    new Wg3SegmentOpening(0, 120, 240)
                }
            }));

            samples.Add(new KeyValuePair<string, Wg3Segment>("transicion_2m4_a_5m", new Wg3Segment
            {
                xCm = 1200, zCm = -80, sizeXCm = 600, sizeZCm = 500,
                floorYCm = 0, heightCm = 320,
                openings = new[]
                {
                    new Wg3SegmentOpening(3, 250, 240),
                    new Wg3SegmentOpening(1, 250, 500)
                }
            }));

            samples.Add(new KeyValuePair<string, Wg3Segment>("tres_bocas", new Wg3Segment
            {
                xCm = 4000, zCm = 4000, sizeXCm = 500, sizeZCm = 500,
                floorYCm = 0, heightCm = 360,
                openings = new[]
                {
                    new Wg3SegmentOpening(3, 250, 240),
                    new Wg3SegmentOpening(1, 250, 240),
                    new Wg3SegmentOpening(0, 250, 500)
                }
            }));

            samples.Add(new KeyValuePair<string, Wg3Segment>("escalon_a_72cm", new Wg3Segment
            {
                xCm = -1000, zCm = 300, sizeXCm = 400, sizeZCm = 240,
                floorYCm = 72, heightCm = 320,
                openings = new[]
                {
                    new Wg3SegmentOpening(3, 120, 240),
                    new Wg3SegmentOpening(1, 120, 240)
                }
            }));

            samples.Add(new KeyValuePair<string, Wg3Segment>("tope_25m", new Wg3Segment
            {
                xCm = 10000, zCm = -20000, sizeXCm = 2500, sizeZCm = 500,
                floorYCm = -150, heightCm = 400,
                openings = new[]
                {
                    new Wg3SegmentOpening(2, 250, 500),
                    new Wg3SegmentOpening(0, 250, 500)
                }
            }));

            samples.Add(new KeyValuePair<string, Wg3Segment>("boca_descentrada", new Wg3Segment
            {
                xCm = 700, zCm = 700, sizeXCm = 800, sizeZCm = 300,
                floorYCm = 0, heightCm = 320,
                openings = new[]
                {
                    new Wg3SegmentOpening(2, 150, 240),
                    new Wg3SegmentOpening(1, 150, 240)
                }
            }));

            return samples;
        }

        [MenuItem("Backrooms/WorldGen3/Exportar oráculo de conectores")]
        public static void Export()
        {
            List<KeyValuePair<string, Wg3Segment>> samples = SampleSegments();
            var segments = new OracleSegment[samples.Count];

            for (int i = 0; i < samples.Count; i++)
            {
                Wg3Segment segment = samples[i].Value;
                List<Wg3Volume> volumes = Wg3GeneratedSegment.Build(segment);

                var boxes = new List<OracleBox>(volumes.Count);
                foreach (Wg3Volume v in volumes)
                {
                    // R25: la decoración se dibuja y no colisiona, así que no cruza al servidor y no
                    // entra en el oráculo. Compararla obligaría a que Rust generase rodapiés, que es
                    // exactamente lo que este reparto de responsabilidades evita.
                    if (!v.IsSolid) continue;
                    boxes.Add(new OracleBox
                    {
                        cx_cm = Mathf.RoundToInt(v.center.x * 100f),
                        cy_cm = Mathf.RoundToInt(v.center.y * 100f),
                        cz_cm = Mathf.RoundToInt(v.center.z * 100f),
                        sx_cm = Mathf.RoundToInt(v.size.x * 100f),
                        sy_cm = Mathf.RoundToInt(v.size.y * 100f),
                        sz_cm = Mathf.RoundToInt(v.size.z * 100f),
                        kind = (int)v.kind
                    });
                }

                var openings = new OracleOpening[segment.openings.Length];
                for (int o = 0; o < openings.Length; o++)
                {
                    openings[o] = new OracleOpening
                    {
                        side = segment.openings[o].side,
                        offset_cm = segment.openings[o].offsetCm,
                        width_cm = segment.openings[o].widthCm
                    };
                }

                segments[i] = new OracleSegment
                {
                    name = samples[i].Key,
                    x_cm = segment.xCm,
                    z_cm = segment.zCm,
                    size_x_cm = segment.sizeXCm,
                    size_z_cm = segment.sizeZCm,
                    floor_y_cm = segment.floorYCm,
                    height_cm = segment.heightCm,
                    style = segment.style,
                    openings = openings,
                    boxes = boxes.ToArray()
                };
            }

            var oracle = new ConnectorOracle
            {
                slab_thickness_mm = Mathf.RoundToInt(Wg3Geometry.SlabThickness * 1000f),
                wall_thickness_mm = Mathf.RoundToInt(new Wg3Piece().wallThickness * 1000f),
                segments = segments
            };

            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, RelativeFolder));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName);
            File.WriteAllText(path, JsonUtility.ToJson(oracle, true), new UTF8Encoding(false));

            int total = 0;
            foreach (OracleSegment c in oracle.segments) total += c.boxes.Length;
            Debug.Log($"[WG3] oráculo de conectores en {path}: {oracle.segments.Length} tramos, " +
                      $"{total} cajas sólidas, losa {oracle.slab_thickness_mm} mm, " +
                      $"pared {oracle.wall_thickness_mm} mm.");
        }
    }
}
#endif
