using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// WorldGen3, F1 — el manifiesto.
    ///
    /// El test que cierra la fase es <see cref="TheManifestAloneCanPlaceTheSameWorld"/>: compone un
    /// mundo con el catálogo de código y otro con el catálogo RECONSTRUIDO DESDE EL JSON, y exige
    /// que salgan idénticos. Eso convierte "el manifiesto lleva lo necesario para colocar" de
    /// suposición en afirmación comprobada, y es justo la suposición cuyo fallo solo se descubriría
    /// a mitad de F2, con el parser de Rust ya escrito.
    /// </summary>
    [TestFixture]
    public class Wg3ManifestTests
    {
        private static List<Wg3Piece> Catalog() => Wg3Catalog.Build();
        private static Wg3Manifest Manifest() => Wg3Manifest.FromCatalog(Catalog());

        // ── formato ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void EveryCatalogPieceIsExportedWithItsSockets()
        {
            List<Wg3Piece> catalog = Catalog();
            Wg3Manifest manifest = Wg3Manifest.FromCatalog(catalog);

            Assert.AreEqual(Wg3Manifest.FormatVersion, manifest.version);
            Assert.AreEqual(catalog.Count, manifest.pieces.Length);

            for (int i = 0; i < catalog.Count; i++)
            {
                Wg3Piece src = catalog[i];
                Wg3ManifestPiece dst = manifest.pieces[i];

                Assert.AreEqual(i, dst.index, "el índice tiene que ser la posición: ES lo que viaja");
                Assert.AreEqual(src.id, dst.id);
                Assert.AreEqual(src.sizeX, dst.size_x, 1e-4f);
                Assert.AreEqual(src.sizeZ, dst.size_z, 1e-4f);
                Assert.AreEqual(src.heightMeters, dst.height_meters, 1e-4f);
                Assert.AreEqual((int)src.scale, dst.scale);
                Assert.AreEqual(src.minDepth, dst.min_depth);
                Assert.AreEqual(src.isDeadEnd, dst.dead_end);
                Assert.AreEqual(src.sockets.Length, dst.sockets.Length, $"{src.id}: bocas");

                for (int s = 0; s < src.sockets.Length; s++)
                {
                    Assert.AreEqual(src.sockets[s].side, dst.sockets[s].side);
                    Assert.AreEqual(src.sockets[s].offset, dst.sockets[s].offset, 1e-4f);
                    Assert.AreEqual(src.sockets[s].width, dst.sockets[s].width, 1e-4f);
                    Assert.AreEqual((int)src.sockets[s].type, dst.sockets[s].type);
                }
            }
        }

        [Test]
        public void TheChuletaCarriesEverySolidVolumeAndNoDecoration()
        {
            // REGLA R25 cruzando la frontera de autoridad: lo que no bloquea, no viaja. Si el
            // rodapié llegara al manifiesto, el servidor frenaría al jugador a 12 cm de cada pared
            // del mundo y el cliente no mostraría nada que lo explicara.
            List<Wg3Piece> catalog = Catalog();
            Wg3Manifest manifest = Wg3Manifest.FromCatalog(catalog);

            bool sawDecorationInGeometry = false;
            for (int i = 0; i < catalog.Count; i++)
            {
                int solid = 0;
                foreach (Wg3Volume v in Wg3Geometry.Build(catalog[i]))
                {
                    if (v.IsSolid) solid++;
                    else sawDecorationInGeometry = true;
                }
                Assert.AreEqual(solid, manifest.pieces[i].collision.Length,
                    $"{catalog[i].id}: la chuleta no cuadra con los volúmenes sólidos");

                foreach (Wg3ManifestVolume mv in manifest.pieces[i].collision)
                    Assert.AreNotEqual((int)Wg3VolumeKind.Decoration, mv.kind,
                        $"{catalog[i].id}: se exportó decoración");
            }
            Assert.IsTrue(sawDecorationInGeometry,
                "no había decoración que excluir — el test no estaba probando nada");
        }

        [Test]
        public void JsonRoundTripsWithoutLosingAnything()
        {
            Wg3Manifest original = Manifest();
            Wg3Manifest parsed = Wg3Manifest.FromJson(original.ToJson());

            Assert.AreEqual(original.version, parsed.version);
            Assert.AreEqual(original.digest, parsed.digest);
            Assert.AreEqual(original.pieces.Length, parsed.pieces.Length);

            for (int i = 0; i < original.pieces.Length; i++)
            {
                Wg3ManifestPiece a = original.pieces[i], b = parsed.pieces[i];
                Assert.AreEqual(a.id, b.id);
                Assert.AreEqual(a.sockets.Length, b.sockets.Length);
                Assert.AreEqual(a.collision.Length, b.collision.Length, $"{a.id}: cajas");

                for (int v = 0; v < a.collision.Length; v++)
                {
                    Assert.AreEqual(a.collision[v].cx, b.collision[v].cx, 1e-4f);
                    Assert.AreEqual(a.collision[v].cy, b.collision[v].cy, 1e-4f);
                    Assert.AreEqual(a.collision[v].cz, b.collision[v].cz, 1e-4f);
                    Assert.AreEqual(a.collision[v].sx, b.collision[v].sx, 1e-4f);
                    Assert.AreEqual(a.collision[v].sy, b.collision[v].sy, 1e-4f);
                    Assert.AreEqual(a.collision[v].sz, b.collision[v].sz, 1e-4f);
                    Assert.AreEqual(a.collision[v].yaw, b.collision[v].yaw, 1e-3f);
                    Assert.AreEqual(a.collision[v].kind, b.collision[v].kind);
                }
            }
        }

        // ── digest ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void ExportingTwiceGivesTheSameBytesAndTheSameDigest()
        {
            // Sin esto el digest no vale para nada: si dos exportaciones del mismo catálogo
            // difirieran, comparar digests entre dos máquinas daría falsos desacuerdos y se
            // acabaría ignorando la comparación, que es peor que no tenerla.
            Assert.AreEqual(Manifest().ToJson(), Manifest().ToJson());
            Assert.AreEqual(Manifest().digest, Manifest().digest);
        }

        [Test]
        public void TheDigestCoversThePiecesAndNothingElse()
        {
            Wg3Manifest manifest = Manifest();
            Assert.AreEqual(manifest.digest, manifest.RecomputeDigest());
            Assert.AreEqual(64, manifest.digest.Length, "SHA-256 en hex son 64 caracteres");
            StringAssert.IsMatch("^[0-9a-f]{64}$", manifest.digest, "hex en minúscula");
        }

        [Test]
        public void MovingASocketChangesTheDigest()
        {
            List<Wg3Piece> catalog = Catalog();
            string before = Wg3Manifest.FromCatalog(catalog).digest;

            Wg3Socket[] sockets = catalog[0].sockets;
            sockets[0].offset += 0.05f;
            string after = Wg3Manifest.FromCatalog(catalog).digest;

            Assert.AreNotEqual(before, after,
                "mover una boca 5 cm no cambió el digest: no está firmando lo que dice firmar");
        }

        [Test]
        public void MovingAPillarChangesTheDigest()
        {
            // La columna no toca ni bocas ni huella: si el digest no la viera, dos catálogos con
            // colisiones distintas se darían por iguales — que es exactamente el caso en el que
            // comparar digests tenía que servir de algo.
            List<Wg3Piece> catalog = Catalog();
            int index = catalog.FindIndex(p => p.pillars.Length > 0);
            Assert.GreaterOrEqual(index, 0, "ninguna pieza del catálogo tiene columnas");

            string before = Wg3Manifest.FromCatalog(catalog).digest;
            catalog[index].pillars[0].position += new Vector2(0.4f, 0f);
            string after = Wg3Manifest.FromCatalog(catalog).digest;

            Assert.AreNotEqual(before, after, "mover una columna no cambió el digest");
        }

        // ── el test que cierra F1 ───────────────────────────────────────────────────────────

        [Test]
        public void TheManifestAloneCanPlaceTheSameWorld()
        {
            // Compone con el catálogo de código y con el reconstruido SOLO desde el JSON. Si los
            // dos mundos no son idénticos, al manifiesto le falta algo que la colocación necesita,
            // y ese hallazgo llegaría si no a mitad de F2 con el parser de Rust ya escrito.
            List<Wg3Piece> authored = Catalog();
            List<Wg3Piece> fromJson = Wg3Manifest.FromJson(Manifest().ToJson()).ToPlacementCatalog();

            foreach (int seed in new[] { 42, 7, 1337, -19 })
            {
                var settings = new Wg3ComposerSettings { budget = 30 };
                string a = Wg3Composer.Compose(seed, authored, settings).Signature();
                string b = Wg3Composer.Compose(seed, fromJson, settings).Signature();
                Assert.AreEqual(a, b, $"semilla {seed}: el manifiesto no basta para colocar igual");
            }
        }

        [Test]
        public void PlacedCollisionFromTheManifestMatchesTheAuthoredGeometry()
        {
            // La otra mitad: no basta con colocar la pieza en el mismo sitio, sus cajas tienen que
            // caer donde el cliente dibuja. Es el contrato entero de WG3 en un test — Unity hornea,
            // Rust coloca, y los dos acaban en el mismo metro cúbico.
            List<Wg3Piece> catalog = Catalog();
            Wg3Manifest manifest = Wg3Manifest.FromCatalog(catalog);

            for (int i = 0; i < catalog.Count; i++)
                for (int r = 0; r < 4; r++)
                {
                    var placement = new Wg3Placement
                    {
                        piece = catalog[i], rotation = r, originX = 91.5f, originZ = -13.25f,
                        socketState = new byte[catalog[i].sockets.Length]
                    };

                    List<Wg3Volume> authored = Wg3Geometry.BuildPlaced(placement);
                    authored.RemoveAll(v => !v.IsSolid);
                    List<Wg3Volume> baked = Wg3Manifest.PlacedCollision(manifest.pieces[i], placement);

                    Assert.AreEqual(authored.Count, baked.Count, $"{catalog[i].id} giro {r}: cuenta");
                    for (int v = 0; v < authored.Count; v++)
                    {
                        Assert.AreEqual(authored[v].center.x, baked[v].center.x, 1e-3f,
                            $"{catalog[i].id} giro {r} caja {v}: X");
                        Assert.AreEqual(authored[v].center.y, baked[v].center.y, 1e-3f,
                            $"{catalog[i].id} giro {r} caja {v}: Y");
                        Assert.AreEqual(authored[v].center.z, baked[v].center.z, 1e-3f,
                            $"{catalog[i].id} giro {r} caja {v}: Z");
                        Assert.AreEqual(authored[v].size, baked[v].size,
                            $"{catalog[i].id} giro {r} caja {v}: tamaño");
                        Assert.AreEqual(Mathf.Repeat(authored[v].yawDegrees, 360f),
                                        Mathf.Repeat(baked[v].yawDegrees, 360f), 1e-3f,
                            $"{catalog[i].id} giro {r} caja {v}: giro");
                    }
                }
        }
    }
}
