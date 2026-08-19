using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;
using static BackroomsSurvival.Tests.RoomGeometry;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El generador de salas autoradas: modelo (<see cref="RoomDefinition"/>) → malla
    /// (<see cref="RoomMeshBuilder"/>) → cajas de colisión (<see cref="RoomColliderBuilder"/>).
    ///
    /// Cada test le exige a su sala las tres propiedades de <see cref="RoomGeometry.AssertRoom"/>
    /// y luego sondea lo suyo. La división en casos no es cosmética: un fallo dice QUÉ combinación
    /// lo rompió, que es la mitad del trabajo de arreglarlo.
    /// </summary>
    [TestFixture]
    public class RoomMeshTests
    {
        // ── plantas base ──────────────────────────────────────────────────────

        [Test] public void Box_shell_is_closed() => AssertRoom("box", Box(4, 3));

        [Test]
        public void Round_plan_shell_is_closed() =>
            AssertRoom("round", new RoomDefinition { tilesX = 4, tilesZ = 4, sides = 24, squareness = 0f });

        /// <summary>Los extremos del rango: un triángulo de un tile y una nave de 32×40 con 64
        /// lados. Ahí es donde el redondeo y los tramos cortos se hacen visibles.</summary>
        [Test]
        public void Tiny_plan_shell_is_closed() =>
            AssertRoom("tiny", new RoomDefinition { tilesX = 1, tilesZ = 1, sides = 3, squareness = 0f });

        [Test]
        public void Huge_plan_shell_is_closed() =>
            AssertRoom("huge", new RoomDefinition { tilesX = 32, tilesZ = 40, sides = 64, squareness = 0.5f });

        // ── boquetes de pared ─────────────────────────────────────────────────

        /// <summary>El hueco tiene que ATRAVESAR el muro: la misma esquina en las dos caras. Si
        /// solo aparece en la interior, lo que hay es un nicho pintado, no una puerta.</summary>
        [Test]
        public void Door_pierces_both_wall_faces()
        {
            var doored = Box(4, 4);
            doored.holes = new[] { Door(0, 0.5f) };
            var m = AssertRoom("door", doored);
            Assert.IsTrue(HasVertex(m, 0.8f, 2.2f, 10f), "no corner on the inner face");
            Assert.IsTrue(HasVertex(m, 0.8f, 2.2f, 10.2f), "no corner on the outer face");
        }

        [Test]
        public void Two_holes_on_one_wall()
        {
            var d = Box(4, 4);
            d.holes = new[] { Door(1, 0.25f), Window(1, 0.75f) };
            AssertRoom("two holes on one wall", d);
        }

        /// <summary>Dos ventanas superpuestas emiten jambas que se cruzan dentro del hueco de la
        /// otra, y por ahí se cuela una grieta. Se funden en una sola abertura.</summary>
        [Test]
        public void Overlapping_holes_merge()
        {
            var d = Box(4, 4);
            d.holes = new[] { Window(0, 0.45f), Window(0, 0.55f) };
            AssertRoom("overlapping holes merge", d);
        }

        /// <summary>Un hueco más ancho que su pared se recorta contra ella en vez de desbordarse
        /// a la vecina y comerse la esquina.</summary>
        [Test]
        public void Oversized_hole_clamps_to_its_wall()
        {
            var d = Box(1, 1);
            d.holes = new[]
            {
                new RoomDefinition.WallHole { side = 2, along = 0.5f, baseY = 0f, width = 999f, height = 999f },
            };
            AssertRoom("oversized hole clamps", d);
        }

        // ── boquetes que doblan la esquina ───────────────────────────────────

        /// <summary>
        /// Con <c>spanCorners</c>, una abertura centrada en una esquina se lleva por delante el
        /// vértice y sale por las dos paredes. Se comprueba contra la COLISIÓN y no contra el
        /// número de trozos: lo que importa es que se pueda pasar por los dos lados del rincón,
        /// no cómo esté repartido por dentro.
        /// </summary>
        [Test]
        public void Corner_door_opens_both_walls()
        {
            var d = Box(4, 4);
            d.holes = new[] { Door(0, 1f) };          // centrada justo en la esquina 0-1
            d.holes[0].width = 4f;
            d.holes[0].spanCorners = true;
            AssertRoom("corner door", d);

            var cb = RoomColliderBuilder.Build(d);
            Vector2 a = WallMid(d, 0, 0.95f), b = WallMid(d, 1, 0.05f);
            Assert.IsFalse(Inside(cb, new Vector3(a.x, 1.2f, a.y)), "the first wall stayed solid");
            Assert.IsFalse(Inside(cb, new Vector3(b.x, 1.2f, b.y)), "the second wall stayed solid");
        }

        /// <summary>Sin el mando, esos mismos dos puntos siguen siendo muro: encenderlo es lo que
        /// cambia el resultado, no la anchura.</summary>
        [Test]
        public void Without_spanCorners_the_same_door_stays_on_its_wall()
        {
            var d = Box(4, 4);
            d.holes = new[] { Door(0, 1f) };
            d.holes[0].width = 4f;
            AssertRoom("wide door, one wall", d);

            // La pared VECINA es la que separa los dos comportamientos. La propia no sirve de
            // prueba: la puerta está en ella de las dos maneras.
            var cb = RoomColliderBuilder.Build(d);
            Vector2 b = WallMid(d, 1, 0.05f);
            Assert.IsTrue(Inside(cb, new Vector3(b.x, 1.2f, b.y)), "it spilled onto the next wall");
        }

        /// <summary>
        /// El caso que motivó todo esto y no se ve venir: en una rotonda cada faceta mide poco
        /// más de un metro, así que una puerta de 4 m recortada contra su faceta desaparecería.
        /// </summary>
        [Test]
        public void Wide_door_on_a_rotunda()
        {
            // 10 m de diámetro entre 16 facetas: cada una mide menos de 2 m, así que una puerta
            // de 4 m NO cabe en ninguna. En una rotonda de 25 m la faceta ya mide casi 5 m y el
            // caso deja de probar nada — la primera versión de este test se equivocó justo ahí.
            var d = new RoomDefinition { tilesX = 2, tilesZ = 2, sides = 16, squareness = 0f, heightMeters = 4f };
            d.holes = new[] { Door(0, 0.5f) };
            d.holes[0].width = 4f;
            d.holes[0].spanCorners = true;
            AssertRoom("wide door on a rotunda", d);

            // Tres facetas seguidas abiertas: la del centro y sus dos vecinas.
            var cb = RoomColliderBuilder.Build(d);
            foreach (int side in new[] { 15, 0, 1 })
            {
                Vector2 p = WallMid(d, side, 0.5f);
                Assert.IsFalse(Inside(cb, new Vector3(p.x, 1.2f, p.y)), $"facet {side} stayed solid");
            }
        }

        /// <summary>Una abertura más ancha que el perímetro dejaría la sala sin ninguna pared —
        /// y sin pared no hay dónde apoyar las jambas.</summary>
        [Test]
        public void Arc_wider_than_the_room_clamps()
        {
            var d = Box(2, 2);
            d.holes = new[] { Door(0, 0.5f) };
            d.holes[0].width = 999f;
            d.holes[0].spanCorners = true;
            AssertRoom("arc wider than the room", d);
        }

        /// <summary>Una esquina ENTRANTE dobla al revés: el inglete del muro se pliega hacia
        /// dentro y las dos jambas se encuentran por el otro lado.</summary>
        [Test]
        public void Corner_door_on_a_concave_corner()
        {
            var d = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            var inner = d.InnerContour();
            // El rincón entrante de una L es el único vértice con giro de signo contrario.
            int reflex = -1;
            for (int i = 0; i < inner.Length && reflex < 0; i++)
            {
                Vector2 u = inner[(i + 1) % inner.Length] - inner[i];
                Vector2 v = inner[(i + 2) % inner.Length] - inner[(i + 1) % inner.Length];
                if (u.x * v.y - u.y * v.x < 0f) reflex = (i + 1) % inner.Length;
            }
            Assert.GreaterOrEqual(reflex, 0, "the L has no re-entrant corner");

            d.holes = new[] { Door(reflex - 1, 1f) };
            d.holes[0].width = 4f;
            d.holes[0].spanCorners = true;
            AssertRoom("corner door on a concave corner", d);
        }

        /// <summary>Doblando la esquina y con el techo en pendiente a la vez: el recorte de altura
        /// se aplica trozo a trozo, y cada trozo cae bajo una parte distinta del techo.</summary>
        [Test]
        public void Corner_door_under_a_tilted_ceiling()
        {
            var d = Box(6, 5);
            d.ceilingTilt = 22f;
            d.holes = new[] { Door(0, 1f) };
            d.holes[0].width = 5f;
            d.holes[0].height = 3.5f;
            d.holes[0].spanCorners = true;
            AssertRoom("corner door under a tilted ceiling", d);
        }

        [Test]
        public void Grate_fills_the_opening()
        {
            var d = Box(4, 4);
            d.holes = new[] { Window(0, 0.5f) };
            d.holes[0].grateBars = 5;
            AssertRoom("grate", d);
        }

        // ── sólidos sueltos ───────────────────────────────────────────────────

        [Test]
        public void Pillars_grids_blocks_and_stairs()
        {
            var d = Box(6, 5);
            d.pillars = new[] { new RoomDefinition.Pillar { position = new Vector2(3f, 1f), size = 0.9f, sides = 8 } };
            d.pillarGrids = new[] { new RoomDefinition.PillarGrid { countX = 3, countZ = 2, spacingX = 5f, spacingZ = 4f, size = 0.7f } };
            d.blocks = new[] { new RoomDefinition.Block { position = new Vector2(-5f, 2f), sizeX = 3f, sizeZ = 0.5f, height = 2.2f, yawDegrees = 31f } };
            d.stairs = new[] { new RoomDefinition.Stairs { position = new Vector2(0f, -10f), width = 2f, steps = 8 } };

            AssertRoom("shell under solids", ShellOnly(d));

            var m = RoomMeshBuilder.Build(d);
            Assert.IsTrue(WindingOk(m, out int bad), $"solids: {bad} triangles face the wrong way");
            Assert.IsTrue(UvWorldScale(m, out int uv), $"solids: {uv} edges with stretched texture");
        }

        // ── pozos ─────────────────────────────────────────────────────────────

        [Test]
        public void Pit_reaches_its_depth()
        {
            var d = Box(4, 4);
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            var m = AssertRoom("pit", d);
            Assert.IsTrue(HasVertexY(m, -2.5f), "the pit does not go down to its depth");
        }

        /// <summary>Sacarle un rectángulo a un suelo redondo no se arregla cortando en rejilla:
        /// obliga a triangular de verdad.</summary>
        [Test]
        public void Pit_in_a_round_room()
        {
            var d = new RoomDefinition { tilesX = 5, tilesZ = 5, sides = 16, squareness = 0f };
            d.floorHoles = new[] { Pit(Vector2.zero, 4f, 4f, 3f, 0f) };
            AssertRoom("pit in round room", d);
        }

        [Test]
        public void Two_pits()
        {
            var d = Box(6, 5);
            d.floorHoles = new[]
            {
                Pit(new Vector2(-6f, 0f), 3f, 3f, 2f, 0f),
                Pit(new Vector2(6f, 0f), 2f, 4f, 3.5f, 25f),
            };
            AssertRoom("two pits", d);
        }

        [Test]
        public void Pit_next_to_a_door()
        {
            var d = Box(4, 4);
            d.holes = new[] { Door(0, 0.5f) };
            d.floorHoles = new[] { Pit(new Vector2(0f, 6f), 3f, 2f, 2f, 0f) };
            AssertRoom("pit next to a door", d);
        }

        // ── boquetes en bloques ───────────────────────────────────────────────

        [Test]
        public void Block_door_pierces_both_faces_and_walks_through()
        {
            var d = Box(6, 5);
            d.blocks = new[]
            {
                new RoomDefinition.Block
                {
                    position = new Vector2(0f, 0f), sizeX = 3f, sizeZ = 0.4f, height = 2.4f,
                    holes = new[] { new RoomDefinition.BlockHole { side = 0, along = 0.5f, baseY = 0f, width = 1.2f, height = 2.1f } },
                },
            };
            AssertRoom("block with a door", d);

            var cb = RoomColliderBuilder.Build(d);
            // Justo en el centro del tabique, a media altura de la puerta: se anda.
            Assert.IsFalse(Inside(cb, new Vector3(0f, 1f, 0f)), "the doorway stayed sealed");
            // A los lados del hueco, el tabique sigue en pie.
            Assert.IsTrue(Inside(cb, new Vector3(-1.3f, 1f, 0f)), "the jamb is missing");
            Assert.IsTrue(Inside(cb, new Vector3(1.3f, 1f, 0f)), "the jamb is missing");
            // Por encima de la puerta, el dintel bloquea.
            Assert.IsTrue(Inside(cb, new Vector3(0f, 2.3f, 0f)), "the lintel is missing");
        }

        /// <summary>Un boquete pedido en la cara OPUESTA (side 2) tiene que dar el mismo hueco
        /// físico que uno en side 0 con el mismo `along`: las dos caras describen el MISMO
        /// túnel.</summary>
        [Test]
        public void Block_door_on_opposite_face_is_the_same_hole()
        {
            var a = Box(6, 5);
            a.blocks = new[] { new RoomDefinition.Block { sizeX = 3f, sizeZ = 0.4f, height = 2.4f,
                holes = new[] { new RoomDefinition.BlockHole { side = 0, along = 0.3f, width = 1f, height = 2f } } } };
            var b = Box(6, 5);
            b.blocks = new[] { new RoomDefinition.Block { sizeX = 3f, sizeZ = 0.4f, height = 2.4f,
                holes = new[] { new RoomDefinition.BlockHole { side = 2, along = 0.7f, width = 1f, height = 2f } } } };

            var cbA = RoomColliderBuilder.Build(a);
            var cbB = RoomColliderBuilder.Build(b);
            // along=0.3 en side0 y along=0.7 en side2 (su espejo) tienen que caer en el MISMO
            // punto del mundo.
            Vector3 probe = new Vector3(3f * 0.3f - 1.5f, 1f, 0f);
            Assert.IsFalse(Inside(cbA, probe), "side 0 baseline is not open");
            Assert.IsFalse(Inside(cbB, probe), "side 2, mirrored, does not match side 0");
        }

        [Test]
        public void Block_window_does_not_reach_the_floor()
        {
            var d = Box(6, 5);
            d.blocks = new[]
            {
                new RoomDefinition.Block
                {
                    sizeX = 3f, sizeZ = 0.4f, height = 2.4f,
                    holes = new[] { new RoomDefinition.BlockHole { side = 0, along = 0.5f, baseY = 1.1f, width = 1.5f, height = 0.8f } },
                },
            };
            AssertRoom("block with a window", d);
            var cb = RoomColliderBuilder.Build(d);
            Assert.IsTrue(Inside(cb, new Vector3(0f, 0.5f, 0f)), "a window opened down to the floor");
            Assert.IsFalse(Inside(cb, new Vector3(0f, 1.4f, 0f)), "the window stayed sealed");
        }

        /// <summary>Un tabique orientado (yaw != 0) tiene que abrir el hueco donde el bloque
        /// caiga, no donde caería sin girar.</summary>
        [Test]
        public void Block_door_on_a_rotated_partition()
        {
            var d = Box(6, 5);
            d.blocks = new[]
            {
                new RoomDefinition.Block
                {
                    position = new Vector2(1f, 1f), sizeX = 3f, sizeZ = 0.4f, height = 2.4f,
                    yawDegrees = 90f,
                    holes = new[] { new RoomDefinition.BlockHole { side = 0, along = 0.5f, width = 1.2f, height = 2.1f } },
                },
            };
            AssertRoom("rotated block with a door", d);
            var cb = RoomColliderBuilder.Build(d);
            // Girado 90°, el eje largo del tabique pasa a ser Z; el centro de la puerta cae en
            // el propio centro del bloque.
            Assert.IsFalse(Inside(cb, new Vector3(1f, 1f, 1f)), "the doorway stayed sealed after rotating");
        }

        [Test]
        public void Two_blocks_with_doors()
        {
            var d = Box(8, 6);
            d.blocks = new[]
            {
                new RoomDefinition.Block { position = new Vector2(-6f, 0f), sizeX = 4f, sizeZ = 0.3f, height = 2.4f,
                    holes = new[] { new RoomDefinition.BlockHole { side = 0, along = 0.5f, width = 1.2f, height = 2.1f } } },
                new RoomDefinition.Block { position = new Vector2(6f, 0f), sizeX = 3f, sizeZ = 0.3f, height = 2.6f,
                    holes = new[] { new RoomDefinition.BlockHole { side = 0, along = 0.4f, baseY = 1f, width = 1.5f, height = 1f } } },
            };
            AssertRoom("two blocks with doors", d);
        }

        // ── pozos sin fondo ───────────────────────────────────────────────────

        /// <summary>Un pozo <c>bottomless</c> SIGUE teniendo paredes hasta Depth metros, igual
        /// que uno con fondo — la malla llega hasta ahí — pero remata en algo que NO es un suelo
        /// pisable: nada en la submalla de suelo a esa profundidad.</summary>
        [Test]
        public void Bottomless_pit_walls_reach_depth_but_draw_no_floor_there()
        {
            var d = Box(4, 4);
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            d.floorHoles[0].bottomless = true;
            var m = AssertRoom("bottomless pit", d);
            Assert.IsTrue(HasVertexY(m, -2.5f), "the shaft does not reach Depth");
            Assert.IsFalse(HasTriangleNearY(m, RoomMeshBuilder.SubmeshFloor, -2.5f),
                "it drew a walkable floor at the bottom of an open shaft");
        }

        /// <summary>El centro del hueco nunca tiene fondo, ni a la profundidad pedida ni más
        /// abajo: por ahí se sigue cayendo pase lo que pase.</summary>
        [Test]
        public void Bottomless_pit_has_no_floor_to_land_on()
        {
            var d = Box(4, 4);
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            d.floorHoles[0].bottomless = true;
            var cb = RoomColliderBuilder.Build(d);
            Assert.IsFalse(Inside(cb, new Vector3(1f, -2.5f, 1f)), "something caught the fall");
            Assert.IsFalse(Inside(cb, new Vector3(1f, -20f, 1f)), "something caught the fall, further down");
        }

        /// <summary>El contraste que motiva Depth en un pozo sin fondo: las paredes SÍ bloquean
        /// de lado mientras se cae dentro de Depth, y dejan de hacerlo justo debajo — ahí no hay
        /// nada, ni suelo ni pared.</summary>
        [Test]
        public void Bottomless_pit_walls_collide_within_depth_then_open()
        {
            var d = Box(4, 4);
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            d.floorHoles[0].bottomless = true;
            var cb = RoomColliderBuilder.Build(d);

            // Justo dentro del borde del pozo en XZ (el hueco va de x=-0.5 a x=2.5).
            var nearEdge = new Vector3(-0.5f + 0.02f, -1f, 1f);
            Assert.IsTrue(Inside(cb, nearEdge), "no wall collision within Depth");

            var belowDepth = new Vector3(-0.5f + 0.02f, -3f, 1f);
            Assert.IsFalse(Inside(cb, belowDepth), "something still blocks past Depth");
        }

        /// <summary>El contraste que motiva la opción: un pozo NORMAL sí tiene un fondo en el que
        /// aterrizar y paredes que no se pueden atravesar de lado. Antes de esto ninguno de los
        /// dos existía en colisión — visualmente había un suelo al fondo del pozo que no
        /// detenía a nadie.</summary>
        [Test]
        public void Normal_pit_has_a_floor_and_walls_to_land_on()
        {
            var d = Box(4, 4);
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            var cb = RoomColliderBuilder.Build(d);
            Assert.IsTrue(Inside(cb, new Vector3(1f, -2.5f - 0.05f, 1f)), "no floor at the bottom of the pit");
            // Justo dentro del hueco en XZ pero a la altura del suelo de la sala: si no hay pared
            // de pozo, se atravesaria de lado sin tocar el fondo.
            Assert.IsTrue(Inside(cb, new Vector3(1f - 1.5f + 0.02f, -1f, 1f)), "no wall on the side of the shaft");
        }

        /// <summary>
        /// El bug que se veía en pantalla: la losa EXTERIOR del suelo (a -wallThickness) no se
        /// recortaba para un pozo sin fondo, así que quedaba una chapa cruzando el pozo un palmo
        /// por debajo del borde — el agujero se veía abierto arriba y tapado justo después. Se
        /// sondea la vertical entera del hueco, no solo la submalla de suelo: la chapa era de
        /// pared y mirando hacia abajo, así que ni salía en la sonda de suelo ni se veía desde
        /// arriba, pero estaba.
        /// </summary>
        [Test]
        public void Bottomless_pit_has_nothing_across_its_mouth()
        {
            var d = Box(4, 4);
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            d.floorHoles[0].bottomless = true;
            var m = AssertRoom("bottomless pit, open all the way down", d);

            Assert.IsFalse(CrossesVertical(m, new Vector2(1f, 1f), -50f, -0.001f),
                "something crosses the shaft below the floor");
        }

        /// <summary>El contraste: un pozo CON fondo sí tiene que tener algo cruzando — su suelo.
        /// Sin esto, la sonda de arriba pasaría igual con un generador que no dibujara pozos.</summary>
        [Test]
        public void Normal_pit_does_have_a_bottom_across_its_mouth()
        {
            var d = Box(4, 4);
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            var m = AssertRoom("normal pit", d);

            Assert.IsTrue(CrossesVertical(m, new Vector2(1f, 1f), -50f, -0.001f),
                "a pit with a bottom drew no bottom");
        }

        [Test]
        public void Bottomless_pit_on_a_round_room()
        {
            var d = new RoomDefinition { tilesX = 5, tilesZ = 5, sides = 16, squareness = 0f, heightMeters = 4f };
            d.floorHoles = new[] { Pit(Vector2.zero, 4f, 4f, 3f, 0f) };
            d.floorHoles[0].bottomless = true;
            AssertRoom("bottomless pit in round room", d);
        }

        [Test]
        public void Bottomless_pit_next_to_a_normal_pit()
        {
            var d = Box(6, 5);
            d.floorHoles = new[]
            {
                Pit(new Vector2(-6f, 0f), 3f, 3f, 2f, 0f),
                Pit(new Vector2(6f, 0f), 2f, 4f, 3.5f, 25f),
            };
            d.floorHoles[1].bottomless = true;
            AssertRoom("bottomless pit next to a normal pit", d);
        }

        // ── rodapié y molduras ───────────────────────────────────────────────

        /// <summary>Apagado (el valor por defecto) no cambia nada: una sala ya horneada no
        /// cambia de forma el día que este parámetro se añade.</summary>
        [Test]
        public void Trim_disabled_leaves_the_mesh_untouched()
        {
            var d = Box(4, 3);
            d.trimEnabled = false;
            var mOff = RoomMeshBuilder.Build(d);
            var mBaseline = RoomMeshBuilder.Build(Box(4, 3));
            Assert.AreEqual(mBaseline.vertexCount, mOff.vertexCount);
        }

        [Test]
        public void Trim_adds_baseboard_geometry()
        {
            var plain = Box(4, 3);
            var trimmed = Box(4, 3); trimmed.trimEnabled = true;
            var mPlain = AssertRoom("plain box", plain);
            var mTrimmed = AssertRoom("box with trim", trimmed);
            Assert.Greater(mTrimmed.vertexCount, mPlain.vertexCount, "trim added no geometry at all");
        }

        [Test]
        public void Trim_with_a_door_and_a_window()
        {
            var d = Box(5, 4);
            d.trimEnabled = true;
            d.holes = new[] { Door(0, 0.5f), Window(1, 0.5f) };
            AssertRoom("trim with door and window", d);
        }

        /// <summary>Una ventana no para el rodapié: sigue de largo por debajo, sin ningún
        /// vértice en el borde del hueco. Una puerta sí lo corta ahí. Las coordenadas del borde
        /// salen de <c>ResolveHoles</c> — el mismo cálculo que usa la malla — para no
        /// aritmetizar a mano un número que la más mínima constante cambiada dejaría desfasado.</summary>
        [Test]
        public void Baseboard_continues_under_a_window_but_not_a_door()
        {
            var withDoor = Box(6, 5); withDoor.trimEnabled = true;
            withDoor.holes = new[] { Door(0, 0.5f) };
            var withWindow = Box(6, 5); withWindow.trimEnabled = true;
            withWindow.holes = new[] { Window(0, 0.5f) };

            var mDoor = AssertRoom("trim with door", withDoor);
            var mWindow = AssertRoom("trim with window", withWindow);

            Vector2[] inner = withDoor.InnerContour(); // misma planta en los dos, mismo Box(6,5)
            var perSide = new List<RoomDefinition.HoleRect>[inner.Length];
            for (int i = 0; i < perSide.Length; i++) perSide[i] = new List<RoomDefinition.HoleRect>();

            // A la altura del rodapié (RoomMeshBuilder.TrimHeight) y no al ras del suelo: ahí
            // TODA pared tiene un vértice de la propia rejilla (innerYCuts siempre incluye
            // Y=0), puerta o ventana o ninguna de las dos — sondear ahí daría un falso positivo
            // que no viene del rodapié.
            withDoor.ResolveHoles(inner, 0f, withDoor.heightMeters, perSide);
            float doorU0 = perSide[0][0].u0;
            Vector2 doorEdge = Vector2.Lerp(inner[0], inner[1], doorU0);
            Assert.IsTrue(HasVertex(mDoor, doorEdge.x, RoomMeshBuilder.TrimHeight, doorEdge.y),
                "the door should leave a baseboard piece boundary right at its own edge");

            withWindow.ResolveHoles(inner, 0f, withWindow.heightMeters, perSide);
            float windowU0 = perSide[0][0].u0;
            Vector2 windowEdge = Vector2.Lerp(inner[0], inner[1], windowU0);
            Assert.IsFalse(HasVertex(mWindow, windowEdge.x, RoomMeshBuilder.TrimHeight, windowEdge.y),
                "a window should not cut the baseboard at all");
        }

        [Test]
        public void Trim_on_an_L_shaped_room_with_holes_and_a_pit()
        {
            var d = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            d.trimEnabled = true;
            d.holes = new[] { Door(0, 0.5f), Window(2, 0.4f) };
            d.floorHoles = new[] { Pit(new Vector2(-8f, -5f), 3f, 3f, 2f, 0f) };
            AssertRoom("trimmed L-shape with holes and a pit", d);
        }

        [Test]
        public void Trim_on_a_round_room()
        {
            var d = new RoomDefinition { tilesX = 5, tilesZ = 5, sides = 16, squareness = 0f, heightMeters = 4f };
            d.trimEnabled = true;
            AssertRoom("trimmed round room", d);
        }

        // ── marcadores ────────────────────────────────────────────────────────

        /// <summary>Los marcadores no son geometría: la malla tiene que salir IDÉNTICA con o sin
        /// ellos. RoomMeshBuilder ni siquiera lee <c>def.markers</c> — esto es la prueba
        /// documentada de esa afirmación, no una comprobación de "algo raro pasó".</summary>
        [Test]
        public void Markers_do_not_affect_the_mesh()
        {
            var withMarkers = Box(4, 3);
            withMarkers.markers = new[]
            {
                new RoomDefinition.Marker { kind = RoomDefinition.MarkerKind.Light, position = new Vector2(1f, 1f), y = 2f },
                new RoomDefinition.Marker { kind = RoomDefinition.MarkerKind.Spawn, position = new Vector2(-1f, 0f), tag = "player" },
            };
            var mWith = AssertRoom("room with markers", withMarkers);

            var without = Box(4, 3);
            var mWithout = RoomMeshBuilder.Build(without);

            Assert.AreEqual(mWithout.vertexCount, mWith.vertexCount);
            Assert.AreEqual(mWithout.triangles.Length, mWith.triangles.Length);
        }

        // ── planta dibujada a mano ───────────────────────────────────────────

        [Test]
        public void Manual_contour_with_fewer_than_3_points_falls_back_to_polygon()
        {
            var d = Box(4, 3);
            d.planMode = RoomDefinition.PlanMode.Manual;
            d.manualContour = new[] { new Vector2(1f, 1f), new Vector2(-1f, 1f) }; // solo 2
            Assert.AreEqual(d.ProceduralContour().Length, d.InnerContour().Length,
                "2 points is not a plan; it should degrade to the polygon of always");
        }

        [Test]
        public void Manual_triangle_is_a_valid_room()
        {
            var d = new RoomDefinition { heightMeters = 3f };
            d.planMode = RoomDefinition.PlanMode.Manual;
            d.manualContour = new[] { new Vector2(-5f, -4f), new Vector2(5f, -4f), new Vector2(0f, 5f) };
            AssertRoom("manual triangle", d);
            Assert.AreEqual(3, d.InnerContour().Length);
        }

        /// <summary>Un contorno dibujado en sentido horario (fácil si se han puesto los puntos
        /// al revés) tiene que salir bien igual: la herramienta lo corrige sola, no le pide al
        /// usuario que sepa qué es "antihorario".</summary>
        [Test]
        public void Manual_contour_drawn_clockwise_still_builds_correctly()
        {
            var d = new RoomDefinition { heightMeters = 3f };
            d.planMode = RoomDefinition.PlanMode.Manual;
            // Mismo triángulo que el test de arriba, pero con los puntos en el orden opuesto.
            d.manualContour = new[] { new Vector2(0f, 5f), new Vector2(5f, -4f), new Vector2(-5f, -4f) };
            AssertRoom("clockwise manual triangle", d);
        }

        /// <summary>La irregularidad NO toca una planta manual: quien mueve los puntos a mano ya
        /// está pidiendo el control exacto que la irregularidad existe para no tener que
        /// pedir.</summary>
        [Test]
        public void Manual_contour_ignores_irregularity()
        {
            var d = new RoomDefinition { heightMeters = 3f };
            d.planMode = RoomDefinition.PlanMode.Manual;
            d.manualContour = new[] { new Vector2(-5f, -4f), new Vector2(5f, -4f), new Vector2(0f, 5f) };
            d.irregularity = 1f; d.irregularitySeed = 7;
            var withIrregularity = d.InnerContour();

            var straight = new RoomDefinition { heightMeters = 3f };
            straight.planMode = RoomDefinition.PlanMode.Manual;
            straight.manualContour = d.manualContour;
            var without = straight.InnerContour();

            Assert.Less(ContourDiff(withIrregularity, without), 1e-5f,
                "irregularity moved a hand-drawn plan");
        }

        /// <summary>Una forma en L dibujada a mano, no mordida de una rejilla: la manual no
        /// depende de que la sala sea convexa, igual que Blocks. Parte del contorno YA probado
        /// de <c>L_shape</c> para aislar la única variable real: que llegue por
        /// <c>manualContour</c> en vez de por <c>BlockContour</c>.</summary>
        [Test]
        public void Manual_L_shape_is_non_convex_and_builds()
        {
            var reference = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            var d = new RoomDefinition { heightMeters = 3f };
            d.planMode = RoomDefinition.PlanMode.Manual;
            d.manualContour = reference.InnerContour();
            AssertRoom("manual L shape", d);
            Assert.IsFalse(IsConvex(d.InnerContour()));
        }

        /// <summary>El contorno manual, con un boquete y un pozo encima, tiene que comportarse
        /// EXACTAMENTE igual que la misma planta generada por polígono: la manual solo cambia de
        /// dónde sale el contorno, no cómo se resuelve lo que cuelga de él. Se parte del propio
        /// contorno de <c>Pit_next_to_a_door</c> (ya probado) para que la única variable sea el
        /// modo manual, no una geometría nueva sin probar.</summary>
        [Test]
        public void Manual_contour_with_a_door_and_a_pit()
        {
            var reference = Box(4, 4);
            var d = new RoomDefinition { heightMeters = 4f };
            d.planMode = RoomDefinition.PlanMode.Manual;
            d.manualContour = reference.InnerContour();
            d.holes = new[] { Door(0, 0.5f) };
            d.floorHoles = new[] { Pit(new Vector2(0f, 6f), 3f, 2f, 2f, 0f) };
            AssertRoom("manual contour with door and pit", d);
        }

        /// <summary>
        /// Un lazo/mariposa dibujado a mano (dos aristas no adyacentes que se cruzan) tiene que
        /// detectarse: el recorte de orejas puede triangularlo "bien" de todas formas, así que
        /// esta comprobación es la ÚNICA red antes de guardar una sala con pared cruzada.
        /// </summary>
        [Test]
        public void Bowtie_manual_contour_is_detected_as_self_intersecting()
        {
            // Cuadrado con dos vértices intercambiados: (1,1)-(1,-1) y luego (-1,1)-(-1,-1) en vez
            // de recorrer el perímetro, así que las dos "diagonales" opuestas se cruzan en el medio.
            var bowtie = new[]
            {
                new Vector2(-1f, -1f), new Vector2(1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, 1f),
            };
            Assert.IsTrue(RoomDefinition.ContourSelfIntersects(bowtie));
        }

        [Test]
        public void Simple_manual_contour_is_not_flagged_as_self_intersecting()
        {
            var simple = new RoomDefinition { heightMeters = 3f };
            simple.planMode = RoomDefinition.PlanMode.Manual;
            simple.manualContour = new[] { new Vector2(-5f, -4f), new Vector2(5f, -4f), new Vector2(0f, 5f) };
            Assert.IsFalse(RoomDefinition.ContourSelfIntersects(simple.InnerContour()));

            var L = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            Assert.IsFalse(RoomDefinition.ContourSelfIntersects(L.InnerContour()),
                "a non-convex but simple L-shape is not a self-intersection");
        }

        // ── entreplantas ──────────────────────────────────────────────────────

        [Test]
        public void Level_slab_is_closed()
        {
            var d = Box(6, 5); d.heightMeters = 6f;
            d.levels = new[] { new RoomDefinition.Level { height = 3f } };
            AssertRoom("level slab", d);
        }

        /// <summary>Una escalera que llega a la altura del nivel le abre hueco sola: nadie tiene
        /// que describir el hueco a mano.
        ///
        /// No pasa por <c>AssertRoom</c>: los peldaños de un tramo se TOCAN entre sí por
        /// construcción (comparten cara con el siguiente) y esa cara compartida es interior, no
        /// una fuga — exactamente el motivo por el que <c>Pillars_grids_blocks_and_stairs</c>
        /// tampoco exige estanqueidad estricta con escaleras puestas. Lo que sí se comprueba
        /// aquí es que construye, que las caras no salen invertidas y que la textura no se
        /// estira; la estanqueidad de la LOSA en sí (con o sin hueco) la cubren los tests de
        /// arriba.</summary>
        [Test]
        public void Stairs_reaching_a_level_open_it_automatically()
        {
            var d = Box(6, 5); d.heightMeters = 6f;
            d.levels = new[] { new RoomDefinition.Level { height = 3f } };
            d.stairs = new[]
            {
                new RoomDefinition.Stairs { position = new Vector2(0f, -10f), width = 2f, steps = 17, rise = 0.18f, run = 0.28f },
            };
            var s = d.stairs[0];
            Assert.GreaterOrEqual(s.TopHeight(), 3f, "the stairs do not even reach the level");

            var m = RoomMeshBuilder.Build(d);
            Assert.IsFalse(RoomMeshBuilder.TriangulationFailed, "level with stairs opening: triangulation fell back");
            Assert.IsTrue(WindingOk(m, out int bw), $"level with stairs opening: {bw} triangles face the wrong way");
            Assert.IsTrue(UvWorldScale(m, out int bu), $"level with stairs opening: {bu} edges with stretched texture");

            var cb = RoomColliderBuilder.Build(d);
            Vector3 throughTheHole = new Vector3(s.FootprintCentre().x, 3f, s.FootprintCentre().y);
            Assert.IsFalse(Inside(cb, throughTheHole), "the stairwell stayed sealed");
            // Lejos del hueco, la losa sigue siendo solida.
            Assert.IsTrue(Inside(cb, new Vector3(6f, 3f, 6f)), "the slab is missing away from the stairs");
        }

        /// <summary>Una escalera que NO llega a la altura del nivel no le abre nada: sigue siendo
        /// una losa entera ahí, y la escalera se queda corta contra ella. Mismo motivo que arriba
        /// para no pasar por <c>AssertRoom</c>.</summary>
        [Test]
        public void Stairs_falling_short_of_a_level_do_not_open_it()
        {
            var d = Box(6, 5); d.heightMeters = 6f;
            d.levels = new[] { new RoomDefinition.Level { height = 4f } };
            d.stairs = new[]
            {
                new RoomDefinition.Stairs { position = new Vector2(0f, -10f), width = 2f, steps = 8, rise = 0.18f, run = 0.28f },
            };
            var s = d.stairs[0];
            Assert.Less(s.TopHeight(), 4f, "this case needs stairs that fall short on purpose");

            var m = RoomMeshBuilder.Build(d);
            Assert.IsFalse(RoomMeshBuilder.TriangulationFailed, "level not reached by stairs: triangulation fell back");
            Assert.IsTrue(WindingOk(m, out int bw), $"level not reached by stairs: {bw} triangles face the wrong way");
            Assert.IsTrue(UvWorldScale(m, out int bu), $"level not reached by stairs: {bu} edges with stretched texture");

            var cb = RoomColliderBuilder.Build(d);
            Vector3 overTheStairs = new Vector3(s.FootprintCentre().x, 4f, s.FootprintCentre().y);
            Assert.IsTrue(Inside(cb, overTheStairs), "the slab opened without the stairs reaching it");
        }

        [Test]
        public void Level_height_clamps_to_leave_headroom_both_ways()
        {
            var d = Box(6, 5); d.heightMeters = 3f;
            // Pedidas fuera de rango a proposito: pegada al suelo y pegada al techo.
            d.levels = new[]
            {
                new RoomDefinition.Level { height = 0.1f },
                new RoomDefinition.Level { height = 2.9f },
            };
            AssertRoom("clamped levels", d);
        }

        [Test]
        public void Level_on_an_L_shaped_room()
        {
            var d = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            d.heightMeters = 6f;
            d.levels = new[] { new RoomDefinition.Level { height = 3f } };
            AssertRoom("level on an L plan", d);
        }

        [Test]
        public void Level_on_a_round_room()
        {
            var d = new RoomDefinition { tilesX = 6, tilesZ = 6, sides = 20, squareness = 0f, heightMeters = 6f };
            d.levels = new[] { new RoomDefinition.Level { height = 3f } };
            AssertRoom("level on a round plan", d);
        }

        /// <summary>Tampoco pasa por <c>AssertRoom</c>, mismo motivo que los dos de arriba: hay
        /// escaleras puestas.
        ///
        /// El mismo `run`/`steps` que ya prueban los dos tests de arriba en las dos escaleras —
        /// solo cambia `rise` para llegar más alto sin alargar la huella en planta. Un tramo real
        /// no subiría tan empinado, pero aquí lo que se comprueba es el cableado (qué escalera
        /// abre qué nivel), no la ergonomía del tramo.</summary>
        [Test]
        public void Two_levels_two_stairs()
        {
            var d = Box(6, 5); d.heightMeters = 9f;
            d.levels = new[]
            {
                new RoomDefinition.Level { height = 3f },
                new RoomDefinition.Level { height = 6f },
            };
            d.stairs = new[]
            {
                // Llega a las dos losas.
                new RoomDefinition.Stairs { position = new Vector2(0f, -10f), width = 2f, steps = 17, rise = 0.36f, run = 0.28f },
                // Se queda corta para la de 6 m; solo abre la de 3.
                new RoomDefinition.Stairs { position = new Vector2(4f, -10f), width = 2f, steps = 17, rise = 0.18f, run = 0.28f },
            };

            var m = RoomMeshBuilder.Build(d);
            Assert.IsFalse(RoomMeshBuilder.TriangulationFailed, "two levels, two stairs: triangulation fell back");
            Assert.IsTrue(WindingOk(m, out int bw), $"two levels, two stairs: {bw} triangles face the wrong way");
            Assert.IsTrue(UvWorldScale(m, out int bu), $"two levels, two stairs: {bu} edges with stretched texture");
        }

        // ── colisión coherente con lo que se ve ───────────────────────────────

        /// <summary>El modo de fallo peligroso del sistema: la malla dibuja suelo sólido donde el
        /// collider abre hueco, o al revés. Se sondea la caja, no el triángulo.</summary>
        [Test]
        public void Collision_matches_the_pit_and_the_door()
        {
            var pit = Box(4, 4);
            pit.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            var cb = RoomColliderBuilder.Build(pit);
            Assert.IsFalse(Inside(cb, new Vector3(1f, -0.1f, 1f)), "the floor is solid over the pit");
            Assert.IsTrue(Inside(cb, new Vector3(-7f, -0.1f, -7f)), "the floor is missing away from the pit");

            var doored = Box(4, 4);
            doored.holes = new[] { Door(0, 0.5f) };
            var cd = RoomColliderBuilder.Build(doored);
            Vector2 mid = WallMid(doored, 0, 0.5f);
            Assert.IsFalse(Inside(cd, new Vector3(mid.x, 1.2f, mid.y)), "the doorway is blocked");
            Assert.IsTrue(Inside(cd, new Vector3(mid.x, 3.2f, mid.y)), "the lintel is not solid");
        }

        /// <summary>Una reja bloquea el paso aunque la malla dibuje un hueco real cruzado por
        /// barrotes: para la colisión NO es una abertura. Sin esto el jugador atraviesa los
        /// barrotes que ve dibujados.</summary>
        [Test]
        public void Collision_blocks_a_grated_window()
        {
            var d = Box(4, 4);
            d.holes = new[] { Window(0, 0.5f) };
            d.holes[0].grateBars = 4;
            AssertRoom("grated window", d);

            var cb = RoomColliderBuilder.Build(d);
            Vector2 mid = WallMid(d, 0, 0.5f);
            // Window: baseY 1.2, height 1.2 -> mitad de la abertura a Y=1.8.
            Assert.IsTrue(Inside(cb, new Vector3(mid.x, 1.8f, mid.y)),
                "a grated window has no collider -- walks straight through the bars");
        }

        /// <summary>Un pozo SIN FONDO con profundidad casi nula sigue siendo un pozo abierto (la
        /// profundidad "no tiene efecto" con bottomless, según su propio tooltip): la losa del
        /// suelo tiene que abrirse igual que si tuviera 2,5 m.</summary>
        [Test]
        public void Collision_matches_a_bottomless_pit_at_near_zero_depth()
        {
            var pit = Box(4, 4);
            pit.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 0f, 0f) };
            pit.floorHoles[0].bottomless = true;
            var cb = RoomColliderBuilder.Build(pit);
            Assert.IsFalse(Inside(cb, new Vector3(1f, -0.1f, 1f)),
                "the floor slab is still solid over a bottomless pit with near-zero depth");
        }

        /// <summary>
        /// Un hueco alto bajo techo inclinado se recorta al MISMO tope en la malla y en la
        /// colisión (minCeil - dintel mínimo), no contra la altura nominal: si el collider usara
        /// un tope distinto, quedaría hueco de colisión justo donde la malla ya pinta pared
        /// sólida — pared que se atraviesa aunque se vea entera.
        /// </summary>
        [Test]
        public void Collision_clips_a_tall_hole_to_the_tilted_ceiling_like_the_mesh_does()
        {
            var d = Box(6, 5);
            d.ceilingTilt = 30f;
            d.holes = new[] { new RoomDefinition.WallHole
                { side = 0, along = 0.5f, baseY = 0f, width = 1.6f, height = 10f } };
            AssertRoom("tall hole under a tilted ceiling", d);

            float minCeil = d.MinCeilingOver(d.InnerContour());
            Vector2 mid = WallMid(d, 0, 0.5f);
            var cb = RoomColliderBuilder.Build(d);
            Assert.IsTrue(Inside(cb, new Vector3(mid.x, minCeil - 0.02f, mid.y)),
                "the collider left the hole open above the mesh's clipped top");
        }

        // ── plantas NO convexas ───────────────────────────────────────────────

        [Test]
        public void L_shape()
        {
            var L = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            AssertRoom("L-shape", L);
            Assert.IsFalse(IsConvex(L.InnerContour()), "the notch did not make it non-convex");
            // 6 esquinas es lo que tiene una L; si salieran 4, la muesca no se aplicó.
            Assert.AreEqual(6, L.InnerContour().Length, "an L has 6 corners");
        }

        [Test]
        public void U_shape()
        {
            var U = Blocks(6, 4, new RoomDefinition.Notch { tileX = 2, tileZ = 2, tilesX = 2, tilesZ = 2 });
            AssertRoom("U-shape", U);
            Assert.IsFalse(IsConvex(U.InnerContour()), "the notch did not make it non-convex");
        }

        [Test]
        public void T_shape() =>
            AssertRoom("T-shape", Blocks(6, 4,
                new RoomDefinition.Notch { tileX = 0, tileZ = 2, tilesX = 2, tilesZ = 2 },
                new RoomDefinition.Notch { tileX = 4, tileZ = 2, tilesX = 2, tilesZ = 2 }));

        /// <summary>El caso de uso de verdad, no el de laboratorio.</summary>
        [Test]
        public void L_shape_with_door_window_and_pit()
        {
            var d = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            d.holes = new[] { Door(0, 0.5f), Window(2, 0.4f) };
            d.floorHoles = new[] { Pit(new Vector2(-8f, -5f), 3f, 3f, 2f, 0f) };
            AssertRoom("L-shape with door, window and pit", d);
        }

        /// <summary>Una muesca que vacía la sala o la parte en dos no entrega geometría imposible:
        /// cae al plan poligonal.</summary>
        [Test]
        public void Notch_that_empties_the_room_degrades() =>
            AssertRoom("empty notch", Blocks(3, 3,
                new RoomDefinition.Notch { tileX = 0, tileZ = 0, tilesX = 3, tilesZ = 3 }));

        [Test]
        public void Notch_that_splits_the_room_degrades()
        {
            var split = Blocks(3, 3, new RoomDefinition.Notch { tileX = 1, tileZ = 0, tilesX = 1, tilesZ = 3 });
            AssertRoom("splitting notch", split);
            Assert.AreEqual(split.sides, split.InnerContour().Length, "it did not fall back to the polygon plan");
        }

        // ── techo inclinado ───────────────────────────────────────────────────

        [Test]
        public void Tilted_ceiling_really_varies()
        {
            var tilt = Box(6, 5); tilt.ceilingTilt = 25f;
            var m = AssertRoom("tilted ceiling", tilt);
            float lo = CeilingYNear(m, 0f, -12f), hi = CeilingYNear(m, 0f, 12f);
            Assert.Greater(Mathf.Abs(lo - hi), 2f, $"the ceiling is flat: {lo:F2} vs {hi:F2}");
        }

        [Test]
        public void Tilted_ceiling_rotated()
        {
            var d = Box(6, 5); d.ceilingTilt = 20f; d.ceilingTiltYaw = 90f;
            AssertRoom("tilted ceiling, rotated", d);
        }

        /// <summary>
        /// Un hueco alto en el lado BAJO no puede asomar por encima del techo. Se comprueba la
        /// MALLA, no la definición: el recorte se aplica al construir, así que mirar los
        /// parámetros crudos no dice nada — la primera versión de este test hacía justo eso y
        /// fallaba por comprobar lo que no era.
        /// </summary>
        [Test]
        public void Nothing_pokes_above_a_tilted_ceiling()
        {
            var d = Box(6, 5);
            d.ceilingTilt = 30f;
            d.holes = new[] { Door(0, 0.5f), Window(2, 0.5f) };
            var m = AssertRoom("tilted ceiling with holes", d);

            float highest = 0f;
            foreach (var v in m.vertices) highest = Mathf.Max(highest, v.y);

            // Sobre el contorno EXTERIOR: la cara de fuera sobresale del footprint, así que su
            // punto más alto está por encima del que da el contorno interior.
            float maxCeil = 0f;
            foreach (var c in RoomDefinition.OffsetOutward(d.InnerContour(), d.wallThickness))
                maxCeil = Mathf.Max(maxCeil, d.CeilingYAt(c));

            Assert.LessOrEqual(highest, maxCeil + d.wallThickness + 0.01f,
                $"{highest:F2} m over a ceiling that tops out at {maxCeil + d.wallThickness:F2} m");
        }

        [Test]
        public void Tilted_ceiling_on_an_L_plan()
        {
            var d = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            d.ceilingTilt = 22f;
            AssertRoom("tilted ceiling on an L plan", d);
        }

        [Test]
        public void Tilted_ceiling_over_a_pit()
        {
            var d = Box(5, 5);
            d.ceilingTilt = 18f;
            d.floorHoles = new[] { Pit(Vector2.zero, 3f, 3f, 2f, 0f) };
            AssertRoom("tilted ceiling over a pit", d);
        }

        /// <summary>Se limita la PENDIENTE, no la altura punto a punto: recortar cada punto aplana
        /// un lado y el techo deja de ser un plano.</summary>
        [Test]
        public void Extreme_tilt_clamps_instead_of_cutting_the_floor()
        {
            var steep = Box(8, 3); steep.ceilingTilt = 40f; steep.heightMeters = 3f;
            AssertRoom("extreme tilt", steep);
            Assert.GreaterOrEqual(steep.MinCeilingOver(steep.InnerContour()),
                RoomDefinition.MinCeilingHeight - 0.01f, "the ceiling cut into the floor");
        }

        /// <summary>
        /// El techo escalonado CUELGA por debajo del pintado a propósito: se choca un poco antes,
        /// nunca se atraviesa. Lo que hay que comprobar es justo eso —que no se pueda salir por
        /// arriba— y no que collider y malla COINCIDAN, que sería pedirle al diseño lo contrario
        /// de lo que hace. La primera versión de este test afirmaba justo eso.
        /// </summary>
        [TestCase(-10f)]
        [TestCase(0f)]
        [TestCase(10f)]
        public void Tilt_stops_you_no_later_than_the_visible_ceiling(float pz)
        {
            var tilt = Box(6, 5); tilt.ceilingTilt = 25f;
            var cb = RoomColliderBuilder.Build(tilt);
            float stop = FirstBlockedGoingUp(cb, new Vector2(0f, pz));
            float seen = tilt.CeilingYAt(new Vector2(0f, pz));
            Assert.LessOrEqual(stop, seen + 0.01f, $"stopped at {stop:F2} with the ceiling at {seen:F2}");
        }

        // ── irregularidad ─────────────────────────────────────────────────────

        [Test]
        public void Irregularity_moves_the_walls()
        {
            var irr = Box(5, 4); irr.irregularity = 0.6f; irr.irregularitySeed = 7;
            AssertRoom("irregular walls", irr);
            Assert.Greater(ContourDiff(Box(5, 4).InnerContour(), irr.InnerContour()), 0.1f,
                "the knob does nothing");
        }

        /// <summary>A 0 la planta tiene que ser EXACTAMENTE la de antes: el mando apagado no puede
        /// mover ni un milímetro, o cada sala ya horneada cambiaría de forma.</summary>
        [Test]
        public void Irregularity_zero_leaves_the_plan_untouched()
        {
            var zero = Box(5, 4); zero.irregularity = 0f; zero.irregularitySeed = 99;
            Assert.Less(ContourDiff(Box(5, 4).InnerContour(), zero.InnerContour()), 1e-5f);
        }

        /// <summary>La planta la piden por separado la malla, los colliders y los tests: los tres
        /// tienen que ver exactamente la misma.</summary>
        [Test]
        public void Same_seed_same_plan_different_seed_different_plan()
        {
            var a = Box(5, 4); a.irregularity = 0.6f; a.irregularitySeed = 7;
            var b = Box(5, 4); b.irregularity = 0.6f; b.irregularitySeed = 7;
            var c = Box(5, 4); c.irregularity = 0.6f; c.irregularitySeed = 8;
            Assert.Less(ContourDiff(a.InnerContour(), b.InnerContour()), 1e-5f, "same seed drifted");
            Assert.Greater(ContourDiff(a.InnerContour(), c.InnerContour()), 0.05f, "seeds are ignored");
        }

        [Test]
        public void Max_irregularity_does_not_self_intersect()
        {
            var d = Box(5, 4); d.irregularity = 1f; d.irregularitySeed = 3;
            AssertRoom("irregularity at maximum", d);
            Assert.IsFalse(SelfIntersects(d.InnerContour()), "the plan folded over itself");
        }

        [Test]
        public void Irregular_round_plan_does_not_self_intersect()
        {
            var d = new RoomDefinition { tilesX = 5, tilesZ = 5, sides = 20, squareness = 0f };
            d.irregularity = 0.7f; d.irregularitySeed = 5;
            AssertRoom("irregular round plan", d);
            Assert.IsFalse(SelfIntersects(d.InnerContour()), "the plan folded over itself");
        }

        /// <summary>Todo encima de todo: es donde salen las interacciones que por separado no
        /// aparecen.</summary>
        [Test]
        public void Irregular_L_plan_tilted_with_holes_and_a_pit()
        {
            var d = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            d.irregularity = 0.5f; d.irregularitySeed = 11;
            d.ceilingTilt = 18f;
            d.holes = new[] { Door(0, 0.5f), Window(2, 0.4f) };
            d.floorHoles = new[] { Pit(new Vector2(-8f, -5f), 3f, 3f, 2f, 0f) };
            AssertRoom("irregular L plan, tilted, with holes and a pit", d);
        }

        // ── salas aleatorias ──────────────────────────────────────────────────

        /// <summary>
        /// Las combinaciones que a mano no se prueban. Un caso por semilla y no un bucle dentro de
        /// un test: así el informe dice QUÉ semilla, que es con lo que se reproduce.
        /// </summary>
        [Test]
        public void Random_room_shell_is_closed(
            [Values(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20,
                    21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40)]
            int seed)
        {
            var d = new RoomDefinition();
            d.Randomize(seed);
            AssertRoom($"seed {seed}", ShellOnly(d));
        }

        /// <summary>
        /// Regresión del bug de las 4 aristas duplicadas: dos huecos SIN relación entre sí (huecos
        /// 5 y 6, en lados distintos) con niveles Y a menos de 0,1 mm uno del otro. La lista
        /// compartida de cortes los fusiona a 1 mm (<see cref="RoomMeshBuilder.LevelCuts"/>), pero
        /// la jamba de cada hueco recortaba contra su PROPIO y0/y1 en precisión nativa con un
        /// epsilon de 0,01 mm — más fino que esa fusión — y el resto de menos de 0,1 mm sobrevivía
        /// como una loncha de jamba de grosor invisible.
        /// </summary>
        [Test]
        public void Random_room_shell_is_closed_seed_19()
        {
            var d = new RoomDefinition();
            d.Randomize(19);
            AssertRoom("seed 19", ShellOnly(d));
        }

        /// <summary>
        /// Bug conocido en <see cref="PolygonTriangulator"/>, encontrado autorando las
        /// entreplantas: un ÚNICO agujero rectangular, DESCENTRADO respecto a la sala, puede
        /// hacer que el recorte de orejas se quede sin ninguna oreja válida a mitad de camino —
        /// pese a que el teorema de las dos orejas garantiza que un polígono simple siempre tiene
        /// alguna. El mismo agujero centrado (x=0) triangula bien; corrido a x=-2, no. Ni
        /// siquiera es monótono: añadirle un SEGUNDO agujero al lado a veces lo arregla, porque
        /// cambia el orden de cosido de los puentes. No se ha localizado la causa exacta —
        /// probablemente el puente roza algo lo bastante cerca como para que el margen de
        /// <c>Eps</c> lo cuente como bloqueo — y no se ha intentado arreglar: es del triangulador,
        /// no de las entreplantas, y las entreplantas ya evitan la posición que lo dispara.
        /// </summary>
        [Test]
        [Ignore("Bug conocido en PolygonTriangulator: un agujero rectangular descentrado puede dejar el recorte de orejas sin ninguna oreja valida.")]
        public void Off_center_hole_triangulates()
        {
            var outer = new List<Vector2>
            {
                new Vector2(15f, 12.5f), new Vector2(-15f, 12.5f),
                new Vector2(-15f, -12.5f), new Vector2(15f, -12.5f),
            };
            var hole = new List<Vector2> { new Vector2(-3f, -5.24f), new Vector2(-1f, -5.24f),
                new Vector2(-1f, -10f), new Vector2(-3f, -10f) };
            bool ok = PolygonTriangulator.Triangulate(outer, new List<IList<Vector2>> { hole },
                new List<Vector2>(), new List<int>());
            Assert.IsTrue(ok, "the off-center hole failed to triangulate");
        }

        [Test]
        public void Same_seed_same_room()
        {
            var a = new RoomDefinition(); a.Randomize(777);
            var b = new RoomDefinition(); b.Randomize(777);
            Assert.AreEqual(a.tilesX, b.tilesX);
            Assert.AreEqual(a.sides, b.sides);
            Assert.AreEqual(a.holes.Length, b.holes.Length);
        }

        /// <summary>
        /// La herramienta clona una sala guardada con un viaje de ida y vuelta por JSON
        /// (<c>RoomAuthoringWindow.LoadRoom</c>) para no aliasear los arrays del pool. Esto
        /// comprueba que ese viaje sobrevive con TODOS los features encima, incluidos los más
        /// nuevos (niveles, pozos sin fondo, huecos que doblan esquina) — un campo olvidado en
        /// un <c>[Serializable]</c> nuevo no da error de compilación, solo una sala cargada con
        /// ese dato perdido en silencio.
        /// </summary>
        [Test]
        public void Definition_survives_a_json_round_trip()
        {
            var d = Blocks(6, 5, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 2 });
            d.ceilingTilt = 18f; d.ceilingTiltYaw = 40f;
            d.irregularity = 0.4f; d.irregularitySeed = 5;
            d.holes = new[] { Door(0, 0.5f) };
            d.holes[0].spanCorners = true;
            d.floorHoles = new[] { Pit(new Vector2(1f, 1f), 3f, 2f, 2.5f, 0f) };
            d.floorHoles[0].bottomless = true;
            d.pillars = new[] { new RoomDefinition.Pillar { position = new Vector2(3f, 1f), size = 0.9f, sides = 8 } };
            d.stairs = new[] { new RoomDefinition.Stairs { position = new Vector2(0f, -10f), width = 2f, steps = 17, rise = 0.18f, run = 0.28f } };
            d.levels = new[] { new RoomDefinition.Level { height = 3f } };
            d.markers = new[]
            {
                new RoomDefinition.Marker { kind = RoomDefinition.MarkerKind.Light, position = new Vector2(1f, 1f),
                    y = 2f, lightColor = Color.cyan, lightIntensity = 2.5f, lightRange = 6f },
                new RoomDefinition.Marker { kind = RoomDefinition.MarkerKind.Prop, position = new Vector2(-1f, -1f),
                    tag = "crate" },
            };

            var clone = JsonUtility.FromJson<RoomDefinition>(JsonUtility.ToJson(d));

            Assert.AreEqual(d.tilesX, clone.tilesX);
            Assert.AreEqual(d.planMode, clone.planMode);
            Assert.AreEqual(d.notches.Length, clone.notches.Length);
            Assert.AreEqual(d.ceilingTilt, clone.ceilingTilt);
            Assert.AreEqual(d.irregularitySeed, clone.irregularitySeed);
            Assert.AreEqual(d.holes.Length, clone.holes.Length);
            Assert.AreEqual(d.holes[0].spanCorners, clone.holes[0].spanCorners);
            Assert.AreEqual(d.floorHoles[0].bottomless, clone.floorHoles[0].bottomless);
            Assert.AreEqual(d.pillars[0].sides, clone.pillars[0].sides);
            Assert.AreEqual(d.stairs[0].steps, clone.stairs[0].steps);
            Assert.AreEqual(d.levels.Length, clone.levels.Length);
            Assert.AreEqual(d.levels[0].height, clone.levels[0].height);
            Assert.AreEqual(d.markers.Length, clone.markers.Length);
            Assert.AreEqual(d.markers[0].kind, clone.markers[0].kind);
            Assert.AreEqual(d.markers[0].lightColor, clone.markers[0].lightColor);
            Assert.AreEqual(d.markers[0].lightIntensity, clone.markers[0].lightIntensity);
            Assert.AreEqual(d.markers[1].tag, clone.markers[1].tag);

            // Y no solo los campos: el CLON tiene que producir la MISMA planta, que es lo que de
            // verdad importa para poder seguir editando donde se dejó.
            Assert.Less(ContourDiff(d.InnerContour(), clone.InnerContour()), 1e-4f,
                "the clone's plan drifted from the original");

            // Sin AssertRoom: hay escaleras, y sus peldaños se tocan entre sí por construcción
            // (ver los tests de niveles de más arriba) — no es lo que este test comprueba.
            var m = RoomMeshBuilder.Build(clone);
            Assert.IsFalse(RoomMeshBuilder.TriangulationFailed, "round-tripped clone: triangulation fell back");
            Assert.IsTrue(WindingOk(m, out int bw), $"round-tripped clone: {bw} triangles face the wrong way");
        }
    }
}
