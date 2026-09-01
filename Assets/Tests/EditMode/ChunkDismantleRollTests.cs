using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-114 — el sorteo de muebles desmontables y su tabla de materiales, headless. Es PURO por
    /// diseño, igual que <see cref="ChunkContainerRollTests"/>, así que aquí cabe todo menos la
    /// colocación (el rayo de andabilidad) y el registro (IPC), que son de Play.
    /// </summary>
    [TestFixture]
    public class ChunkDismantleRollTests
    {
        private const long Seed = 90114;

        private const byte Office = 0, Spine = 1, Corridor = 2, Hall = 3, Service = 4,
            DeadEnd = 5, Stair = 6;

        private static System.Func<float, float, byte?> Everywhere(byte style) => (u, v) => style;

        private static bool Roll(long seed, int cx, int cz, byte style,
            out ChunkDismantleRoll.Entry entry, out bool spaceKnown)
            => ChunkDismantleRoll.RollPropByStyle(seed, cx, cz, Everywhere(style), out entry, out spaceKnown);

        private static bool FindOne(byte style, out ChunkDismantleRoll.Entry found, out int atX)
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

        /// <summary>La misma pregunta, la misma respuesta: es lo que permite que host y joiner
        /// instancien el MISMO mueble en el mismo sitio sin mandarlo por el cable (ADR-114 D3).
        /// </summary>
        [Test]
        public void Roll_IsDeterministicForTheSameChunk()
        {
            Assert.IsTrue(FindOne(Office, out var first, out int cx), "ningún mueble en 400 chunks");
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(Roll(Seed, cx, 0, Office, out var again, out _));
                Assert.AreEqual(first.Prop, again.Prop);
                Assert.AreEqual(first.U, again.U);
                Assert.AreEqual(first.V, again.V);
                Assert.AreEqual(first.Rotation, again.Rotation);
            }
        }

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

        /// <summary>ADR-114 D9: sal propia. Si el desmontable compartiera sorteo con el contenedor,
        /// los dos caerían en los mismos chunks y en la misma esquina — y tocar la densidad de uno
        /// movería la del otro, que es un número de balance ya en juego.</summary>
        [Test]
        public void DismantleAndContainerDoNotShareTheirRoll()
        {
            int same = 0, total = 0;
            for (int cx = 0; cx < 400; cx++)
            {
                bool dis = Roll(Seed, cx, 0, Office, out var de, out _);
                bool con = ChunkContainerRoll.RollContainerByStyle(
                    Seed, cx, 0, Everywhere(Office),
                    s => ChunkLootRoll.DefaultStyleLootProfiles()[s],
                    out var ce, out _);
                if (dis && con)
                {
                    total++;
                    if (de.U == ce.U && de.V == ce.V)
                        same++;
                }
            }
            Assert.AreEqual(0, same,
                $"{same}/{total} chunks ponen mueble y contenedor en la MISMA esquina: sal compartida");
        }

        // ── el papel manda ─────────────────────────────────────────────────────────────────────

        /// <summary>Los tres sitios de PASO no llevan mueble: estorbaría la circulación, igual que
        /// ya protegen `itemCacheChance = 0` en la escalera y el contenedor del Paso 3.</summary>
        [Test]
        public void ThoroughfareRolesNeverGetAProp()
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

        /// <summary>ADR-114 D9: los tres props del alcance, cada uno en su papel. Ni uno más.</summary>
        [Test]
        public void OnlyTheThreeApprovedPropsExist()
        {
            Assert.AreEqual(DismantleProp.Desk, ChunkDismantleRoll.PropForStyle(Office));
            Assert.AreEqual(DismantleProp.Shelf, ChunkDismantleRoll.PropForStyle(Hall));
            Assert.AreEqual(DismantleProp.Shelf, ChunkDismantleRoll.PropForStyle(Service));
            Assert.AreEqual(DismantleProp.Chair, ChunkDismantleRoll.PropForStyle(DeadEnd));
            Assert.AreEqual(DismantleProp.None, ChunkDismantleRoll.PropForStyle(Spine));
            Assert.AreEqual(DismantleProp.None, ChunkDismantleRoll.PropForStyle(Corridor));
            Assert.AreEqual(DismantleProp.None, ChunkDismantleRoll.PropForStyle(Stair));
            // Un papel que no existe no puede reventar el sorteo ni inventarse un mueble.
            Assert.AreEqual(DismantleProp.None, ChunkDismantleRoll.PropForStyle(200));
        }

        // ── la tabla de materiales (D8) ────────────────────────────────────────────────────────

        /// <summary>La tabla, literal. Este test es el que impide que las cantidades se muevan sin
        /// pasar por una enmienda: ADR-114 D8 las fija, y un ADR no se edita.</summary>
        [Test]
        public void TheMaterialTableIsExactlyWhatTheAdrSays()
        {
            void Expect(DismantleProp prop, params (string name, int count)[] want)
            {
                var got = ChunkDismantleRoll.MaterialsFor(prop);
                Assert.AreEqual(want.Length, got.Count, $"{prop}: número de materiales");
                for (int i = 0; i < want.Length; i++)
                {
                    Assert.AreEqual(want[i].name, got[i].Name, $"{prop}[{i}] nombre");
                    Assert.AreEqual(want[i].count, got[i].Count, $"{prop}[{i}] cantidad");
                }
            }

            Expect(DismantleProp.Desk, ("Wooden Plank", 2), ("Metal Beam", 1));
            Expect(DismantleProp.Shelf, ("Metal Beam", 2), ("Wooden Plank", 1));
            Expect(DismantleProp.Chair, ("Wooden Plank", 1), ("Cloth", 1), ("Leather", 1));
            Assert.AreEqual(0, ChunkDismantleRoll.MaterialsFor(DismantleProp.None).Count);
        }

        /// <summary>ADR-114 D8: **la cinta adhesiva NO sale de desmontar**. Es el único de los cinco
        /// materiales autorizados que se queda fuera, y a propósito: si los cinco salen del mueble
        /// más cercano, el desmontaje deja de rozarse con la exploración.</summary>
        [Test]
        public void DuctTapeIsNeverAProductOfDismantling()
        {
            foreach (DismantleProp prop in System.Enum.GetValues(typeof(DismantleProp)))
            {
                foreach (var m in ChunkDismantleRoll.MaterialsFor(prop))
                    Assert.AreNotEqual("Duct Tape", m.Name, $"{prop} suelta cinta adhesiva");
            }
        }

        /// <summary>Ningún prop suelta electrónica: ADR-064 la tiene diferida y sin autorar, y
        /// ADR-114 D10 la deja explícitamente fuera.</summary>
        [Test]
        public void NoPropYieldsElectronics()
        {
            var banned = new[] { "Metal", "Circuit", "Battery", "Cable" };
            foreach (DismantleProp prop in System.Enum.GetValues(typeof(DismantleProp)))
            {
                foreach (var m in ChunkDismantleRoll.MaterialsFor(prop))
                    CollectionAssert.DoesNotContain(banned, m.Name, $"{prop} suelta electrónica");
            }
        }

        /// <summary>Todo material sale en cantidad positiva y con nombre. Un `count` de cero sería
        /// un mueble que se desmonta para nada.</summary>
        [Test]
        public void EveryYieldIsNamedAndPositive()
        {
            foreach (DismantleProp prop in System.Enum.GetValues(typeof(DismantleProp)))
            {
                if (prop == DismantleProp.None) continue;
                var got = ChunkDismantleRoll.MaterialsFor(prop);
                Assert.Greater(got.Count, 0, $"{prop} no suelta nada");
                foreach (var m in got)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(m.Name));
                    Assert.Greater(m.Count, 0);
                }
            }
        }

        // ── el id determinista (D2) ────────────────────────────────────────────────────────────

        /// <summary>ADR-114 D2 — el id es función de (semilla, chunk) y de nada más. Es lo que hace
        /// que el `remaining` guardado siga apuntando al mismo mueble tras recargar; con un
        /// contador de barrido, al recargar cambia el orden y cada escritorio desmontado vuelve
        /// entero con la salud de otro.</summary>
        [Test]
        public void NetIdIsStableAcrossRuns()
        {
            uint a = ChunkDismantleRoll.NetIdFor(Seed, 12, -7);
            for (int i = 0; i < 10; i++)
                Assert.AreEqual(a, ChunkDismantleRoll.NetIdFor(Seed, 12, -7));

            Assert.AreNotEqual(a, ChunkDismantleRoll.NetIdFor(Seed + 1, 12, -7), "la semilla no entra");
            Assert.AreNotEqual(a, ChunkDismantleRoll.NetIdFor(Seed, 13, -7), "cx no entra");
            Assert.AreNotEqual(a, ChunkDismantleRoll.NetIdFor(Seed, 12, -6), "cz no entra");
        }

        /// <summary>
        /// Y casi no choca — «casi» es la palabra exacta y conviene entender por qué.
        ///
        /// El id es un `u32` porque `StpHarvestableInfo.id` lo es, y ADR-114 D3 prohíbe tocar el
        /// cable. Con 4.290 millones de valores, el cumpleaños dice que 360.000 ids sortean del
        /// orden de 15 choques por pura aritmética, **por buena que sea la mezcla**: exigir cero
        /// aquí sería exigir que 2^32 diera para más de lo que da.
        ///
        /// Lo que sí se puede exigir es que choquen a ese ritmo y no a uno peor — una mezcla mala
        /// (recortar sin mezclar) da MILES, no quince. El margen de 4× separa las dos cosas sin
        /// volverse frágil.
        ///
        /// Y una colisión real es benigna, no silenciosa-y-corrupta: sólo importa si los DOS chunks
        /// sacaron mueble (10 % cada uno) y los dos están registrados a la vez, y aun entonces
        /// `RegisterHostProp` rechaza el id ya vinculado — el segundo mueble se queda sin desmontar
        /// en vez de compartir salud con el primero.
        /// </summary>
        [Test]
        public void NetIdsCollideNoMoreThanChanceDemands()
        {
            const int side = 600, total = side * side;
            var seen = new HashSet<uint>();
            int collisions = 0;
            for (int cx = -side / 2; cx < side / 2; cx++)
            {
                for (int cz = -side / 2; cz < side / 2; cz++)
                {
                    if (!seen.Add(ChunkDismantleRoll.NetIdFor(Seed, cx, cz)))
                        collisions++;
                }
            }
            double expected = (double)total * total / (2.0 * 4294967296.0);
            Assert.LessOrEqual(collisions, expected * 4,
                $"{collisions} colisiones en {total} chunks; el azar solo justifica ~{expected:F0}");

            // Y el caso que mordió en el sembrador de contenedores: bits altos de cx.
            Assert.AreNotEqual(
                ChunkDismantleRoll.NetIdFor(Seed, 5, 5),
                ChunkDismantleRoll.NetIdFor(Seed, 5 + 65536, 5),
                "los bits altos de cx se están tirando fuera de la máscara");
        }

        /// <summary>Entre los muebles que EXISTEN de verdad —los que el sorteo saca, no todos los
        /// chunks del mundo— no hay ni una colisión. Es la cifra que decide si dos escritorios
        /// pueden compartir salud en una partida real.</summary>
        [Test]
        public void ActualPropsNeverShareAnId()
        {
            var seen = new HashSet<uint>();
            int props = 0, collisions = 0;
            for (int cx = -300; cx < 300; cx++)
            {
                for (int cz = -300; cz < 300; cz++)
                {
                    if (!Roll(Seed, cx, cz, Office, out _, out _))
                        continue;
                    props++;
                    if (!seen.Add(ChunkDismantleRoll.NetIdFor(Seed, cx, cz)))
                        collisions++;
                }
            }
            Assert.Greater(props, 1000, "el barrido no sacó muebles suficientes para probar nada");
            Assert.AreEqual(0, collisions, $"{collisions} de {props} muebles comparten id");
        }

        /// <summary>Nunca 0: el backend usa el 0 como «sin objetivo» y descarta el golpe entero, así
        /// que un mueble con id 0 sería indestructible sin decir por qué.</summary>
        [Test]
        public void NetIdIsNeverZero()
        {
            for (int cx = -500; cx < 500; cx++)
                Assert.AreNotEqual(0u, ChunkDismantleRoll.NetIdFor(Seed, cx, cx * 7));
        }

        // ── la puerta de "todavía no" ──────────────────────────────────────────────────────────

        /// <summary>Sin geometría montada la respuesta NO es «aquí no hay mueble»: es «todavía no se
        /// sabe», y la columna se queda sin sellar. Confundir las dos deja una columna
        /// re-sorteándose para siempre (ADR-108 enm. 4).</summary>
        [Test]
        public void AnUnknownSpaceIsNotAnAbsentProp()
        {
            bool got = ChunkDismantleRoll.RollPropByStyle(
                Seed, 3, 4, (u, v) => null, out _, out bool spaceKnown);
            Assert.IsFalse(got);
            Assert.IsFalse(spaceKnown);

            ChunkDismantleRoll.RollPropByStyle(
                Seed, 3, 4, Everywhere(Office), out _, out bool known);
            Assert.IsTrue(known);
        }

        [Test]
        public void ThePropStaysInsideItsOwnChunk()
        {
            Assert.IsTrue(FindOne(Office, out var e, out _));
            Assert.GreaterOrEqual(e.U, 0f);
            Assert.Less(e.U, 1f);
            Assert.GreaterOrEqual(e.V, 0f);
            Assert.Less(e.V, 1f);
            Assert.GreaterOrEqual(e.Rotation, 0f);
            Assert.Less(e.Rotation, 360f);
        }

        /// <summary>La densidad se queda cerca de la perilla declarada: si alguien la baja a la
        /// mitad, el mundo tiene la mitad de muebles. No es un test de balance — el número es
        /// placeholder por el riesgo 4 de ADR-114 — sino de que la perilla SIGNIFIQUE lo que dice.
        /// </summary>
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
            Assert.That(rate, Is.EqualTo(ChunkDismantleRoll.DismantleChance).Within(0.03f),
                $"{with}/{total} chunks con mueble: no se parece a DismantleChance");
        }
    }
}
