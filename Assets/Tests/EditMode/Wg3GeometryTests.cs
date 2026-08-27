using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// WorldGen3, tanda 2 — la geometría de una pieza.
    ///
    /// El test que importa es <see cref="NoSolidVolumeBlocksADoorway"/>. Todos los demás son
    /// higiene; ese es el que impide el peor fallo posible del sistema, que no es una pieza fea
    /// sino un vano que se VE abierto y está cerrado por colisión: el jugador ve una cosa y el
    /// juego hace otra, y desde dentro es indistinguible de un bug de red.
    /// </summary>
    [TestFixture]
    public class Wg3GeometryTests
    {
        private static List<Wg3Piece> Catalog() => Wg3Catalog.Build();

        // ── contención en caja con giro ─────────────────────────────────────────────────────

        private static bool Contains(in Wg3Volume v, Vector3 p, float margin = 0f)
        {
            Vector3 local = Quaternion.Euler(0f, -v.yawDegrees, 0f) * (p - v.center);
            return Mathf.Abs(local.x) <= v.size.x * 0.5f + margin
                && Mathf.Abs(local.y) <= v.size.y * 0.5f + margin
                && Mathf.Abs(local.z) <= v.size.z * 0.5f + margin;
        }

        /// <summary>Extensión en XZ de una caja con giro, que es lo que ocupa de verdad en el
        /// mundo. Con yaw 0 coincide con el tamaño; con 45° es notablemente mayor.</summary>
        private static Vector2 FootprintExtent(in Wg3Volume v)
        {
            float rad = v.yawDegrees * Mathf.Deg2Rad;
            float c = Mathf.Abs(Mathf.Cos(rad)), s = Mathf.Abs(Mathf.Sin(rad));
            return new Vector2(v.size.x * c + v.size.z * s, v.size.x * s + v.size.z * c);
        }

        // ── estructura básica ───────────────────────────────────────────────────────────────

        [Test]
        public void EveryPieceHasAFloorACeilingAndWalls()
        {
            foreach (Wg3Piece piece in Catalog())
            {
                List<Wg3Volume> vs = Wg3Geometry.Build(piece);
                int floors = 0, ceilings = 0, walls = 0;
                foreach (Wg3Volume v in vs)
                {
                    if (v.kind == Wg3VolumeKind.Floor) floors++;
                    else if (v.kind == Wg3VolumeKind.Ceiling) ceilings++;
                    else if (v.kind == Wg3VolumeKind.Wall) walls++;
                }
                Assert.AreEqual(1, floors, $"{piece.id}: losas de suelo");
                Assert.AreEqual(1, ceilings, $"{piece.id}: losas de techo");
                Assert.Greater(walls, 0, $"{piece.id}: sin una sola pared");
            }
        }

        [Test]
        public void AuthoredFeaturesAllReachTheVolumes()
        {
            foreach (Wg3Piece piece in Catalog())
            {
                List<Wg3Volume> vs = Wg3Geometry.Build(piece);
                int pillars = 0, blocks = 0, steps = 0;
                foreach (Wg3Volume v in vs)
                {
                    if (v.kind == Wg3VolumeKind.Pillar) pillars++;
                    else if (v.kind == Wg3VolumeKind.Block) blocks++;
                    else if (v.kind == Wg3VolumeKind.Step) steps++;
                }
                Assert.AreEqual(piece.pillars.Length, pillars, $"{piece.id}: columnas");
                Assert.AreEqual(piece.blocks.Length, blocks, $"{piece.id}: bloques");

                int expectedSteps = 0;
                foreach (Wg3StairRun s in piece.stairs) expectedSteps += s.steps;
                Assert.AreEqual(expectedSteps, steps, $"{piece.id}: escalones");
            }
        }

        [Test]
        public void EveryVolumeStaysInsideTheDeclaredFootprint()
        {
            // Si una pieza se sale de su huella, el compositor decide sin solape y el mundo se
            // solapa igual: la comprobación de ocupación mira la huella declarada, no la real.
            foreach (Wg3Piece piece in Catalog())
            {
                foreach (Wg3Volume v in Wg3Geometry.Build(piece))
                {
                    Vector2 ext = FootprintExtent(v);
                    Assert.GreaterOrEqual(v.center.x - ext.x * 0.5f, -1e-3f, $"{piece.id}: sale por −X");
                    Assert.GreaterOrEqual(v.center.z - ext.y * 0.5f, -1e-3f, $"{piece.id}: sale por −Z");
                    Assert.LessOrEqual(v.center.x + ext.x * 0.5f, piece.sizeX + 1e-3f, $"{piece.id}: sale por +X");
                    Assert.LessOrEqual(v.center.z + ext.y * 0.5f, piece.sizeZ + 1e-3f, $"{piece.id}: sale por +Z");
                }
            }
        }

        // ── el vano existe ──────────────────────────────────────────────────────────────────

        [Test]
        public void NoSolidVolumeBlocksADoorway()
        {
            foreach (Wg3Piece piece in Catalog())
            {
                List<Wg3Volume> vs = Wg3Geometry.Build(piece);
                for (int s = 0; s < piece.sockets.Length; s++)
                {
                    Wg3Socket socket = piece.sockets[s];
                    Vector2 mouth = Wg3Piece.LocalPoint(socket.side, socket.offset, piece.sizeX, piece.sizeZ);
                    Vector2 inward = -Wg3Piece.OutwardNormal(socket.side);

                    // Tres alturas y tres puntos a lo ancho del vano: la esquina superior de una
                    // puerta es donde acabaría el fallo si el corte de la pared se hiciera por el
                    // centro en vez de por los bordes de la boca.
                    Vector2 along = new Vector2(-inward.y, inward.x);
                    foreach (float t in new[] { -0.35f, 0f, 0.35f })
                    {
                        Vector2 xz = mouth + inward * (piece.wallThickness * 0.5f)
                                           + along * (socket.width * t);
                        foreach (float y in new[] { 0.25f, 1.2f, 1.9f })
                        {
                            var p = new Vector3(xz.x, y, xz.y);
                            foreach (Wg3Volume v in vs)
                            {
                                if (!v.IsSolid) continue;
                                Assert.IsFalse(Contains(v, p, -1e-3f),
                                    $"{piece.id}: el vano {s} está tapado por un volumen {v.kind} " +
                                    $"en {p}");
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void TheSkirtingIsDecorationAndNeverBlocks()
        {
            // REGLA R25 escrita como test: si el rodapié colisionara, frenaría al jugador a 12 cm
            // de cada pared del mundo. La línea entre estructura y decoración tiene que ser un
            // dato, no una intención.
            bool sawSkirting = false;
            foreach (Wg3Piece piece in Catalog())
                foreach (Wg3Volume v in Wg3Geometry.Build(piece))
                    if (v.kind == Wg3VolumeKind.Decoration)
                    {
                        sawSkirting = true;
                        Assert.IsFalse(v.IsSolid, $"{piece.id}: la decoración cuenta como sólida");
                    }
            Assert.IsTrue(sawSkirting, "ninguna pieza lleva rodapié — R31 no se está aplicando");
        }

        // ── giro: geometría contra composición ──────────────────────────────────────────────

        [Test]
        public void APlacedDoorwayLandsOnTheSocketWorldPoint()
        {
            // Ata las DOS rotaciones del sistema: la de la composición (Wg3Placement.WorldPoint) y
            // la de la geometría (Wg3Geometry.BuildPlaced). Están escritas por separado a
            // propósito; si divergen, la pared de una pieza girada tapa la puerta de su vecina y
            // el síntoma aparece a cien metros de la causa.
            foreach (Wg3Piece piece in Catalog())
            {
                for (int r = 0; r < 4; r++)
                {
                    var placement = new Wg3Placement
                    {
                        piece = piece, rotation = r, originX = 137.5f, originZ = -64.25f,
                        socketState = new byte[piece.sockets.Length]
                    };
                    List<Wg3Volume> vs = Wg3Geometry.BuildPlaced(placement);

                    for (int s = 0; s < piece.sockets.Length; s++)
                    {
                        Vector2 p = placement.WorldPoint(s);
                        Vector2 inward = -Wg3Piece.OutwardNormal(placement.WorldSide(s));
                        Vector2 xz = p + inward * (piece.wallThickness * 0.5f);
                        var probe = new Vector3(xz.x, 1.2f, xz.y);

                        foreach (Wg3Volume v in vs)
                        {
                            if (!v.IsSolid) continue;
                            Assert.IsFalse(Contains(v, probe, -1e-3f),
                                $"{piece.id} giro {r}: la boca {s} queda tapada tras colocar");
                        }
                    }
                }
            }
        }

        [Test]
        public void PlacedVolumesStayInsideThePlacementFootprint()
        {
            foreach (Wg3Piece piece in Catalog())
                for (int r = 0; r < 4; r++)
                {
                    var placement = new Wg3Placement
                    {
                        piece = piece, rotation = r, originX = -12f, originZ = 30f,
                        socketState = new byte[piece.sockets.Length]
                    };
                    foreach (Wg3Volume v in Wg3Geometry.BuildPlaced(placement))
                    {
                        Vector2 ext = FootprintExtent(v);
                        Assert.GreaterOrEqual(v.center.x - ext.x * 0.5f, placement.originX - 1e-2f,
                            $"{piece.id} giro {r}: sale por −X");
                        Assert.GreaterOrEqual(v.center.z - ext.y * 0.5f, placement.originZ - 1e-2f,
                            $"{piece.id} giro {r}: sale por −Z");
                        Assert.LessOrEqual(v.center.x + ext.x * 0.5f, placement.MaxX + 1e-2f,
                            $"{piece.id} giro {r}: sale por +X");
                        Assert.LessOrEqual(v.center.z + ext.y * 0.5f, placement.MaxZ + 1e-2f,
                            $"{piece.id} giro {r}: sale por +Z");
                    }
                }
        }

        // ── malla ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void TheMeshCoversEveryVolumeAndSplitsBySubMesh()
        {
            foreach (Wg3Piece piece in Catalog())
            {
                List<Wg3Volume> vs = Wg3Geometry.Build(piece);
                Mesh mesh = Wg3MeshBuilder.Build(vs, Vector3.zero);
                try
                {
                    Assert.AreEqual(Wg3MeshBuilder.SubMesh.Count, mesh.subMeshCount, piece.id);
                    Assert.AreEqual(vs.Count * 24, mesh.vertexCount,
                        $"{piece.id}: 24 vértices por caja, caras con normal dura");

                    int triangles = 0;
                    for (int i = 0; i < mesh.subMeshCount; i++)
                        triangles += (int)mesh.GetIndexCount(i) / 3;
                    Assert.AreEqual(vs.Count * 12, triangles, $"{piece.id}: 12 triángulos por caja");

                    Assert.Greater(mesh.GetIndexCount(Wg3MeshBuilder.SubMesh.Floor), 0u,
                        $"{piece.id}: el suelo no llegó a su submalla");
                    Assert.Greater(mesh.GetIndexCount(Wg3MeshBuilder.SubMesh.Structure), 0u,
                        $"{piece.id}: la estructura no llegó a su submalla");
                }
                finally { Object.DestroyImmediate(mesh); }
            }
        }

        [Test]
        public void TheMeshIsBuiltRelativeToItsOrigin()
        {
            // Los vértices salen relativos para que a 5 km del origen un float siga distinguiendo
            // el milímetro. Si esto se rompe, el rodapié se cose mal solo lejos del centro, que es
            // el sitio donde nadie mira al depurar.
            Wg3Piece piece = Catalog()[0];
            var placement = new Wg3Placement
            {
                piece = piece, rotation = 0, originX = 5000f, originZ = -5000f,
                socketState = new byte[piece.sockets.Length]
            };
            List<Wg3Volume> vs = Wg3Geometry.BuildPlaced(placement);
            var origin = new Vector3(placement.originX, 0f, placement.originZ);
            Mesh mesh = Wg3MeshBuilder.Build(vs, origin);
            try
            {
                Assert.Less(mesh.bounds.center.magnitude, 40f,
                    "la malla lleva las coordenadas de mundo dentro en vez de ser relativa");
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
