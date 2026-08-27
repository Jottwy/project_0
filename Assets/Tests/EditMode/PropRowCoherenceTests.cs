using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-036 enmienda 1 - las dos banderas opcionales de <c>ZonePropSet</c>.
    ///
    /// Lo que de verdad hay que clavar aqui no es que las banderas funcionen: es que
    /// APAGADAS no cambien nada. Son opt-in precisamente para no tocar lo que ya estaba
    /// validado, y un test que solo mire el camino encendido dejaria pasar una regresion en
    /// las cuatro capas y las trece zonas que no las llevan.
    /// </summary>
    [TestFixture]
    public class PropRowCoherenceTests
    {
        private const int Tiles = GridChunkDataMsg.Tiles;
        private const int ZoneOffice = 12;
        private const byte WallN = 1 << 0;

        private GameObject _root;
        private LayerVisualConfig _cfg;
        private readonly List<GameObject> _prefabs = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            ZoneRegistry.ResetForNewSession();
            ZoneRegistry.SetZone_EditorTestsOnly(0, 0, ZoneOffice);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_cfg != null) Object.DestroyImmediate(_cfg);
            foreach (var p in _prefabs) if (p != null) Object.DestroyImmediate(p);
            _prefabs.Clear();
            _root = null;
            _cfg = null;
            ZoneRegistry.ResetForNewSession();
        }

        /// Catalogo de DOS entradas distinguibles por nombre, para poder leer CUAL salio.
        private LayerVisualConfig OfficeConfig(bool rowCoherent, bool wallOnly)
        {
            _cfg = ScriptableObject.CreateInstance<LayerVisualConfig>();
            _cfg.showCeiling = false;
            _cfg.ceilingPipes = false;
            _cfg.wallPanelVariety = 0f;
            _cfg.lintelChance = 0f;
            _cfg.propDensity = 1f;      // todos los tiles elegibles
            _cfg.propClusterBias = 0f;
            _cfg.props = new[] { Entry("StubFallback") };
            _cfg.zonePropSets = new[]
            {
                new ZonePropSet
                {
                    zoneKind = ZoneOffice,
                    anyZoneKind = false,
                    props = new[] { Entry("StubA"), Entry("StubB") },
                    densityScale = 1f,
                    maxPropsPerChunk = 999,
                    propsPerTile = 1,
                    rowCoherentProps = rowCoherent,
                    wallOnlyProps = wallOnly,
                },
            };
            return _cfg;
        }

        private PropEntry Entry(string name)
        {
            var go = new GameObject(name);
            _prefabs.Add(go);
            return new PropEntry
            {
                prefab = go,
                placeholderType = "",
                spawnWeight = 1f,
                canBeRotated = false,   // wall-aligned
                floorOnly = true,
            };
        }

        private static byte[,] EmptyWalls() => new byte[Tiles, Tiles];

        /// Una pared NORTE en toda la fila tz: la hilera que el ADR quiere ver.
        private static byte[,] NorthWallRow(int tz)
        {
            var w = new byte[Tiles, Tiles];
            for (int tx = 0; tx < Tiles; tx++) w[tx, tz] = WallN;
            return w;
        }

        private GameObject Build(byte[,] walls, LayerVisualConfig cfg) =>
            GridChunkBuilder.BuildFromWalls(walls, GridPrefabSet.LoadFromResources(),
                Vector3.zero, "PropRowChunk", 0, 2, cfg, new LayerVisualMaterials(), 0, 0, null);

        /// Que entrada salio en cada tile, indexado por el tile que contiene su posicion.
        private static Dictionary<Vector2Int, string> ChosenByTile(GameObject root)
        {
            var map = new Dictionary<Vector2Int, string>();
            foreach (Transform c in root.transform)
            {
                if (!c.name.StartsWith("Stub")) continue;
                var key = new Vector2Int(
                    Mathf.FloorToInt(c.localPosition.x / 5f),
                    Mathf.FloorToInt(c.localPosition.z / 5f));
                map[key] = c.name.Replace("(Clone)", "");
            }
            return map;
        }

        // -- (a) apagadas: la eleccion NO mira las paredes ------------------------------

        [Test]
        public void FlagsOffChoiceIgnoresWalls()
        {
            _root = Build(EmptyWalls(), OfficeConfig(false, false));
            var sinParedes = ChosenByTile(_root);
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_cfg);

            _root = Build(NorthWallRow(2), OfficeConfig(false, false));
            var conPared = ChosenByTile(_root);

            Assert.IsNotEmpty(sinParedes, "el escenario no coloco ni un prop");
            foreach (var kv in sinParedes)
            {
                Assert.IsTrue(conPared.ContainsKey(kv.Key), "falta prop en " + kv.Key);
                Assert.AreEqual(kv.Value, conPared[kv.Key],
                    "con las banderas apagadas la eleccion pasa a depender de las paredes en " +
                    kv.Key + " - esa es la regresion que la enmienda promete NO causar");
            }
        }

        // -- (b) rowCoherent: la fila entera saca el mismo mueble -----------------------

        [Test]
        public void RowCoherentGivesOneEntryPerWallRow()
        {
            _root = Build(NorthWallRow(2), OfficeConfig(true, false));
            var map = ChosenByTile(_root);

            string first = null;
            int seen = 0;
            for (int tx = 0; tx < Tiles; tx++)
            {
                var key = new Vector2Int(tx, 2);
                if (!map.TryGetValue(key, out var name)) continue;
                seen++;
                if (first == null) first = name;
                Assert.AreEqual(first, name,
                    "la fila apoyada en la misma pared saca muebles distintos en " + key);
            }
            Assert.GreaterOrEqual(seen, 3, "la fila no tiene props suficientes para medir nada");
        }

        [Test]
        public void RowCoherentStillPerTileWhereThereIsNoWall()
        {
            _root = Build(EmptyWalls(), OfficeConfig(true, false));
            var conBandera = ChosenByTile(_root);
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_cfg);

            _root = Build(EmptyWalls(), OfficeConfig(false, false));
            var sinBandera = ChosenByTile(_root);

            Assert.IsNotEmpty(sinBandera);
            CollectionAssert.AreEquivalent(sinBandera, conBandera,
                "sin una sola pared, rowCoherent no tiene fila sobre la que hashear y debe " +
                "caer al comportamiento de siempre");
        }

        // -- (c) wallOnly: sin pared, sin mueble ----------------------------------------

        [Test]
        public void WallOnlyLeavesTilesWithoutWallsEmpty()
        {
            _root = Build(NorthWallRow(2), OfficeConfig(false, true));
            var map = ChosenByTile(_root);

            Assert.IsNotEmpty(map, "wallOnly dejo el chunk entero vacio - la fila si tiene pared");
            foreach (var kv in map)
                Assert.AreEqual(2, kv.Key.y, "hay un prop en un tile sin pared: " + kv.Key);
        }

        // -- ADR-036 enm. 2: props de pared y de techo -------------------------------

        private const float TileSize = 5f;
        private const float CeilingY = 4f;   // GridConstants.LayerHeight

        private LayerVisualConfig MountedConfig(PropEntry[] wall, float wallD,
            PropEntry[] ceil, float ceilD)
        {
            var cfg = OfficeConfig(false, false);
            var set = cfg.zonePropSets[0];
            set.wallProps = wall;
            set.wallPropDensity = wallD;
            set.ceilingProps = ceil;
            set.ceilingPropDensity = ceilD;
            cfg.zonePropSets[0] = set;
            return cfg;
        }

        private PropEntry Mounted(string name, float height)
        {
            var e = Entry(name);
            e.mountHeight = height;
            e.floorOnly = false;
            return e;
        }

        private static List<Transform> Named(GameObject root, string prefix)
        {
            var list = new List<Transform>();
            foreach (Transform c in root.transform)
                if (c.name.StartsWith(prefix)) list.Add(c);
            return list;
        }

        [Test]
        public void MountedPropsAreInertWithEmptyCatalogues()
        {
            _root = Build(NorthWallRow(2), MountedConfig(null, 1f, null, 1f));
            Assert.IsEmpty(Named(_root, "StubWall"));
            Assert.IsEmpty(Named(_root, "StubCeil"));
        }

        [Test]
        public void WallPropHangsOnAWalledSideAtItsHeight()
        {
            var cfg = MountedConfig(new[] { Mounted("StubWall", 2.2f) }, 1f, null, 0f);
            _root = Build(NorthWallRow(2), cfg);
            var hung = Named(_root, "StubWall");
            Assert.IsNotEmpty(hung, "no se colgo ni un prop de pared");
            foreach (var h in hung)
            {
                Assert.AreEqual(2.2f, h.localPosition.y, 0.01f,
                    "el prop de pared no respeta su mountHeight");
                int tz = Mathf.FloorToInt(h.localPosition.z / TileSize);
                Assert.AreEqual(2, tz,
                    "hay un prop de pared en una fila sin pared: " + h.localPosition);
                // La pared es la NORTE (-Z), asi que el prop se pega a ese plano.
                float insideTile = h.localPosition.z - tz * TileSize;
                Assert.Less(insideTile, 0.5f,
                    "el prop no esta pegado al plano de su pared, sino suelto en el tile");
            }
        }

        [Test]
        public void NoWallPropWhereThereIsNoWall()
        {
            var cfg = MountedConfig(new[] { Mounted("StubWall", 2f) }, 1f, null, 0f);
            _root = Build(EmptyWalls(), cfg);
            Assert.IsEmpty(Named(_root, "StubWall"),
                "se colgo algo de una pared que no existe");
        }

        [Test]
        public void CeilingPropHangsFromTheCeiling()
        {
            var cfg = MountedConfig(null, 0f, new[] { Mounted("StubCeil", 0.4f) }, 1f);
            _root = Build(EmptyWalls(), cfg);
            var hung = Named(_root, "StubCeil");
            Assert.IsNotEmpty(hung, "no se colgo ni un prop de techo");
            foreach (var h in hung)
                Assert.AreEqual(CeilingY - 0.4f, h.localPosition.y, 0.01f,
                    "el prop de techo no cuelga a la altura pedida");
        }
    }
}
