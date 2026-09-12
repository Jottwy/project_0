using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-145 D1/D2/D5 — qué atrezo de <see cref="Wg3PropMsg"/> es desmontable, su clase de
    /// material y su id de red. PURO por diseño, igual que <see cref="ChunkDismantleRollTests"/>:
    /// sin escena, sin IPC.
    /// </summary>
    [TestFixture]
    public class Wg3PropHarvestTests
    {
        private const long Seed = 42;

        // ── D1: exactamente los kinds del ADR, ni uno más ──────────────────────────────────────

        [Test]
        public void ThePhysicalKindsAreExactlyTheNineteenTheAdrLists()
        {
            byte[] physical =
            {
                Wg3PropMsg.Desk, Wg3PropMsg.Chair, Wg3PropMsg.Cabinet, Wg3PropMsg.Shelf,
                Wg3PropMsg.Whiteboard, Wg3PropMsg.Trash, Wg3PropMsg.Box, Wg3PropMsg.Paper,
                Wg3PropMsg.Monitor, Wg3PropMsg.Clock, Wg3PropMsg.Phone, Wg3PropMsg.Keyboard,
                Wg3PropMsg.Tray, Wg3PropMsg.ChairFallen, Wg3PropMsg.TableLong, Wg3PropMsg.Counter,
                Wg3PropMsg.Microwave, Wg3PropMsg.Fridge, Wg3PropMsg.Rack,
            };
            foreach (byte kind in physical)
                Assert.IsTrue(Wg3PropHarvest.IsPhysical(kind), $"kind {kind} debería ser físico");
            Assert.AreEqual(19, new HashSet<byte>(physical).Count, "la lista del test tiene un duplicado");
        }

        /// <summary>D1 — Sign, CeilingTileHung, LightHung: sin malla ni collider que golpear, y
        /// ningún kind fuera del wire (0, 255) se cuela por accidente.</summary>
        [Test]
        public void TheThreeNonPhysicalKindsAreOut()
        {
            Assert.IsFalse(Wg3PropHarvest.IsPhysical(Wg3PropMsg.Sign));
            Assert.IsFalse(Wg3PropHarvest.IsPhysical(Wg3PropMsg.CeilingTileHung));
            Assert.IsFalse(Wg3PropHarvest.IsPhysical(Wg3PropMsg.LightHung));
            Assert.IsFalse(Wg3PropHarvest.IsPhysical(0));
            Assert.IsFalse(Wg3PropHarvest.IsPhysical(255));
        }

        /// <summary>Barrido completo del byte: exactamente 19 valores son físicos, ni uno de más
        /// colándose por un `default` mal puesto.</summary>
        [Test]
        public void ExactlyNineteenValuesInTheWholeByteRangeArePhysical()
        {
            int count = 0;
            for (int k = 0; k <= 255; k++)
                if (Wg3PropHarvest.IsPhysical((byte)k)) count++;
            Assert.AreEqual(19, count);
        }

        // ── D5: la tabla de clases, literal ─────────────────────────────────────────────────────

        [Test]
        public void TheMaterialClassTableIsExactlyWhatTheAdrSays()
        {
            void Expect(Wg3PropHarvest.MaterialClass want, params byte[] kinds)
            {
                foreach (byte kind in kinds)
                    Assert.AreEqual(want, Wg3PropHarvest.ClassFor(kind), $"kind {kind}");
            }

            Expect(Wg3PropHarvest.MaterialClass.WoodSmall,
                Wg3PropMsg.Chair, Wg3PropMsg.ChairFallen, Wg3PropMsg.Trash, Wg3PropMsg.Box,
                Wg3PropMsg.Whiteboard, Wg3PropMsg.Paper, Wg3PropMsg.Tray);
            Expect(Wg3PropHarvest.MaterialClass.MetalSmall,
                Wg3PropMsg.Monitor, Wg3PropMsg.Clock, Wg3PropMsg.Phone, Wg3PropMsg.Keyboard,
                Wg3PropMsg.Microwave);
            Expect(Wg3PropHarvest.MaterialClass.BigFurniture,
                Wg3PropMsg.Desk, Wg3PropMsg.TableLong, Wg3PropMsg.Counter);
            Expect(Wg3PropHarvest.MaterialClass.MetalContainer,
                Wg3PropMsg.Cabinet, Wg3PropMsg.Shelf, Wg3PropMsg.Fridge, Wg3PropMsg.Rack);
        }

        [Test]
        public void ItemDropsMatchTheAdrTableAndMetalContainerDropsNothingByItemDrops()
        {
            void Expect(Wg3PropHarvest.MaterialClass cls, params (string name, int count)[] want)
            {
                var got = Wg3PropHarvest.ItemDropsFor(cls);
                Assert.AreEqual(want.Length, got.Count, $"{cls}: número de materiales");
                for (int i = 0; i < want.Length; i++)
                {
                    Assert.AreEqual(want[i].name, got[i].Name, $"{cls}[{i}] nombre");
                    Assert.AreEqual(want[i].count, got[i].Count, $"{cls}[{i}] cantidad");
                }
            }

            Expect(Wg3PropHarvest.MaterialClass.WoodSmall, ("Wooden Plank", 1));
            Expect(Wg3PropHarvest.MaterialClass.MetalSmall, ("Metal Beam", 1));
            Expect(Wg3PropHarvest.MaterialClass.BigFurniture, ("Wooden Plank", 2), ("Metal Beam", 1));
            // MetalContainer va por logDefId/logCount (el carryable Metal) — nunca las dos vías.
            Assert.AreEqual(0, Wg3PropHarvest.ItemDropsFor(Wg3PropHarvest.MaterialClass.MetalContainer).Count);
        }

        // ── D2: el id determinista ───────────────────────────────────────────────────────────────

        [Test]
        public void NetIdIsStableAcrossRunsAndEveryComponentEnters()
        {
            uint a = Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Desk, 100, 0, 250);
            for (int i = 0; i < 10; i++)
                Assert.AreEqual(a, Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Desk, 100, 0, 250));

            Assert.AreNotEqual(a, Wg3PropHarvest.NetIdFor(Seed + 1, Wg3PropMsg.Desk, 100, 0, 250), "la semilla no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Chair, 100, 0, 250), "kind no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Desk, 101, 0, 250), "x_cm no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Desk, 100, 1, 250), "y_cm no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Desk, 100, 0, 251), "z_cm no entra");
        }

        [Test]
        public void NetIdIsNeverZero()
        {
            for (int x = -500; x < 500; x++)
                Assert.AreNotEqual(0u, Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Desk, x, 0, x * 7));
        }

        /// <summary>ADR-145 D2: sal propia — ni la de `ChunkDismantleRoll.NetIdFor` ni la de
        /// `StpWorldContainerSpawner.RequestIdFor`. Misma posición y semilla, mecanismo distinto,
        /// id distinto: si compartieran sal, un escritorio de ADR-114 y un atrezo de ADR-145 en el
        /// mismo punto acuñarían el mismo id y uno pisaría la salud del otro.</summary>
        [Test]
        public void TheSaltIsItsOwnNotSharedWithChunkDismantleRoll()
        {
            uint fromProp = Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Desk, 500, 0, 500);
            uint fromDismantle = ChunkDismantleRoll.NetIdFor(Seed, 5, 5);
            Assert.AreNotEqual(fromProp, fromDismantle);
        }

        [Test]
        public void ActualPhysicalPropsNeverShareAnId()
        {
            byte[] physical =
            {
                Wg3PropMsg.Desk, Wg3PropMsg.Chair, Wg3PropMsg.Cabinet, Wg3PropMsg.Shelf,
                Wg3PropMsg.Whiteboard, Wg3PropMsg.Trash, Wg3PropMsg.Box, Wg3PropMsg.Paper,
                Wg3PropMsg.Monitor, Wg3PropMsg.Clock, Wg3PropMsg.Phone, Wg3PropMsg.Keyboard,
                Wg3PropMsg.Tray, Wg3PropMsg.ChairFallen, Wg3PropMsg.TableLong, Wg3PropMsg.Counter,
                Wg3PropMsg.Microwave, Wg3PropMsg.Fridge, Wg3PropMsg.Rack,
            };
            var seen = new HashSet<uint>();
            int collisions = 0;
            foreach (byte kind in physical)
            {
                for (int x = -50; x < 50; x += 5)
                {
                    for (int z = -50; z < 50; z += 5)
                    {
                        if (!seen.Add(Wg3PropHarvest.NetIdFor(Seed, kind, x * 10, 0, z * 10)))
                            collisions++;
                    }
                }
            }
            Assert.AreEqual(0, collisions, $"{collisions} colisiones entre {seen.Count} props reales");
        }

        // ── D6: el id del cofre y su loot ────────────────────────────────────────────────────────

        [Test]
        public void ChestIdIsStableAcrossRunsAndEveryComponentEnters()
        {
            uint a = Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Cabinet, 100, 0, 250);
            for (int i = 0; i < 10; i++)
                Assert.AreEqual(a, Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Cabinet, 100, 0, 250));

            Assert.AreNotEqual(a, Wg3PropHarvest.ChestIdFor(Seed + 1, Wg3PropMsg.Cabinet, 100, 0, 250), "la semilla no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Shelf, 100, 0, 250), "kind no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Cabinet, 101, 0, 250), "x_cm no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Cabinet, 100, 1, 250), "y_cm no entra");
            Assert.AreNotEqual(a, Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Cabinet, 100, 0, 251), "z_cm no entra");
        }

        /// <summary>El bit reservado (ADR-145 D6): `World::next_corpse_id` en Rust es un contador
        /// puro desde 1, así que ningún id de cofre-atrezo puede chocar con un cadáver o con un
        /// cofre de `StpChestSpawner`/`StpWorldContainerSpawner` si SIEMPRE lleva este bit.</summary>
        [Test]
        public void ChestIdAlwaysCarriesTheReservedBit()
        {
            for (int x = -300; x < 300; x += 7)
            {
                uint id = Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Rack, x, 0, x * 3);
                Assert.AreNotEqual(0u, id & 0x8000_0000u, $"id={id:x8} sin el bit reservado");
                Assert.AreNotEqual(0u, id);
            }
        }

        /// <summary>Sal propia: ni la de `NetIdFor` (el harvestable de la MISMA pieza) ni la de
        /// `ChunkDismantleRoll`/`StpWorldContainerSpawner`. Compartirla pondría el cofre y el
        /// harvestable del mismo mueble en el mismo id — uno pisaría al otro en `world.corpses`
        /// / `net.stp_harvestables`, que son mapas distintos pero con la MISMA fuente de verdad
        /// de posición.</summary>
        [Test]
        public void TheChestSaltDiffersFromTheHarvestableSaltForTheSamePiece()
        {
            uint harvestId = Wg3PropHarvest.NetIdFor(Seed, Wg3PropMsg.Cabinet, 500, 0, 500);
            uint chestId = Wg3PropHarvest.ChestIdFor(Seed, Wg3PropMsg.Cabinet, 500, 0, 500);
            // Los dos podrían coincidir en los 31 bits bajos por puro azar sin que sea un fallo de
            // diseño (son mezclas independientes) — lo que el ADR exige es que la MEZCLA sea
            // distinta, comprobado abajo por el bit reservado que sólo lleva uno de los dos.
            Assert.AreEqual(0u, harvestId & 0x8000_0000u, "el harvestable no debe llevar el bit del cofre");
            Assert.AreNotEqual(0u, chestId & 0x8000_0000u);
        }

        [Test]
        public void ContainerClassPropsNeverShareAChestId()
        {
            byte[] containers = { Wg3PropMsg.Cabinet, Wg3PropMsg.Shelf, Wg3PropMsg.Fridge, Wg3PropMsg.Rack };
            var seen = new HashSet<uint>();
            int collisions = 0;
            foreach (byte kind in containers)
            {
                for (int x = -50; x < 50; x += 5)
                {
                    for (int z = -50; z < 50; z += 5)
                    {
                        if (!seen.Add(Wg3PropHarvest.ChestIdFor(Seed, kind, x * 10, 0, z * 10)))
                            collisions++;
                    }
                }
            }
            Assert.AreEqual(0, collisions, $"{collisions} colisiones entre {seen.Count} cofres reales");
        }

        [Test]
        public void ChestContentsAreDeterministicAndNeverEmpty()
        {
            var profile = ChunkLootRoll.DefaultStyleLootProfiles()[4]; // servicio/almacén

            var a = Wg3PropHarvest.ChestContentsFor(Seed, Wg3PropMsg.Shelf, 100, 0, 250, profile);
            var b = Wg3PropHarvest.ChestContentsFor(Seed, Wg3PropMsg.Shelf, 100, 0, 250, profile);
            CollectionAssert.AreEqual(a, b, "el mismo cofre debe sortear el mismo contenido");
            Assert.Greater(a.Count, 0, "un cofre sin loot sería inmortal en el backend (ADR-028 post-E3)");

            var c = Wg3PropHarvest.ChestContentsFor(Seed, Wg3PropMsg.Fridge, 100, 0, 250, profile);
            CollectionAssert.AreNotEqual(a, c, "kind entra en la semilla del sorteo de contenido");
        }
    }
}
