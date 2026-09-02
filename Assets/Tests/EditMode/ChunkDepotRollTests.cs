using System.Collections.Generic;
using System.Linq;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>El almacén de muebles del playtest: puro, determinista, y con las cuentas que lo
    /// hacen un almacén y no un montón. Corre headless.</summary>
    public class ChunkDepotRollTests
    {
        private const long Seed = 0x5EED_DE90_7CEL;

        [Test]
        public void LaCeldaEsDe200MetrosYRedondeaHaciaAbajo()
        {
            Assert.AreEqual(0, ChunkDepotRoll.CellOf(0f));
            Assert.AreEqual(0, ChunkDepotRoll.CellOf(199.9f));
            Assert.AreEqual(1, ChunkDepotRoll.CellOf(200f));
            Assert.AreEqual(-1, ChunkDepotRoll.CellOf(-0.1f), "negativo: la celda de la izquierda, no la 0");
            Assert.AreEqual(4, ChunkDepotRoll.ChunksPerCellSide, "200 m son 4 chunks de 50");
        }

        [Test]
        public void LosCandidatosSonDeterministasYCubrenLos16ChunksDeLaCelda()
        {
            var chunks = new HashSet<(int, int)>();
            for (int k = 0; k < ChunkDepotRoll.CandidateCount; k++)
            {
                var a = ChunkDepotRoll.CandidateAt(Seed, 3, -2, k);
                var b = ChunkDepotRoll.CandidateAt(Seed, 3, -2, k);
                Assert.AreEqual(a.X, b.X);
                Assert.AreEqual(a.Z, b.Z);

                // Dentro de la celda (3, -2): x en [600, 800), z en [-400, -200).
                Assert.IsTrue(a.X >= 600f && a.X < 800f, $"candidato {k} fuera de la celda en X: {a.X}");
                Assert.IsTrue(a.Z >= -400f && a.Z < -200f, $"candidato {k} fuera de la celda en Z: {a.Z}");
                chunks.Add(((int)System.Math.Floor(a.X / 50f), (int)System.Math.Floor(a.Z / 50f)));
            }
            Assert.AreEqual(16, chunks.Count, "un candidato por chunk, sin repetir");
        }

        [Test]
        public void ElOrdenCambiaConLaSemillaYConLaCelda()
        {
            var a = ChunkDepotRoll.CandidateAt(Seed, 0, 0, 0);
            var b = ChunkDepotRoll.CandidateAt(Seed + 1, 0, 0, 0);
            var c = ChunkDepotRoll.CandidateAt(Seed, 1, 0, 0);
            Assert.IsTrue(a.X != b.X || a.Z != b.Z, "otra semilla, otro primer candidato");
            Assert.IsTrue(c.X >= 200f && c.X < 400f, "la celda vecina cae en su propio rango");
        }

        [Test]
        public void UnaSalaGrandeRecibeDoceMueblesRepartidos()
        {
            // 20 x 15 m: una nave pequeña.
            var slots = ChunkDepotRoll.Layout(Seed, 0, 0, 100f, 100f, 120f, 115f);
            Assert.AreEqual(ChunkDepotRoll.PropsPerDepot, slots.Count);

            foreach (var s in slots)
            {
                Assert.IsTrue(s.X >= 100f + ChunkDepotRoll.WallMargin && s.X <= 120f - ChunkDepotRoll.WallMargin,
                    $"mueble pegado a la pared en X: {s.X}");
                Assert.IsTrue(s.Z >= 100f + ChunkDepotRoll.WallMargin && s.Z <= 115f - ChunkDepotRoll.WallMargin,
                    $"mueble pegado a la pared en Z: {s.Z}");
                Assert.AreEqual(0f, s.Rotation % 90f, "alineado a la sala");
            }

            for (int i = 0; i < slots.Count; i++)
            for (int j = i + 1; j < slots.Count; j++)
            {
                float dx = slots[i].X - slots[j].X, dz = slots[i].Z - slots[j].Z;
                Assert.GreaterOrEqual(System.Math.Sqrt(dx * dx + dz * dz), ChunkDepotRoll.Spacing - 1e-3,
                    "dos muebles demasiado juntos");
            }

            var kinds = slots.Select(s => s.Prop).ToList();
            Assert.AreEqual(4, kinds.Count(p => p == DismantleProp.Desk));
            Assert.AreEqual(4, kinds.Count(p => p == DismantleProp.Shelf));
            Assert.AreEqual(4, kinds.Count(p => p == DismantleProp.Chair));
        }

        [Test]
        public void ElRepartoEsDeterminista()
        {
            var a = ChunkDepotRoll.Layout(Seed, 2, 2, 0f, 0f, 30f, 20f);
            var b = ChunkDepotRoll.Layout(Seed, 2, 2, 0f, 0f, 30f, 20f);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].Prop, b[i].Prop);
                Assert.AreEqual(a[i].X, b[i].X);
                Assert.AreEqual(a[i].Z, b[i].Z);
                Assert.AreEqual(a[i].Rotation, b[i].Rotation);
            }
        }

        [Test]
        public void UnaSalaMedianaRecibeMenosYUnaPequenaNoVale()
        {
            // 9 x 7 m: caben 3x2 = 6 — justo el mínimo.
            var mediana = ChunkDepotRoll.Layout(Seed, 0, 0, 0f, 0f, 9f, 7f);
            Assert.AreEqual(6, mediana.Count);

            // 6 x 5 m: caben 2x2 = 4, por debajo del mínimo → no es un almacén.
            var pequena = ChunkDepotRoll.Layout(Seed, 0, 0, 0f, 0f, 6f, 5f);
            Assert.AreEqual(0, pequena.Count, "menos de MinPropsToPlace es «este espacio no vale», no «pon los que quepan»");

            Assert.AreEqual(0, ChunkDepotRoll.Layout(Seed, 0, 0, 0f, 0f, 1f, 1f).Count);
        }

        [Test]
        public void SoloLosPapelesDeMuebleAdmitenAlmacen()
        {
            Assert.IsTrue(ChunkDepotRoll.StyleIsEligible(0), "oficina");
            Assert.IsTrue(ChunkDepotRoll.StyleIsEligible(3), "nave");
            Assert.IsTrue(ChunkDepotRoll.StyleIsEligible(4), "servicio / almacén");
            foreach (byte paso in new byte[] { 1, 2, 5, 6 })
                Assert.IsFalse(ChunkDepotRoll.StyleIsEligible(paso), $"papel {paso} es sitio de paso o callejón");
        }

        [Test]
        public void LosIdsSonUnicosEntreSlotsYCeldasYNuncaCero()
        {
            var ids = new HashSet<uint>();
            int total = 0;
            for (int cx = -10; cx < 10; cx++)
            for (int cz = -10; cz < 10; cz++)
            for (int slot = 0; slot < ChunkDepotRoll.PropsPerDepot; slot++)
            {
                uint id = ChunkDepotRoll.NetIdFor(Seed, cx, cz, slot);
                Assert.AreNotEqual(0u, id);
                ids.Add(id);
                total++;
            }
            Assert.AreEqual(total, ids.Count, "colisión de id entre muebles de almacén");

            // Y no pisan a los del sorteo normal del mismo chunk: distinta sal.
            Assert.AreNotEqual(ChunkDismantleRoll.NetIdFor(Seed, 0, 0), ChunkDepotRoll.NetIdFor(Seed, 0, 0, 0));
        }
    }
}
