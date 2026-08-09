using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ZONE_OFFICE — "escalera a ninguna parte". Dos familias de pruebas, en orden de
    /// importancia:
    ///
    /// 1. CROSS-LAYER CERO. La escalera es decorativa y vive entera dentro de su capa; la
    ///    verticalidad jugable es una pieza aparte con ADR propio (NOTA DE BACKLOG del
    ///    2026-08-08 en docs/DECISIONS.md). El mecanismo por el que se colaría no es
    ///    `require_walkable_above/below` —la pieza no toca el backend— sino la ALTURA: el
    ///    backend reclasifica la capa del jugador con round(pies / LayerHeight), así que
    ///    basta con que alguien pueda ponerse a 2 m del suelo para que el cambio de capa
    ///    ocurra sin que nadie lo haya pedido. Estos tests fijan ese margen.
    ///
    /// 2. ESCALÓN TRANSITABLE DE VERDAD. No una rampa lisa ni una pared con relieve: cada
    ///    contrahuella tiene que caber en el m_StepOffset del CharacterController, y los
    ///    escalones tienen que ser sólidos y contiguos.
    /// </summary>
    [TestFixture]
    public class OfficeStairsTests
    {
        /// <summary>FPS_Player.prefab (Assets/PolymindGames/FPSCore/Prefabs/Core):
        /// m_StepOffset del CharacterController.</summary>
        private const float PlayerStepOffset = 0.275f;

        /// <summary>FPS_Player.prefab: _jumpHeight. Es lo que separa "altura pisable" de
        /// "altura alcanzable", y por eso el rellano no puede subir hasta el umbral de
        /// capa.</summary>
        private const float PlayerJumpHeight = 1.05f;

        /// <summary>FPS_Player.prefab: m_Height del CharacterController.</summary>
        private const float PlayerHeight = 1.8f;

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        private GameObject BuildStairs(float yaw = 0f)
        {
            _root = new GameObject("StairHost");
            return OfficeStairs.Build(_root.transform, null, Color.white, Vector3.zero, yaw);
        }

        private static RoomZoneMsg Zone(byte x0, byte z0, byte x1, byte z1, byte kind = 1) =>
            new RoomZoneMsg { x0 = x0, z0 = z0, x1 = x1, z1 = z1, kindByte = kind };

        private static byte[,] OpenWalls()
        {
            int n = GridChunkBuilder.TilesPerChunk;
            return new byte[n, n]; // todo 0: sin aristas, sin columnas
        }

        // ── 1. CROSS-LAYER CERO ──────────────────────────────────────────────────────

        /// <summary>
        /// Espejo de `f32::round()` de Rust: mitad LEJOS DE CERO.
        ///
        /// NO se puede usar `Mathf.RoundToInt` aquí, que es justo el error que este helper
        /// existe para evitar: delega en `System.Math.Round`, cuyo default es banker's
        /// rounding (mitad al PAR), así que `Mathf.RoundToInt(0.5f) == 0` mientras que en
        /// Rust `0.5f32.round() == 1.0`. Con el umbral cayendo exactamente en 0.5, las dos
        /// convenciones dan respuestas opuestas — y la que manda es la de Rust, porque quien
        /// reclasifica la capa es el backend.
        /// </summary>
        private static int RustRound(float x) => (int)Mathf.Floor(x + 0.5f);

        [Test]
        public void TheLayerFlipThresholdIsDerivedFromLayerHeightNotHardcoded()
        {
            // Espejo de `layer_from_player_y` (backend/src/world/collision.rs):
            // round((y − PLAYER_BASE_Y) / LAYER_HEIGHT), con y = pies + PLAYER_BASE_Y ⇒
            // round(pies / LayerHeight). Cambia de capa en cuanto el cociente redondea a 1.
            Assert.AreEqual(GridConstants.LayerHeight * 0.5f, OfficeStairs.LayerFlipFeetY, 1e-5f,
                "LayerFlipFeetY dejó de seguir a LayerHeight — si la separación entre capas " +
                "cambia, este umbral tiene que cambiar con ella");
            Assert.AreEqual(1, RustRound(OfficeStairs.LayerFlipFeetY / GridConstants.LayerHeight),
                "a LayerFlipFeetY exacto el backend ya reclasifica: el umbral es el primer " +
                "valor que cruza, no el último que no cruza");
            Assert.AreEqual(0, RustRound(
                (OfficeStairs.LayerFlipFeetY - 0.01f) / GridConstants.LayerHeight));
            // Y el ápice de salto desde el rellano, pasado por la MISMA función que usa el
            // backend, sigue resolviendo a la capa 0. Es la comprobación de extremo a
            // extremo: las otras comparan alturas, esta compara CAPAS.
            Assert.AreEqual(0, RustRound(
                (OfficeStairs.LandingY + PlayerJumpHeight) / GridConstants.LayerHeight),
                "el ápice de salto desde el rellano resuelve a una capa distinta de la 0");
        }

        [Test]
        public void StandingOnTheLandingAndJumpingStaysInTheSameLayer()
        {
            // El caso que de verdad importa: no basta con que el rellano esté por debajo del
            // umbral, tiene que estarlo TAMBIÉN el ápice de un salto desde él. Un rellano a
            // 1.0 m pasaría la comprobación ingenua y cambiaría de capa al primer salto.
            float apex = OfficeStairs.LandingY + PlayerJumpHeight;
            Assert.Less(apex, OfficeStairs.LayerFlipFeetY,
                $"saltar desde el rellano ({OfficeStairs.LandingY} m) llega a {apex} m y cruza " +
                $"el umbral de capa ({OfficeStairs.LayerFlipFeetY} m): eso es una interacción " +
                "cross-layer accidental, que es justo lo que esta pieza no puede tener");
            // Y con margen, no al filo: el salto de STP integra velocidad, así que el ápice
            // real puede pasarse unos centímetros del valor nominal.
            Assert.Greater(OfficeStairs.LayerFlipFeetY - apex, 0.2f,
                "el margen contra el umbral de capa es demasiado fino para confiar en él");
        }

        [Test]
        public void NoWalkableSurfaceInThePieceSitsAboveTheLanding()
        {
            var stairs = BuildStairs();
            float highestWalkable = 0f;
            foreach (var col in stairs.GetComponentsInChildren<Collider>())
                highestWalkable = Mathf.Max(highestWalkable, col.bounds.max.y);

            // El rellano ES la superficie pisable más alta de toda la pieza. Si algún día
            // alguien le pone collider al tramo decorativo, este test lo caza antes que el
            // playtest.
            Assert.AreEqual(OfficeStairs.LandingY, highestWalkable, 1e-4f,
                "hay geometría CON COLLIDER por encima del rellano — el tramo superior tiene " +
                "que seguir sin collider, es la segunda barrera del invariante de capa");
            Assert.Less(highestWalkable + PlayerJumpHeight, OfficeStairs.LayerFlipFeetY);
        }

        [Test]
        public void TheGhostFlightIsOutOfJumpReachFromTheLanding()
        {
            float apex = OfficeStairs.LandingY + PlayerJumpHeight;
            Assert.Greater(OfficeStairs.GhostFlightBottomY, apex,
                $"el tramo decorativo arranca a {OfficeStairs.GhostFlightBottomY} m y el salto " +
                $"desde el rellano llega a {apex} m: sería escalable, y escalarlo cruza de capa");
        }

        [Test]
        public void TheGhostFlightCarriesNoColliderAtAll()
        {
            var stairs = BuildStairs();
            foreach (var col in stairs.GetComponentsInChildren<Collider>())
                Assert.Less(col.bounds.max.y, OfficeStairs.GhostFlightBottomY,
                    $"'{col.gameObject.name}' tiene collider dentro del tramo decorativo");
            // Y existe de verdad: sin este assert el test de arriba sería verde con una
            // escalera a la que simplemente le falta el tramo superior.
            bool hasGhost = false;
            foreach (var r in stairs.GetComponentsInChildren<Renderer>())
                if (r.bounds.max.y > OfficeStairs.GhostFlightBottomY) hasGhost = true;
            Assert.IsTrue(hasGhost, "no se construyó ningún tramo decorativo");
        }

        [Test]
        public void TheGhostFlightIsUnclimbableEvenIfSomeoneReachesIt()
        {
            // Defensa en profundidad: aunque un cambio futuro lo bajara al alcance, su
            // contrahuella supera el m_StepOffset del jugador, así que el motor no lo sube.
            Assert.Greater(OfficeStairs.GhostRise, PlayerStepOffset,
                "la contrahuella del tramo decorativo cabe en el step offset del jugador — " +
                "dejaría de ser decorativo en cuanto alguien lo alcanzara");
        }

        [Test]
        public void NothingInThePieceReachesTheLayerAbove()
        {
            var stairs = BuildStairs();
            foreach (var r in stairs.GetComponentsInChildren<Renderer>())
                Assert.LessOrEqual(r.bounds.max.y, GridConstants.LayerHeight + 1e-4f,
                    $"'{r.gameObject.name}' asoma por encima del techo, dentro de la capa de " +
                    "arriba");
        }

        // ── 2. ESCALÓN TRANSITABLE DE VERDAD ─────────────────────────────────────────

        [Test]
        public void EveryRiserFitsInThePlayerStepOffset()
        {
            Assert.LessOrEqual(OfficeStairs.StepRise, PlayerStepOffset,
                "la contrahuella no cabe en el m_StepOffset del CharacterController: el " +
                "jugador no subiría la escalera, chocaría con ella");
        }

        [Test]
        public void TheStepsFormAContinuousClimbWithNoUnsteppableGap()
        {
            var stairs = BuildStairs();

            // Alturas de las superficies pisables, en orden de avance del tramo (eje Z local).
            var tops = new System.Collections.Generic.List<(float z, float top)>();
            foreach (var col in stairs.GetComponentsInChildren<Collider>())
                tops.Add((col.bounds.center.z, col.bounds.max.y));
            tops.Sort((a, b) => a.z.CompareTo(b.z));

            Assert.AreEqual(OfficeStairs.StepCount + 1, tops.Count,
                "se esperaban los escalones jugables más el rellano, y solo eso, con collider");

            float previous = 0f; // el suelo del tile
            foreach (var t in tops)
            {
                Assert.LessOrEqual(t.top - previous, PlayerStepOffset + 1e-4f,
                    $"salto de {t.top - previous:F3} m entre superficies consecutivas: por " +
                    "encima del step offset el jugador se queda atascado a mitad de escalera");
                previous = t.top;
            }
            Assert.AreEqual(OfficeStairs.LandingY, previous, 1e-4f);
        }

        [Test]
        public void EveryStepIsSolidFromTheFloorUpSoNothingFitsUnderneath()
        {
            var stairs = BuildStairs();
            foreach (var col in stairs.GetComponentsInChildren<Collider>())
                Assert.LessOrEqual(col.bounds.min.y, 1e-4f,
                    $"'{col.gameObject.name}' es una losa flotante: deja un hueco bajo el " +
                    "escalón donde la cápsula del jugador puede encajarse");
        }

        [Test]
        public void TheLandingIsWideAndDeepEnoughToStandOn()
        {
            var stairs = BuildStairs();
            // Por NOMBRE, no por "el collider más alto": el último escalón llega a la misma
            // altura que el rellano, así que un max-por-altura devolvería el escalón (0.55 m
            // de fondo) y el test comprobaría la pieza equivocada — pasando o fallando por
            // el motivo que no es.
            Collider landing = null;
            foreach (var col in stairs.GetComponentsInChildren<Collider>())
                if (col.gameObject.name == "Landing") landing = col;

            Assert.IsNotNull(landing, "no se encontró el rellano por nombre");
            // Cápsula del jugador: m_Radius 0.45 ⇒ 0.9 m de diámetro. Un rellano más
            // estrecho que eso no es "jugable", es un borde.
            Assert.Greater(landing.bounds.size.x, 0.9f, "el rellano es más estrecho que el jugador");
            Assert.Greater(landing.bounds.size.z, 0.9f, "el rellano es menos profundo que el jugador");
            // Y con el jugador de pie encima tiene que quedar hueco hasta el techo.
            Assert.Greater(GridConstants.LayerHeight - OfficeStairs.LandingY, PlayerHeight,
                "el jugador no cabe de pie sobre el rellano");
        }

        [Test]
        public void TheWholePieceFitsInsideOneRenderTileInEveryOrientation()
        {
            // Las 4 orientaciones, no solo yaw=0: el tramo es ASIMÉTRICO en Z (arranca en
            // −1.5 y acaba en +2.45), así que "cabe" no es una propiedad que se pueda dar
            // por simétrica sin comprobarla.
            foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
            {
                var stairs = BuildStairs(yaw);
                float half = GridVisualConstants.TileSize * 0.5f;
                foreach (var r in stairs.GetComponentsInChildren<Renderer>())
                {
                    Assert.LessOrEqual(Mathf.Abs(r.bounds.center.z) + r.bounds.extents.z, half + 1e-3f,
                        $"yaw {yaw}: '{r.gameObject.name}' se sale del tile en Z — invadiría el " +
                        "tile vecino, que sí puede llevar props o pared");
                    Assert.LessOrEqual(Mathf.Abs(r.bounds.center.x) + r.bounds.extents.x, half + 1e-3f,
                        $"yaw {yaw}: '{r.gameObject.name}' se sale del tile en X");
                }
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        // ── 3. PLANIFICACIÓN: determinista, acotada y solo en OFFICE ────────────────

        [Test]
        public void NoStairIsPlannedOutsideOfficeChunks()
        {
            var zones = new[] { Zone(2, 2, 8, 8) };
            var walls = OpenWalls();
            for (int zoneKind = 0; zoneKind < OfficeStairs.ZoneOffice; zoneKind++)
                Assert.IsFalse(OfficeStairs.PlanFor(zoneKind, zones, walls, 3, -7).valid,
                    $"zone_kind {zoneKind} planificó una escalera de oficina");
            // −1 = ZoneRegistry aún sin responder: misma degradación que el tinte y el modelo
            // de pared, nada de adivinar la zona.
            Assert.IsFalse(OfficeStairs.PlanFor(-1, zones, walls, 3, -7).valid);
            Assert.IsTrue(OfficeStairs.PlanFor(OfficeStairs.ZoneOffice, zones, walls, 3, -7).valid);
        }

        [Test]
        public void NoStairIsPlannedWithoutRoomZones()
        {
            var walls = OpenWalls();
            Assert.IsFalse(OfficeStairs.PlanFor(OfficeStairs.ZoneOffice, null, walls, 0, 0).valid);
            Assert.IsFalse(OfficeStairs.PlanFor(
                OfficeStairs.ZoneOffice, new RoomZoneMsg[0], walls, 0, 0).valid);
        }

        [Test]
        public void PlanningIsDeterministicAndLandsInsideTheChosenZone()
        {
            // Los rects de sub-región nacen con origen y tamaño PARES, como los produce el
            // backend (align_origin_to_tile / tile_aligned_size).
            var zones = new[] { Zone(2, 2, 8, 8), Zone(12, 2, 18, 8), Zone(2, 12, 8, 18) };
            var walls = OpenWalls();

            for (int cx = -4; cx <= 4; cx++)
            {
                for (int cz = -4; cz <= 4; cz++)
                {
                    var a = OfficeStairs.PlanFor(OfficeStairs.ZoneOffice, zones, walls, cx, cz);
                    var b = OfficeStairs.PlanFor(OfficeStairs.ZoneOffice, zones, walls, cx, cz);
                    Assert.AreEqual(a.tx, b.tx, "el plan no es determinista");
                    Assert.AreEqual(a.tz, b.tz);
                    Assert.AreEqual(a.yaw, b.yaw);
                    Assert.IsTrue(a.valid);

                    // El tile reservado cae dentro de ALGUNA de las sub-regiones — nunca en
                    // el maze, donde una escalera taparía un pasillo de 2 celdas.
                    bool inside = false;
                    foreach (var z in zones)
                        if (z.ContainsCell(a.tx * 2, a.tz * 2)) inside = true;
                    Assert.IsTrue(inside,
                        $"el tile ({a.tx},{a.tz}) del chunk ({cx},{cz}) no cae en ninguna sub-región");

                    Assert.Contains(a.yaw, new object[] { 0f, 90f, 180f, 270f });
                }
            }
        }

        [Test]
        public void NoStairIsPlannedOnASolidOrPillaredTile()
        {
            var zones = new[] { Zone(2, 2, 8, 8) };
            var plan = OfficeStairs.PlanFor(OfficeStairs.ZoneOffice, zones, OpenWalls(), 5, 5);
            Assert.IsTrue(plan.valid, "el caso base debería planificar, o el resto no prueba nada");

            var solid = OpenWalls();
            solid[plan.tx, plan.tz] = 0x0F;
            Assert.IsFalse(OfficeStairs.PlanFor(OfficeStairs.ZoneOffice, zones, solid, 5, 5).valid,
                "se planificó una escalera en un tile macizo");

            var pillared = OpenWalls();
            pillared[plan.tx, plan.tz] = GridChunkBuilder.PillarNW;
            Assert.IsFalse(OfficeStairs.PlanFor(OfficeStairs.ZoneOffice, zones, pillared, 5, 5).valid,
                "se planificó una escalera sobre una columna");
        }
    }
}
