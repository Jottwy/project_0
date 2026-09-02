using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El sorteo de contenedores del mundo, headless: es PURO por diseño, igual que
    /// <see cref="ChunkLootRollTests"/>, así que aquí cabe todo menos la colocación (el rayo de
    /// andabilidad) y el sembrado (IPC), que son de Play.
    ///
    /// Lo que se fija aquí es la cadena entera menos su último tramo: worldgen (el papel del
    /// espacio) decide SI hay contenedor y QUÉ mueble es, y la tabla de loot de ese mismo papel
    /// decide qué lleva dentro.
    /// </summary>
    [TestFixture]
    public class ChunkContainerRollTests
    {
        private const long Seed = 7778;

        /// <summary>Papeles de WG3, en el orden de `DefaultStyleLootProfiles`.</summary>
        private const byte Office = 0, Spine = 1, Corridor = 2, Hall = 3, Service = 4,
            DeadEnd = 5, Stair = 6;

        private static ZoneLootProfile ProfileFor(byte style)
        {
            var t = ChunkLootRoll.DefaultStyleLootProfiles();
            return t[style < t.Length ? style : 0];
        }

        /// <summary>Un espacio conocido, del papel que se pida, en cualquier punto del chunk.</summary>
        private static System.Func<float, float, byte?> Everywhere(byte style)
            => (u, v) => style;

        private static bool Roll(long seed, int cx, int cz, byte style,
            out ChunkContainerRoll.Entry entry, out bool spaceKnown)
            => ChunkContainerRoll.RollContainerByStyle(
                seed, cx, cz, Everywhere(style), ProfileFor, out entry, out spaceKnown);

        /// <summary>Busca el primer chunk con contenedor de ese papel, para no depender de que un
        /// chunk concreto tenga uno — la probabilidad es baja a propósito.</summary>
        private static bool FindOne(byte style, out ChunkContainerRoll.Entry found, out int atX)
        {
            for (int cx = 0; cx < 400; cx++)
            {
                if (Roll(Seed, cx, 0, style, out var e, out _))
                {
                    found = e; atX = cx; return true;
                }
            }
            found = default; atX = 0; return false;
        }

        // ── determinismo ───────────────────────────────────────────────────────────────────────

        /// <summary>La misma pregunta, la misma respuesta. Es lo que permite que host y joiner
        /// vean el mismo mueble sin que nadie lo mande por el cable.</summary>
        [Test]
        public void Roll_IsDeterministicForTheSameChunk()
        {
            Assert.IsTrue(FindOne(Office, out var first, out int cx), "ningún contenedor en 400 chunks");
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(Roll(Seed, cx, 0, Office, out var again, out _));
                Assert.AreEqual(first.Prop, again.Prop);
                Assert.AreEqual(first.U, again.U);
                Assert.AreEqual(first.V, again.V);
                Assert.AreEqual(first.Rotation, again.Rotation);
                CollectionAssert.AreEqual(first.Contents, again.Contents);
            }
        }

        /// <summary>Otra semilla, otro mundo: si el sorteo ignorara la semilla, dos partidas
        /// tendrían los muebles en los mismos sitios.</summary>
        [Test]
        public void Roll_DiffersAcrossSeeds()
        {
            var a = new List<int>();
            var b = new List<int>();
            for (int cx = 0; cx < 200; cx++)
            {
                if (Roll(Seed, cx, 0, Office, out _, out _)) a.Add(cx);
                if (Roll(Seed + 1, cx, 0, Office, out _, out _)) b.Add(cx);
            }
            Assert.IsNotEmpty(a);
            CollectionAssert.AreNotEqual(a, b, "dos semillas reparten los muebles igual");
        }

        // ── el papel manda ─────────────────────────────────────────────────────────────────────

        /// <summary>Los tres sitios de PASO no llevan mueble: estorbaría la circulación, que es lo
        /// mismo que ya protegen `itemCacheChance = 0` en la escalera y la puerta de construcción
        /// de ADR-108 D6.</summary>
        [Test]
        public void ThoroughfareRolesNeverGetAContainer()
        {
            foreach (byte style in new[] { Spine, Corridor, Stair })
            {
                for (int cx = 0; cx < 400; cx++)
                {
                    Assert.IsFalse(Roll(Seed, cx, 0, style, out _, out _),
                        $"el papel {style} es sitio de paso y no admite mueble");
                }
            }
        }

        /// <summary>Cada papel con mueble tiene el SUYO, y el mueble sale del papel — que es lo que
        /// permite no mandarlo por el cable.</summary>
        [Test]
        public void EachRoleGetsItsOwnProp()
        {
            Assert.AreEqual(WorldContainerProp.Desk, ChunkContainerRoll.PropForStyle(Office));
            Assert.AreEqual(WorldContainerProp.Shelf, ChunkContainerRoll.PropForStyle(Hall));
            Assert.AreEqual(WorldContainerProp.Locker, ChunkContainerRoll.PropForStyle(Service));
            Assert.AreEqual(WorldContainerProp.Crate, ChunkContainerRoll.PropForStyle(DeadEnd));
            Assert.AreEqual(WorldContainerProp.None, ChunkContainerRoll.PropForStyle(Spine));
            Assert.AreEqual(WorldContainerProp.None, ChunkContainerRoll.PropForStyle(Corridor));
            Assert.AreEqual(WorldContainerProp.None, ChunkContainerRoll.PropForStyle(Stair));
            // Un papel que no existe no puede reventar el sorteo ni inventarse un mueble.
            Assert.AreEqual(WorldContainerProp.None, ChunkContainerRoll.PropForStyle(200));
        }

        /// <summary>Y el mueble que sale es el del papel donde CAE, no el de quien preguntó.</summary>
        [Test]
        public void ThePropMatchesTheRoleItLandsIn()
        {
            Assert.IsTrue(FindOne(Service, out var locker, out _));
            Assert.AreEqual(WorldContainerProp.Locker, locker.Prop);
            Assert.IsTrue(FindOne(DeadEnd, out var crate, out _));
            Assert.AreEqual(WorldContainerProp.Crate, crate.Prop);
        }

        // ── contenido ──────────────────────────────────────────────────────────────────────────

        /// <summary>Nunca vacío: un contenedor sin nada dentro sería INMORTAL — el despawn de
        /// ADR-028 solo corre después de sacar algo, y de un cofre vacío no se saca nada. Esa
        /// regla ya la aplica el servidor (`empty_loot`), y el sorteo no debe depender de ella.
        /// </summary>
        [Test]
        public void AContainerIsNeverEmpty()
        {
            int seen = 0;
            foreach (byte style in new[] { Office, Hall, Service, DeadEnd })
            {
                for (int cx = 0; cx < 300; cx++)
                {
                    if (!Roll(Seed, cx, 0, style, out var e, out _)) continue;
                    seen++;
                    Assert.IsNotNull(e.Contents);
                    Assert.GreaterOrEqual(e.Contents.Count, 1 + ChunkContainerRoll.ContainerBonusItems);
                    foreach (var name in e.Contents)
                        Assert.IsFalse(string.IsNullOrEmpty(name), "un hueco sin nombre");
                }
            }
            Assert.Greater(seen, 0, "no se sorteó ni un contenedor en todo el barrido");
        }

        /// <summary>Y NUNCA agua de almendras. Las botellas de la partida (una por cofre) salen de los cofres de
        /// `StpChestSpawner` y esa cuenta está medida contra los drenajes de sed; un mueble que
        /// sirviera agua movería un balance ya validado en partida.</summary>
        [Test]
        public void AContainerNeverServesAlmondWater()
        {
            foreach (byte style in new[] { Office, Hall, Service, DeadEnd })
            {
                for (int cx = 0; cx < 300; cx++)
                {
                    if (!Roll(Seed, cx, 0, style, out var e, out _)) continue;
                    CollectionAssert.DoesNotContain(e.Contents, "Almond Water");
                }
            }
        }

        // ── la puerta de "todavía no" ──────────────────────────────────────────────────────────

        /// <summary>Sin geometría montada la respuesta NO es "aquí no hay contenedor": es "todavía
        /// no se sabe", y la columna se queda sin sellar para reintentarla. Confundir las dos deja
        /// una columna re-sorteándose para siempre (ADR-108 enmienda 4).</summary>
        [Test]
        public void AnUnknownSpaceIsNotAnAbsentContainer()
        {
            bool got = ChunkContainerRoll.RollContainerByStyle(
                Seed, 3, 4, (u, v) => null, ProfileFor, out _, out bool spaceKnown);
            Assert.IsFalse(got);
            Assert.IsFalse(spaceKnown, "un espacio sin montar tiene que salir como NO conocido");

            // Y con espacio conocido, `spaceKnown` es cierto aunque no toque contenedor.
            ChunkContainerRoll.RollContainerByStyle(
                Seed, 3, 4, Everywhere(Office), ProfileFor, out _, out bool known);
            Assert.IsTrue(known);
        }

        /// <summary>El centro cae DENTRO del chunk. Si se saliera, el mueble aparecería en el
        /// chunk de al lado y los dos se lo disputarían.</summary>
        [Test]
        public void TheContainerStaysInsideItsOwnChunk()
        {
            Assert.IsTrue(FindOne(Office, out var e, out _));
            Assert.GreaterOrEqual(e.U, 0f);
            Assert.Less(e.U, 1f);
            Assert.GreaterOrEqual(e.V, 0f);
            Assert.Less(e.V, 1f);
            Assert.GreaterOrEqual(e.Rotation, 0f);
            Assert.Less(e.Rotation, 360f);
        }

        /// <summary>La densidad se queda cerca de la perilla declarada. No es un test de balance
        /// —el número es placeholder— sino de que la perilla SIGNIFICA lo que dice: si alguien la
        /// baja a la mitad, el mundo tiene la mitad de muebles.</summary>
        [Test]
        public void DensityFollowsTheDeclaredChance()
        {
            int with = 0, total = 0;
            for (int cx = 0; cx < 500; cx++)
            {
                for (int cz = 0; cz < 4; cz++)
                {
                    total++;
                    if (Roll(Seed, cx, cz, Office, out _, out _)) with++;
                }
            }
            float rate = (float)with / total;
            Assert.That(rate, Is.EqualTo(ChunkContainerRoll.ContainerChance).Within(0.03f),
                $"{with}/{total} chunks con mueble: no se parece a ContainerChance");
        }
    }
}
