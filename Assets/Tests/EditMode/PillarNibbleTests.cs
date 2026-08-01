using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-033/Pillar (enmienda "Opción (c)"): el nibble alto de
    /// <c>GridChunkDataMsg.walls</c> marca qué sub-celda de 2.5 m de un tile de
    /// 5 m es <c>CellType::Pillar</c>. Dos frentes cubiertos:
    /// (a) el wire — round-trip msgpack REAL (encode→decode→Parse), incluida la
    ///     rama 0xcc que un valor ≥128 (0x80, el bit SE) ejercita por primera vez
    ///     en este campo; (b) el render — <see cref="GridChunkBuilder.BuildFromWalls"/>
    ///     instancia columnas donde corresponde y NO revienta si falta el prefab.
    /// </summary>
    [TestFixture]
    public class PillarNibbleTests
    {
        // ── (a) Wire: round-trip msgpack real, incluida la rama 0xcc ───────────

        /// <summary>
        /// Codifica un GridChunkData a mano (mismas claves que Parse espera),
        /// decodifica con el MISMO MsgPackReader que usa IPCClient, y confirma
        /// que los 4 bits de pilar — incluido 0x80 (SE), el primer valor de este
        /// campo que cruza a la rama `case 0xcc` del decoder — sobreviven intactos
        /// junto a los bits de arista existentes.
        /// </summary>
        [Test]
        public void PillarBitsSurviveRealMsgPackRoundTripIncluding0x80Branch()
        {
            byte nw = GridChunkBuilder.PillarNW; // 0x10 = 16 → fixint, rama normal
            byte ne = GridChunkBuilder.PillarNE; // 0x20 = 32 → fixint, rama normal
            byte sw = GridChunkBuilder.PillarSW; // 0x40 = 64 → fixint, rama normal
            byte se = GridChunkBuilder.PillarSE; // 0x80 = 128 → NO cabe en fixint (0..127) → 0xcc
            byte combined = (byte)(0x0F | GridChunkBuilder.PillarMask); // 0xFF = 255, ambos nibbles

            var writer = new MsgPackWriter();
            // "type" primero, igual que el envelope real que IPCClient.Dispatch consume antes de
            // llamar a GridChunkDataMsg.Parse(reader, remainingPairs) — root-tagged, ya no hay
            // Parse(object) al que decodificar contra un Dictionary suelto.
            writer.WriteMapHeader(5);
            writer.WriteString("type"); writer.WriteString("chunk_data");
            writer.WriteString("cx"); writer.WriteInt(3);
            writer.WriteString("cz"); writer.WriteInt(-7);
            writer.WriteString("layer"); writer.WriteInt(0);
            writer.WriteString("walls");
            writer.WriteArrayHeader(GridChunkDataMsg.Tiles);
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
            {
                writer.WriteArrayHeader(GridChunkDataMsg.Tiles);
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                {
                    byte val = 0;
                    if (x == 0 && z == 0) val = nw;
                    else if (x == 1 && z == 0) val = ne;
                    else if (x == 2 && z == 0) val = sw;
                    else if (x == 3 && z == 0) val = se;       // <- cruza a 0xcc
                    else if (x == 4 && z == 0) val = combined; // <- también 0xcc, ambos nibbles
                    writer.WriteInt(val);
                }
            }

            byte[] bytes = writer.ToArray();
            var reader = new MsgPackReader(bytes);
            int n = reader.ReadMapHeader();
            var typeKey = reader.ReadKey();
            Assert.IsTrue(MsgPackReader.Is(typeKey, "type"));
            reader.ReadString(); // "chunk_data"

            var msg = GridChunkDataMsg.Parse(reader, n - 1);
            Assert.AreEqual(3, msg.cx);
            Assert.AreEqual(-7, msg.cz);
            Assert.AreEqual(0, msg.layer);

            Assert.AreEqual(nw, msg.walls[0, 0], "NW (0x10, fixint) no sobrevivió");
            Assert.AreEqual(ne, msg.walls[1, 0], "NE (0x20, fixint) no sobrevivió");
            Assert.AreEqual(sw, msg.walls[2, 0], "SW (0x40, fixint) no sobrevivió");
            Assert.AreEqual(se, msg.walls[3, 0], "SE (0x80) no sobrevivió — rama 0xcc rota");
            Assert.AreEqual(combined, msg.walls[4, 0], "0xFF (ambos nibbles) no sobrevivió");
            // Regresión expresa: un tile normal, SOLO nibble bajo, sigue viajando
            // por fixint como antes de esta enmienda (nunca toca 0xcc).
            Assert.AreEqual(0, msg.walls[5, 0], "tile sin bits no debería llevar ninguno");
        }

        // ── (b) Render: BuildFromWalls instancia columnas, y no revienta sin prefab ──

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private static byte[,] EmptyWalls()
        {
            var walls = new byte[GridChunkDataMsg.Tiles, GridChunkDataMsg.Tiles];
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                    walls[x, z] = 0;
            return walls;
        }

        private static int CountPillarInstances(GameObject root)
        {
            int n = 0;
            foreach (Transform child in root.transform)
                if (child.name.StartsWith("Pillar")) n++;
            return n;
        }

        [Test]
        public void BuildFromWallsInstantiatesOnePillarPerFlaggedSubCell()
        {
            var prefabs = GridPrefabSet.LoadFromResources();
            Assert.IsNotNull(prefabs.pillar,
                "Assets/Resources/GridPrefabs/Pillar.prefab debe existir para este test " +
                "(commiteado; si falta, correr Backrooms/Create Grid Prefabs)");

            var walls = EmptyWalls();
            // Tile (4,4): dos sub-celdas con columna (NW + SE), las otras dos libres.
            walls[4, 4] = (byte)(GridChunkBuilder.PillarNW | GridChunkBuilder.PillarSE);

            _root = GridChunkBuilder.BuildFromWalls(walls, prefabs, Vector3.zero, "TestChunkWithPillars", 0, 1);

            Assert.AreEqual(2, CountPillarInstances(_root),
                "deben instanciarse exactamente 2 columnas (NW y SE de tile 4,4)");
        }

        [Test]
        public void BuildFromWallsWithoutPillarBitsInstantiatesNoPillars()
        {
            var prefabs = GridPrefabSet.LoadFromResources();
            var walls = EmptyWalls(); // sin ningún bit de pilar en ningún tile

            _root = GridChunkBuilder.BuildFromWalls(walls, prefabs, Vector3.zero, "TestChunkNoPillars", 0, 1);

            Assert.AreEqual(0, CountPillarInstances(_root),
                "sin bits de pilar, ninguna columna debe instanciarse (regresión de la ruta pre-existente)");
        }

        /// <summary>
        /// Guarda de null (Tarea 2, punto 2): un chunk con bits de pilar pero SIN
        /// el prefab cargado (simulando el Resources ausente/no re-ejecutado) no
        /// debe lanzar NullReferenceException — solo omite las columnas.
        /// </summary>
        [Test]
        public void BuildFromWallsWithMissingPillarPrefabDoesNotThrow()
        {
            // Esta rama emite un Debug.LogError DELIBERADO (GridChunkBuilder.cs:393-395,
            // fail-loud: "ejecuta Backrooms/Create Grid Prefabs"), y el Test Framework tumba
            // cualquier test con un Error no declarado. El test moría ahí, no en su assert.
            //
            // Se ignora en vez de declararlo con LogAssert.Expect, al revés que en
            // NetworkInitializerTests, y la diferencia importa: aquel LogError sale en CADA
            // llamada, así que Expect lo fija como contrato. Éste está guardado por el estático
            // _loggedMissingPillarPrefab (GridChunkBuilder.cs:485, SIN reset), que solo deja
            // loguear la PRIMERA vez en todo el dominio — un Expect fallaría con "expected but
            // not received" en cuanto cualquier otra cosa dispare la rama antes, o al re-correr
            // la suite sin recarga de dominio. Depender de eso sería un test intermitente.
            //
            // Lo que este test comprueba de verdad — que no lanza y que no instancia columnas —
            // sigue intacto en los dos asserts de abajo.
            LogAssert.ignoreFailingMessages = true;

            var prefabs = GridPrefabSet.LoadFromResources();
            prefabs.pillar = null; // simula el prefab ausente

            var walls = EmptyWalls();
            walls[4, 4] = GridChunkBuilder.PillarNW;

            Assert.DoesNotThrow(() =>
            {
                _root = GridChunkBuilder.BuildFromWalls(walls, prefabs, Vector3.zero, "TestChunkMissingPrefab", 0, 1);
            });
            Assert.AreEqual(0, CountPillarInstances(_root),
                "sin prefab, cero columnas — pero el build entero no debe reventar");
        }
    }
}
