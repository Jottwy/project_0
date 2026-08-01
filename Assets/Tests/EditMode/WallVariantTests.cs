using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-035 — variantes de MODELO de pared por (zone_kind, RoomType, tile).
    /// Cliente puro: el wire no cambia (los bits 0x0F de <c>walls[tx,tz]</c> siguen
    /// diciendo "aquí hay pared"), solo cambia CON QUÉ prefab la dibuja el cliente.
    ///
    /// El invariante que más importa es el primero: sin ningún set autorizado —la
    /// situación de hoy, sin modelos importados— el render debe ser exactamente el de
    /// antes de este ADR. El resto cubre la selección (comodines, pesos, degradación) y
    /// la conversión de escala celda↔tile, que es donde un off-by-one silencioso
    /// (consultar <c>tx</c> en vez de <c>2·tx</c>) pintaría la variante en el cuadrante
    /// noroeste del chunk sin lanzar ningún error.
    /// </summary>
    [TestFixture]
    public class WallVariantTests
    {
        private const int Tiles = GridChunkDataMsg.Tiles;
        private const byte BitS = 2; // backend S (+Z) → panel +z del tile
        private const byte BitE = 4; // backend E (+X) → panel +x del tile

        private GameObject _root;
        private GameObject _root2;
        private LayerVisualConfig _cfg;
        private readonly List<GameObject> _stubPrefabs = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_root2 != null) Object.DestroyImmediate(_root2);
            if (_cfg != null) Object.DestroyImmediate(_cfg);
            foreach (var p in _stubPrefabs)
                if (p != null) Object.DestroyImmediate(p);
            _stubPrefabs.Clear();
            _root = null;
            _root2 = null;
            _cfg = null;
        }

        private static byte[,] EmptyWalls() => new byte[Tiles, Tiles];

        /// <summary>
        /// Config styled mínima, sin sets de variantes salvo que el test los ponga.
        /// Materiales null a propósito (Paint los ignora), igual que WallVarietyTests.
        /// </summary>
        private LayerVisualConfig StyledConfig()
        {
            _cfg = ScriptableObject.CreateInstance<LayerVisualConfig>();
            _cfg.showCeiling = false;
            _cfg.ceilingPipes = false;
            _cfg.props = null;
            _cfg.wallPanelVariety = 0f; // sin knee walls: la escala no enturbia el conteo
            _cfg.lintelChance = 0f;     // sin dinteles: no aparecen hijos "Lintel"
            return _cfg;
        }

        private static LayerVisualMaterials NullMaterials() => new LayerVisualMaterials();

        /// <summary>
        /// "Prefab" de prueba: un GameObject de escena con MeshRenderer, suficiente para
        /// que Instantiate/AddColliderIfMissing/Paint se comporten como con uno de disco.
        /// Se destruye en TearDown.
        /// </summary>
        private GameObject StubPrefab(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            _stubPrefabs.Add(go);
            return go;
        }

        private static WallVariantSet AnyZoneSet(RoomZoneKind roomType, params WallVariant[] variants) =>
            new WallVariantSet
            {
                anyZoneKind = true,
                roomType = roomType,
                anyRoomType = false,
                variants = variants,
            };

        private static WallVariant Variant(GameObject prefab, float weight) =>
            new WallVariant { prefab = prefab, weight = weight };

        /// <summary>layerIndex 0 de 2 ⇒ no es la capa superior ⇒ sin losa de techo extra.</summary>
        private static GameObject Build(byte[,] walls, LayerVisualConfig cfg, string name,
            RoomZoneMsg[] roomZones) =>
            GridChunkBuilder.BuildFromWalls(walls, GridPrefabSet.LoadFromResources(),
                Vector3.zero, name, 0, 2, cfg, NullMaterials(), 0, 0, roomZones);

        /// <summary>Nombres de los hijos directos que son paneles instanciados.</summary>
        private static List<string> PanelNames(GameObject root)
        {
            var list = new List<string>();
            foreach (Transform c in root.transform)
                if (c.name.StartsWith("Wall") || c.name.StartsWith("Variant"))
                    list.Add(c.name);
            return list;
        }

        private static RoomZoneMsg Zone(byte x0, byte z0, byte x1, byte z1, RoomZoneKind kind) =>
            new RoomZoneMsg { x0 = x0, z0 = z0, x1 = x1, z1 = z1, kindByte = (byte)kind };

        // ── 1. Regresión cero — el invariante que gobierna toda la pieza ─────────

        /// <summary>
        /// Sin sets autorizados (estado de hoy), un chunk CON room_zones renderiza
        /// exactamente los mismos paneles que uno sin ellas: mismo número, mismos
        /// nombres, mismo orden. Si esto falla, la pieza cambió el mundo antes de que
        /// exista un solo modelo nuevo.
        /// </summary>
        [Test]
        public void NoVariantSetsAuthored_RendersExactlyTheStockPanels()
        {
            var walls = EmptyWalls();
            walls[3, 3] = BitS;
            walls[4, 4] = BitE;
            walls[5, 5] = (byte)(BitS | BitE);

            var zones = new[]
            {
                Zone(0, 0, 12, 12, RoomZoneKind.SealedRoom),
                Zone(12, 12, 18, 18, RoomZoneKind.CorridorSpine),
            };

            _root = Build(walls, StyledConfig(), "WithZones", zones);
            var withZones = PanelNames(_root);

            _root2 = Build(walls, _cfg, "NoZones", null);
            var withoutZones = PanelNames(_root2);

            Assert.AreEqual(4, withZones.Count, "se esperan 4 paneles (1 + 1 + 2)");
            CollectionAssert.AreEqual(withoutZones, withZones,
                "sin sets autorizados, room_zones no debe cambiar NADA del render");
            foreach (var n in withZones)
                Assert.IsTrue(n.StartsWith("Wall"), $"panel inesperado: {n}");
        }

        /// <summary>Sin sets, el selector devuelve null ⇒ el llamador usa prefabs.wall.</summary>
        [Test]
        public void WallPrefabFor_ReturnsNullWhenNothingAuthored()
        {
            var cfg = StyledConfig();

            Assert.IsNull(cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.5f), "sets null");

            cfg.wallVariantSets = new WallVariantSet[0];
            Assert.IsNull(cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.5f), "sets vacío");

            cfg.wallVariantSets = new[] { AnyZoneSet(RoomZoneKind.Open) }; // variants vacío
            Assert.IsNull(cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.5f), "set sin variantes");

            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.Open, Variant(StubPrefab("VariantA"), 0f)),
            };
            Assert.IsNull(cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.5f),
                "todos los pesos a 0 ⇒ nada que elegir");

            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.Open, Variant(StubPrefab("VariantNaN"), float.NaN)),
            };
            Assert.IsNull(cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.5f),
                "un peso NaN no debe colarse como 'sí hay algo que elegir'");
        }

        /// <summary>
        /// h == 1f exacto — Hash01 lo devuelve cuando sus 24 bits bajos son todos 1 — cae
        /// en la última variante CON PESO, no en la última del array. Una variante de peso
        /// 0 no debe salir nunca, ni siquiera por el fall-through.
        /// </summary>
        [Test]
        public void ZeroWeightVariantNeverWinsNotEvenOnTheFallThrough()
        {
            var real = StubPrefab("VariantReal");
            var muted = StubPrefab("VariantMuted");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.Open, Variant(real, 1f), Variant(muted, 0f)),
            };

            Assert.AreSame(real, cfg.WallPrefabFor(0, RoomZoneKind.Open, 1f), "h = 1 exacto");
            Assert.AreSame(real, cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.999999f));
            Assert.AreSame(real, cfg.WallPrefabFor(0, RoomZoneKind.Open, 0f));
        }

        /// <summary>
        /// Ruta UNSTYLED (cfg null): sin config no hay sets, así que sale el panel de
        /// siempre. Cubre PlaceWalls, que tras esta pieza también pasa por
        /// ResolveWallPrefab y se quedaría sin ejercitar de otro modo.
        /// </summary>
        [Test]
        public void UnstyledPathStillUsesTheStockPanel()
        {
            var walls = EmptyWalls();
            walls[2, 2] = (byte)(BitS | BitE);

            var zones = new[] { Zone(0, 0, 12, 12, RoomZoneKind.SealedRoom) };
            _root = GridChunkBuilder.BuildFromWalls(walls, GridPrefabSet.LoadFromResources(),
                Vector3.zero, "Unstyled", 0, 2, null, null, 0, 0, zones);

            var names = PanelNames(_root);
            Assert.AreEqual(2, names.Count);
            foreach (var n in names)
                Assert.AreEqual("Wall(Clone)", n, "sin cfg no hay variantes que aplicar");
        }

        // ── 2. Selección por (zone_kind, RoomType) ──────────────────────────────

        [Test]
        public void Matches_HonoursWildcardsAndExactValues()
        {
            var exact = new WallVariantSet { zoneKind = 5, roomType = RoomZoneKind.SealedRoom };
            Assert.IsTrue(exact.Matches(5, RoomZoneKind.SealedRoom));
            Assert.IsFalse(exact.Matches(4, RoomZoneKind.SealedRoom), "otro zone_kind");
            Assert.IsFalse(exact.Matches(5, RoomZoneKind.Open), "otro RoomType");

            var anyZone = new WallVariantSet { anyZoneKind = true, roomType = RoomZoneKind.CorridorSpine };
            Assert.IsTrue(anyZone.Matches(0, RoomZoneKind.CorridorSpine));
            Assert.IsTrue(anyZone.Matches(11, RoomZoneKind.CorridorSpine));
            Assert.IsFalse(anyZone.Matches(0, RoomZoneKind.Open));

            var anyRoom = new WallVariantSet { zoneKind = 5, anyRoomType = true };
            Assert.IsTrue(anyRoom.Matches(5, RoomZoneKind.Open));
            Assert.IsTrue(anyRoom.Matches(5, RoomZoneKind.SealedRoom));
            Assert.IsFalse(anyRoom.Matches(6, RoomZoneKind.Open));
        }

        /// <summary>
        /// El default del Inspector (todo a cero) es un set de ZONE_NORMAL + Open, NO un
        /// comodín: los comodines son booleanos explícitos justamente para que ese default
        /// se lea en la UI en vez de aplicarse en silencio a una sola zona.
        /// </summary>
        [Test]
        public void DefaultConstructedSetIsSpecificNotWildcard()
        {
            var fresh = new WallVariantSet();
            Assert.IsTrue(fresh.Matches(0, RoomZoneKind.Open), "0/Open: ZONE_NORMAL abierto");
            Assert.IsFalse(fresh.Matches(1, RoomZoneKind.Open), "no debe aplicar a otra zona");
            Assert.IsFalse(fresh.Matches(0, RoomZoneKind.SealedRoom), "ni a otro RoomType");
        }

        /// <summary>
        /// Zona aún desconocida (ZoneRegistry sin responder) llega como −1: no casa con
        /// ningún set específico, sí con uno marcado anyZoneKind.
        /// </summary>
        [Test]
        public void UnknownZoneOnlyMatchesWildcardSets()
        {
            var specific = new WallVariantSet { zoneKind = 5, roomType = RoomZoneKind.Open };
            Assert.IsFalse(specific.Matches(-1, RoomZoneKind.Open));

            var wildcard = new WallVariantSet { anyZoneKind = true, roomType = RoomZoneKind.Open };
            Assert.IsTrue(wildcard.Matches(-1, RoomZoneKind.Open));
        }

        /// <summary>
        /// End-to-end de la degradación por zona desconocida: en un test EditMode
        /// ZoneRegistry nunca se puebla, así que BuildFromWalls pasa −1 y un set ESPECÍFICO
        /// de zona no debe aplicarse — el chunk sale con el panel de siempre en vez de
        /// hornear el modelo de una zona que no se sabe si es la suya.
        /// </summary>
        [Test]
        public void ZoneSpecificSetDoesNotApplyWhenZoneIsUnknown()
        {
            var model = StubPrefab("VariantZoned");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                new WallVariantSet { zoneKind = 5, anyRoomType = true, variants = new[] { Variant(model, 1f) } },
            };

            var walls = EmptyWalls();
            walls[3, 3] = BitS;
            _root = Build(walls, cfg, "UnknownZone", null);

            CollectionAssert.AreEqual(new[] { "Wall(Clone)" }, PanelNames(_root),
                "zona desconocida (−1) no debe casar con un set de zone_kind 5");
        }

        /// <summary>Gana el PRIMER set que case, no el más específico.</summary>
        [Test]
        public void FirstMatchingSetWins()
        {
            var first = StubPrefab("VariantFirst");
            var second = StubPrefab("VariantSecond");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                new WallVariantSet { anyZoneKind = true, anyRoomType = true, variants = new[] { Variant(first, 1f) } },
                new WallVariantSet { zoneKind = 5, roomType = RoomZoneKind.SealedRoom, variants = new[] { Variant(second, 1f) } },
            };

            Assert.AreSame(first, cfg.WallPrefabFor(5, RoomZoneKind.SealedRoom, 0.5f),
                "el comodín va primero en la lista, así que gana él");
        }

        // ── 3. Pesos ────────────────────────────────────────────────────────────

        /// <summary>
        /// Reparto acumulado por peso, misma forma que PickProp: con pesos 1 y 3 el
        /// primer cuarto de [0,1) cae en A y el resto en B. Se comprueban los bordes
        /// exactos, no una muestra aleatoria.
        /// </summary>
        [Test]
        public void WeightedPickSplitsTheHashRangeProportionally()
        {
            var a = StubPrefab("VariantA");
            var b = StubPrefab("VariantB");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.Open, Variant(a, 1f), Variant(b, 3f)),
            };

            Assert.AreSame(a, cfg.WallPrefabFor(0, RoomZoneKind.Open, 0f), "h=0 ⇒ primera");
            Assert.AreSame(a, cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.24f), "justo por debajo del corte");
            Assert.AreSame(b, cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.25f), "en el corte ⇒ segunda");
            Assert.AreSame(b, cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.99f), "cola ⇒ segunda");
        }

        /// <summary>
        /// Una variante con prefab null significa "el panel de siempre": permite autorar
        /// "70 % pared de stock, 30 % modelo nuevo" sin referenciar Wall.prefab.
        /// </summary>
        [Test]
        public void NullPrefabInsideAVariantMeansTheStockPanel()
        {
            var newModel = StubPrefab("VariantNew");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.Open, Variant(null, 1f), Variant(newModel, 1f)),
            };

            Assert.IsNull(cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.1f), "primera mitad ⇒ stock");
            Assert.AreSame(newModel, cfg.WallPrefabFor(0, RoomZoneKind.Open, 0.9f));
        }

        // ── 4. Escala celda ↔ tile ──────────────────────────────────────────────

        /// <summary>
        /// RoomZoneMsg habla en CELDAS de 2.5 m; el builder itera TILES de 5 m. Una zona
        /// de celdas [0,8)×[0,8) cubre los tiles 0..3 y NO el 4. Si alguien consultara
        /// <c>tx</c> en vez de <c>2·tx</c>, el tile 4..7 seguiría dando "dentro" y el
        /// bug sería invisible — por eso el assert del tile 4 es el que importa.
        /// </summary>
        [Test]
        public void RoomTypeForTileConvertsTileCoordsToCells()
        {
            var zones = new[] { Zone(0, 0, 8, 8, RoomZoneKind.SealedRoom) };

            for (int t = 0; t <= 3; t++)
                Assert.AreEqual(RoomZoneKind.SealedRoom, GridChunkBuilder.RoomTypeForTile(zones, t, t),
                    $"tile {t} (celda {2 * t}) debería caer dentro de [0,8)");

            Assert.AreEqual(RoomZoneKind.Open, GridChunkBuilder.RoomTypeForTile(zones, 4, 4),
                "tile 4 = celda 8, fuera del rect: si esto dice SealedRoom, se está " +
                "consultando el índice de tile como si fuera de celda");
            Assert.AreEqual(RoomZoneKind.Open, GridChunkBuilder.RoomTypeForTile(zones, 0, 4),
                "dentro en X pero fuera en Z");
        }

        /// <summary>Sin zonas (backend anterior a ADR-034, o chunk sin ellas) ⇒ Open.</summary>
        [Test]
        public void RoomTypeForTileFallsBackToOpen()
        {
            Assert.AreEqual(RoomZoneKind.Open, GridChunkBuilder.RoomTypeForTile(null, 0, 0));
            Assert.AreEqual(RoomZoneKind.Open, GridChunkBuilder.RoomTypeForTile(new RoomZoneMsg[0], 0, 0));
            Assert.AreEqual(RoomZoneKind.Open,
                GridChunkBuilder.RoomTypeForTile(new[] { Zone(6, 6, 10, 10, RoomZoneKind.CorridorSpine) }, 0, 0),
                "tile no cubierto por ninguna zona");
        }

        /// <summary>
        /// REGRESIÓN del bug que encontró la auditoría de esta pieza: un panel vive en la
        /// FRONTERA entre dos tiles, y por la regla de no-duplicación cada tile emite solo
        /// sus paneles +Z y +X. El muro OESTE de una sala lo instancia el tile de FUERA
        /// (txMin−1, como su panel +X) y el NORTE el tile tzMin−1. Resolviendo por tile
        /// propietario, esas dos paredes salían con el RoomType de fuera y la sala se
        /// renderizaba con 2 lados del modelo nuevo y 2 del de siempre.
        ///
        /// Zona de celdas [4,12)×[4,12) ⇒ tiles 2..5. El panel +X del tile 1 ES el muro
        /// oeste de la sala; el panel +X del tile 5, el muro este. Ambos deben decir
        /// SealedRoom. El panel +X del tile 0 (interior del maze, sin sala a ningún lado)
        /// debe seguir diciendo Open — si RoomTypeForPanel se limitara a mirar al vecino
        /// sin condición, contagiaría el tipo a paredes que no son de la sala.
        /// </summary>
        [Test]
        public void RoomTypeForPanelClaimsTheWallsEmittedByTheTileOutside()
        {
            var zones = new[] { Zone(4, 4, 12, 12, RoomZoneKind.SealedRoom) };
            // Se referencian las constantes del builder en vez de redeclararlas: los literales
            // que había aquí (1 y 4) son EdgeSouth y EdgeWest, no EdgeNorth y EdgeEast. Con el
            // flag equivocado, RoomTypeForPanel cae en su rama "no desplaza al vecino" y devuelve
            // el tile propietario, así que el test fallaba por su propia transcripción. Ojo al
            // matiz: 1 y 4 SÍ son norte y este en los bits del BACKEND (ver BitS/BitE arriba) —
            // la traducción backend→interno la hace BuildFromWalls, y RoomTypeForPanel recibe ya
            // el flag interno.
            const byte edgeNorth = GridChunkBuilder.EdgeNorth; // panel +Z
            const byte edgeEast = GridChunkBuilder.EdgeEast;   // panel +X

            Assert.AreEqual(RoomZoneKind.SealedRoom,
                GridChunkBuilder.RoomTypeForPanel(zones, 1, 3, edgeEast),
                "muro OESTE: lo emite el tile 1, que está fuera de la sala");
            Assert.AreEqual(RoomZoneKind.SealedRoom,
                GridChunkBuilder.RoomTypeForPanel(zones, 3, 1, edgeNorth),
                "muro NORTE: lo emite el tile de z=1, fuera de la sala");

            Assert.AreEqual(RoomZoneKind.SealedRoom,
                GridChunkBuilder.RoomTypeForPanel(zones, 5, 3, edgeEast),
                "muro ESTE: emisor dentro, ya funcionaba");
            Assert.AreEqual(RoomZoneKind.SealedRoom,
                GridChunkBuilder.RoomTypeForPanel(zones, 3, 5, edgeNorth),
                "muro SUR: emisor dentro, ya funcionaba");

            Assert.AreEqual(RoomZoneKind.Open,
                GridChunkBuilder.RoomTypeForPanel(zones, 0, 3, edgeEast),
                "un panel con maze a ambos lados no debe heredar el tipo de la sala");
            Assert.AreEqual(RoomZoneKind.Open,
                GridChunkBuilder.RoomTypeForPanel(zones, 8, 8, edgeEast),
                "lejos de la sala, Open");
        }

        /// <summary>
        /// El mismo caso, pero a través del render completo: los cuatro muros de la sala
        /// tienen que salir con el modelo autorizado, no dos de cuatro.
        /// </summary>
        [Test]
        public void AllFourWallsOfASealedRoomUseTheAuthoredModel()
        {
            var model = StubPrefab("VariantSealed");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.SealedRoom, Variant(model, 1f)),
            };

            // Zona de celdas [4,12)×[4,12) ⇒ tiles 2..5. Los 4 paneles del perímetro en la
            // fila/columna 3, uno por lado, con sus emisores reales.
            var walls = EmptyWalls();
            walls[1, 3] = BitE; // muro oeste  (emisor fuera)
            walls[3, 1] = BitS; // muro norte  (emisor fuera)
            walls[5, 3] = BitE; // muro este   (emisor dentro)
            walls[3, 5] = BitS; // muro sur    (emisor dentro)

            var zones = new[] { Zone(4, 4, 12, 12, RoomZoneKind.SealedRoom) };
            _root = Build(walls, cfg, "SealedPerimeter", zones);

            var names = PanelNames(_root);
            Assert.AreEqual(4, names.Count, "cuatro paneles esperados");
            foreach (var n in names)
                Assert.AreEqual("VariantSealed(Clone)", n,
                    "los 4 muros del perímetro deben usar el modelo de la sala, " +
                    "incluidos el oeste y el norte que emite el tile de fuera");
        }

        /// <summary>Primera zona que cubre el tile gana (orden de estampado).</summary>
        [Test]
        public void RoomTypeForTileTakesTheFirstCoveringZone()
        {
            var zones = new[]
            {
                Zone(0, 0, 8, 8, RoomZoneKind.CorridorSpine),
                Zone(0, 0, 8, 8, RoomZoneKind.SealedRoom),
            };
            Assert.AreEqual(RoomZoneKind.CorridorSpine, GridChunkBuilder.RoomTypeForTile(zones, 1, 1));
        }

        // ── 5. End-to-end: el modelo elegido llega al render ─────────────────────

        /// <summary>
        /// Con un set comodín de una sola variante, TODOS los paneles del chunk salen de
        /// ese prefab en vez de Wall.prefab. Cierra la cadena entera:
        /// ChunkStreamer → BuildFromWalls → RoomTypeForTile → WallPrefabFor → Instantiate.
        /// </summary>
        [Test]
        public void AuthoredVariantReplacesTheStockPanelInTheRender()
        {
            var model = StubPrefab("VariantSealed");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.SealedRoom, Variant(model, 1f)),
            };

            var walls = EmptyWalls();
            walls[1, 1] = BitS; // tile 1 → celdas 2..3, dentro de la zona de abajo
            walls[8, 8] = BitS; // tile 8 → celdas 16..17, fuera

            var zones = new[] { Zone(0, 0, 8, 8, RoomZoneKind.SealedRoom) };
            _root = Build(walls, cfg, "Mixed", zones);

            var names = PanelNames(_root);
            Assert.AreEqual(2, names.Count, "dos paneles esperados");
            CollectionAssert.Contains(names, "VariantSealed(Clone)",
                "el tile dentro de la SealedRoom debe usar el modelo autorizado");
            CollectionAssert.Contains(names, "Wall(Clone)",
                "el tile fuera de la zona debe seguir con el panel de siempre");
        }

        /// <summary>
        /// Mismo tile ⇒ misma variante entre dos construcciones. El hash es puro sobre
        /// coords GLOBALES de tile, sin rng: reconstruir un chunk al revisitarlo no puede
        /// cambiar qué modelo salió.
        /// </summary>
        [Test]
        public void VariantChoiceIsDeterministicAcrossRebuilds()
        {
            var a = StubPrefab("VariantA");
            var b = StubPrefab("VariantB");
            var cfg = StyledConfig();
            cfg.wallVariantSets = new[]
            {
                AnyZoneSet(RoomZoneKind.Open, Variant(a, 1f), Variant(b, 1f)),
            };

            var walls = EmptyWalls();
            for (int t = 0; t < Tiles; t++)
                walls[t, t] = (byte)(BitS | BitE);

            _root = Build(walls, cfg, "First", null);
            _root2 = Build(walls, cfg, "Second", null);

            var first = PanelNames(_root);
            var second = PanelNames(_root2);
            CollectionAssert.AreEqual(first, second, "el hash es puro: misma entrada, misma salida");
            // Y con dos variantes de igual peso sobre 20 paneles, ambas deben salir —
            // si no, el hash no está variando por panel y la pieza no sirve de nada.
            CollectionAssert.Contains(first, "VariantA(Clone)");
            CollectionAssert.Contains(first, "VariantB(Clone)");
        }
    }
}
