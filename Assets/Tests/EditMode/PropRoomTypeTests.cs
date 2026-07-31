using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-036 — densidad de props condicionada por RoomType. Cliente puro: el catálogo
    /// (<c>cfg.props</c>), la selección ponderada, el sesgo de cluster, el pegado a pared
    /// y los salts no cambian; lo único que se toca es el UMBRAL del gate de spawn.
    ///
    /// Los recuentos esperados no son estimaciones: salen de evaluar el mismo
    /// <c>Hash01</c> del builder sobre los 99 tiles elegibles del chunk (el central está
    /// reservado) con <c>propClusterBias = 0</c>, que reduce
    /// <c>Mathf.Lerp(fine, coarse, 0)</c> a <c>fine</c>. Con <c>propDensity = 0.04</c> los
    /// tres casos quedan por debajo de <c>MaxPropsPerChunk</c> (12), así que el recuento
    /// mide el multiplicador y no el tope.
    /// </summary>
    [TestFixture]
    public class PropRoomTypeTests
    {
        private const int Tiles = GridChunkDataMsg.Tiles;

        /// <summary>propDensity del fixture. Ver el doc-comment de la clase.</summary>
        private const float BaseDensity = 0.04f;

        // Recuentos REALES para BaseDensity sobre el chunk (0,0), calculados con Hash01.
        private const int ExpectedOpen = 4;           // baseline, multiplicador 1.0
        private const int ExpectedSealedRoom = 9;     // multiplicador 2.5
        private const int ExpectedCorridorSpine = 1;  // multiplicador 0.15

        private GameObject _root;
        private LayerVisualConfig _cfg;
        private GameObject _propPrefab;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_cfg != null) Object.DestroyImmediate(_cfg);
            if (_propPrefab != null) Object.DestroyImmediate(_propPrefab);
            _root = null;
            _cfg = null;
            _propPrefab = null;
        }

        private static byte[,] EmptyWalls() => new byte[Tiles, Tiles];

        /// <summary>
        /// Config con UN solo prop, con <c>prefab</c> real (un GameObject vacío) en vez de
        /// placeholder: la rama de placeholder llama a <c>Object.Destroy</c> sobre los
        /// colliders de las primitivas, que en EditMode es un error de Unity. Con prefab se
        /// ejercita el mismo gate de densidad sin tocar esa rama.
        ///
        /// <c>canBeRotated = false</c> + <c>floorOnly = true</c> ⇒ el prop se pega a pared
        /// (Pieza E), que es lo que necesita el test de entradas.
        /// </summary>
        private LayerVisualConfig PropConfig(float density)
        {
            _propPrefab = new GameObject("StubProp");
            _cfg = ScriptableObject.CreateInstance<LayerVisualConfig>();
            _cfg.showCeiling = false;
            _cfg.ceilingPipes = false;
            _cfg.wallPanelVariety = 0f;
            _cfg.lintelChance = 0f;
            _cfg.propDensity = density;
            _cfg.propClusterBias = 0f; // Lerp(fine, coarse, 0) == fine → gate predecible
            _cfg.props = new[]
            {
                new PropEntry
                {
                    prefab = _propPrefab,
                    placeholderType = "",   // ni cable ni paper ni stain ⇒ y = PropFloorY
                    spawnWeight = 1f,
                    canBeRotated = false,   // ⇒ wall-aligned
                    floorOnly = true,
                },
            };
            return _cfg;
        }

        private static LayerVisualMaterials NullMaterials() => new LayerVisualMaterials();

        private static GameObject Build(byte[,] walls, LayerVisualConfig cfg, string name,
            RoomZoneMsg[] roomZones) =>
            GridChunkBuilder.BuildFromWalls(walls, GridPrefabSet.LoadFromResources(),
                Vector3.zero, name, 0, 2, cfg, NullMaterials(), 0, 0, roomZones);

        private static List<Transform> Props(GameObject root)
        {
            var list = new List<Transform>();
            foreach (Transform c in root.transform)
                if (c.name.StartsWith("StubProp")) list.Add(c);
            return list;
        }

        private static RoomZoneMsg WholeChunk(RoomZoneKind kind) =>
            new RoomZoneMsg { x0 = 0, z0 = 0, x1 = 20, z1 = 20, kindByte = (byte)kind };

        private static float TileCentreCoord(int t) => (t + 0.5f) * 5f;

        // ── 1. Regresión cero ───────────────────────────────────────────────────

        /// <summary>
        /// Sin <c>room_zones</c> (backend anterior a ADR-034, o chunk sin zonas) el
        /// recuento es el histórico. Es el baseline contra el que se miden los otros dos.
        /// </summary>
        [Test]
        public void NoRoomZones_KeepsTheHistoricPropCount()
        {
            _root = Build(EmptyWalls(), PropConfig(BaseDensity), "NoZones", null);
            Assert.AreEqual(ExpectedOpen, Props(_root).Count,
                "sin zonas el gate de densidad debe quedar exactamente como antes del ADR");
        }

        /// <summary>
        /// Una zona explícita de tipo Open da EL MISMO recuento y LOS MISMOS tiles que no
        /// tener zonas: el multiplicador de Open es 1.0 por definición, así que el camino
        /// nuevo no puede desviarse del viejo ni en qué tiles elige.
        /// </summary>
        [Test]
        public void OpenRoomType_IsIndistinguishableFromNoRoomZones()
        {
            _root = Build(EmptyWalls(), PropConfig(BaseDensity), "OpenZone",
                new[] { WholeChunk(RoomZoneKind.Open) });
            var withOpen = new List<Vector3>();
            foreach (var p in Props(_root)) withOpen.Add(p.localPosition);

            Object.DestroyImmediate(_root);
            _root = Build(EmptyWalls(), _cfg, "NoZones", null);
            var without = new List<Vector3>();
            foreach (var p in Props(_root)) without.Add(p.localPosition);

            Assert.AreEqual(ExpectedOpen, withOpen.Count);
            CollectionAssert.AreEqual(without, withOpen,
                "Open debe elegir exactamente los mismos tiles, no solo la misma cantidad");
        }

        // ── 2. SealedRoom y CorridorSpine ───────────────────────────────────────

        /// <summary>SealedRoom multiplica ×2.5 ⇒ claramente más props que el baseline.</summary>
        [Test]
        public void SealedRoom_PlacesMorePropsThanTheBaseline()
        {
            _root = Build(EmptyWalls(), PropConfig(BaseDensity), "Sealed",
                new[] { WholeChunk(RoomZoneKind.SealedRoom) });

            int count = Props(_root).Count;
            Assert.AreEqual(ExpectedSealedRoom, count);
            Assert.Greater(count, ExpectedOpen, "una sala sellada debe sentirse habitada");
            // El recuento sigue el multiplicador de forma aproximada (el hash no reparte
            // uniforme en muestras pequeñas): 9/4 = 2.25 frente al 2.5 nominal.
            Assert.That(count / (float)ExpectedOpen, Is.EqualTo(2.5f).Within(0.5f),
                "la proporción debe seguir al multiplicador, no dispararse ni quedarse corta");
        }

        /// <summary>
        /// CorridorSpine multiplica ×0.15 ⇒ menos props que el baseline. El valor NO es 0
        /// a propósito (ver ADR-036): un pasillo con un objeto suelto muy de vez en cuando
        /// lee como vacío deliberado; uno literalmente vacío lee como zona sin terminar.
        /// </summary>
        [Test]
        public void CorridorSpine_PlacesFewerPropsThanTheBaseline()
        {
            _root = Build(EmptyWalls(), PropConfig(BaseDensity), "Spine",
                new[] { WholeChunk(RoomZoneKind.CorridorSpine) });

            int count = Props(_root).Count;
            Assert.AreEqual(ExpectedCorridorSpine, count);
            Assert.LessOrEqual(count, ExpectedOpen, "el pasillo debe quedar más vacío");
        }

        /// <summary>
        /// El tipo se aplica POR TILE, no por chunk: una zona que cubre media rejilla deja
        /// la otra media con el comportamiento de siempre.
        /// </summary>
        [Test]
        public void MultiplierAppliesPerTileNotPerChunk()
        {
            // Celdas [0,20)×[0,10) ⇒ tiles con tz 0..4. La mitad sur queda fuera.
            var half = new[]
            {
                new RoomZoneMsg { x0 = 0, z0 = 0, x1 = 20, z1 = 10, kindByte = (byte)RoomZoneKind.SealedRoom },
            };
            _root = Build(EmptyWalls(), PropConfig(BaseDensity), "HalfSealed", half);
            int mixed = Props(_root).Count;

            Assert.Greater(mixed, ExpectedOpen, "la mitad sellada debe aportar props extra");
            Assert.Less(mixed, ExpectedSealedRoom, "pero menos que si el chunk entero lo fuera");
        }

        // ── 3. Entradas: ningún prop se pega a una arista abierta ───────────────

        /// <summary>
        /// REQUISITO EXPLÍCITO de la pieza, verificado en vez de asumido: un prop nunca
        /// debe apoyarse en la arista por la que se ENTRA a la sala (la que el bitmask
        /// marca abierta). Sale gratis del mecanismo existente —el bucle de pegado solo
        /// recorre los bits de pared que están PUESTOS (<c>walls[tx,tz] &amp; 0x0F</c>)— pero
        /// nada lo fijaba.
        ///
        /// Montaje: todos los tiles sellados (0x0F ⇒ inelegibles) salvo (2,3), que tiene
        /// pared N+S+W y la arista E ABIERTA — la entrada. Con densidad 1.0 ese tile es el
        /// único candidato, así que el prop cae ahí con certeza.
        /// </summary>
        [Test]
        public void PropNeverBacksOntoAnOpenEntranceEdge()
        {
            var walls = EmptyWalls();
            for (int tz = 0; tz < Tiles; tz++)
                for (int tx = 0; tx < Tiles; tx++)
                    walls[tx, tz] = 0x0F;
            walls[2, 3] = 0x0B; // N|S|W puestas, E (4) ABIERTA = entrada

            _root = Build(walls, PropConfig(1f), "Entrance",
                new[] { WholeChunk(RoomZoneKind.SealedRoom) });

            var props = Props(_root);
            Assert.AreEqual(1, props.Count, "solo el tile con arista abierta es elegible");

            var pos = props[0].localPosition;
            float cx = TileCentreCoord(2), cz = TileCentreCoord(3);

            // Lo que de verdad se prueba: el prop NO se ha desplazado hacia +X, que es la
            // entrada. Si lo hiciera, quedaría en mitad del vano.
            Assert.LessOrEqual(pos.x, cx + 0.001f,
                "el prop se apoyó en la arista ABIERTA (+X): estaría bloqueando la entrada");

            // Y el lado concreto que elige el hash existente, para que un cambio de salt o
            // de tabla se delate en vez de pasar por bueno: lado S (+Z), retranqueo 1.9.
            Assert.AreEqual(cx, pos.x, 0.001f, "sin desplazamiento en X");
            Assert.AreEqual(cz + 1.9f, pos.z, 0.001f, "pegado a la pared S (+Z)");
        }
    }
}
