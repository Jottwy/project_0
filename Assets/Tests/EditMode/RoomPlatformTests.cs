using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;
using static BackroomsSurvival.Tests.RoomGeometry;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Las plataformas: polígonos marcados sobre el suelo, con una altura por punto, recortados
    /// contra la planta de la sala.
    ///
    /// Lo que de verdad se prueba aquí es el RECORTE. Que una plataforma dentro de la sala salga
    /// bien es lo fácil; lo que se pidió es poder arrastrar un punto sin cuidado, que se salga por
    /// una pared, y que lo de fuera desaparezca — incluso en una planta en L, donde el recorte se
    /// hace contra una región cóncava.
    /// </summary>
    [TestFixture]
    public class RoomPlatformTests
    {
        private static RoomDefinition.PlatformPoint P(float x, float z, float h) =>
            new RoomDefinition.PlatformPoint { position = new Vector2(x, z), height = h };

        private static RoomDefinition.Platform Plat(params RoomDefinition.PlatformPoint[] pts) =>
            new RoomDefinition.Platform { points = pts };

        private static RoomDefinition WithPlatform(params RoomDefinition.Platform[] plats)
        {
            var d = Box(6, 5);
            d.holes = new[] { Door(0, 0.2f) };
            d.platforms = plats;
            return d;
        }

        /// <summary>La cuña de la referencia: base ancha abajo, un punto alto. Es el caso que
        /// motivó la pieza, así que va el primero.</summary>
        private static RoomDefinition.Platform Ramp() =>
            Plat(P(-4f, -4f, 0f), P(4f, -4f, 0f), P(0f, 4f, 3f));

        // ── que la sala siga entera ───────────────────────────────────────────

        [Test]
        public void A_platform_keeps_the_room_watertight() =>
            AssertRoom("one platform", WithPlatform(Ramp()));

        [Test]
        public void Several_platforms_keep_the_room_watertight() =>
            AssertRoom("three platforms", WithPlatform(
                Ramp(),
                Plat(P(6f, 6f, 1f), P(10f, 6f, 1f), P(10f, 10f, 1f), P(6f, 10f, 1f)),
                Plat(P(-10f, 6f, 0f), P(-6f, 6f, 2f), P(-8f, 10f, 4f))));

        /// <summary>Una tarima plana: todos los puntos a la misma altura. No es una rampa, y tiene
        /// que salir igual de cerrada.</summary>
        [Test]
        public void A_flat_platform_is_watertight() =>
            AssertRoom("flat platform", WithPlatform(
                Plat(P(-4f, -4f, 1.2f), P(4f, -4f, 1.2f), P(4f, 4f, 1.2f), P(-4f, 4f, 1.2f))));

        /// <summary>Muchos puntos: es la mitad de lo que se pidió — poder detallar la forma.</summary>
        [Test]
        public void A_many_sided_platform_is_watertight()
        {
            var pts = new List<RoomDefinition.PlatformPoint>();
            for (int i = 0; i < 9; i++)
            {
                float a = i / 9f * Mathf.PI * 2f;
                pts.Add(P(Mathf.Cos(a) * 5f, Mathf.Sin(a) * 5f, 1f + Mathf.Sin(a * 2f)));
            }
            AssertRoom("nine-sided platform", WithPlatform(Plat(pts.ToArray())));
        }

        // ── el recorte ────────────────────────────────────────────────────────

        /// <summary>
        /// El caso de la captura: los puntos se arrastran FUERA de la sala. Lo que sobresale tiene
        /// que desaparecer, no asomar por detrás de la pared.
        /// </summary>
        [Test]
        public void What_falls_outside_the_room_is_cut_away()
        {
            // La sala es de 30 × 25 m, o sea ±15 en X. Esta plataforma se sale por los dos lados.
            var d = WithPlatform(Plat(P(-40f, -4f, 0f), P(40f, -4f, 0f), P(0f, 4f, 3f)));
            var m = AssertRoom("platform spilling out of the room", d);

            var inner = d.InnerContour();
            float maxX = 0f;
            foreach (var p in inner) maxX = Mathf.Max(maxX, Mathf.Abs(p.x));

            foreach (var v in m.vertices)
                Assert.LessOrEqual(Mathf.Abs(v.x), maxX + d.wallThickness + 0.01f,
                    $"a vertex at x={v.x:F2} escaped a room that ends at {maxX:F2}");
        }

        /// <summary>
        /// El recorte, en una planta en L. Es el caso que obligó a recortar contra la planta
        /// TRIANGULADA en vez de contra el contorno entero: Sutherland–Hodgman a secas solo vale
        /// contra regiones convexas y una L no lo es.
        /// </summary>
        private static RoomDefinition LPlan()
        {
            var d = Blocks(6, 6, new RoomDefinition.Notch { tileX = 3, tileZ = 3, tilesX = 3, tilesZ = 3 });
            d.holes = new[] { Door(0, 0.5f) };
            return d;
        }

        [Test]
        public void Clipping_survives_a_concave_plan()
        {
            var d = LPlan();
            // Cruza el hueco de la L de lado a lado, así que el recorte tiene que partir la pieza
            // contra una región cóncava — que es lo que se está probando.
            //
            // Sin llegar a tocar las paredes a propósito. Una plataforma cuyo borde COINCIDE con el
            // contorno de la sala apoya su tapa inferior justo encima del suelo, arista por arista,
            // y eso son dos superficies pegadas: geometría duplicada, no un fallo del recorte. Ver
            // <see cref="A_platform_flush_with_the_walls_doubles_the_floor"/>.
            d.platforms = new[] { Plat(P(-12f, -12f, 0f), P(12f, -12f, 0f), P(12f, 12f, 3f), P(-12f, 12f, 3f)) };

            // Estanqueidad y textura se exigen enteras. El giro de las caras se comprueba aparte y
            // solo contra la INVERSIÓN de verdad: junto al rincón de la L, el recorte deja alguna
            // astilla casi vertical, y la comprobación compartida marca por igual una cara del
            // revés —que es un agujero— y una cara muy inclinada —que no se ve—. Aquí importa la
            // primera.
            var m = RoomMeshBuilder.Build(d);
            Assert.IsFalse(RoomMeshBuilder.TriangulationFailed, "triangulation fell back");
            Assert.IsTrue(UvWorldScale(m, out int bu), $"{bu} edges with stretched texture");

            // Sin AGUJEROS. Se exige esto y no la estanqueidad completa porque en una planta
            // cóncava quedan un par de aristas verticales DUPLICADAS en la esquina donde se juntan
            // dos trozos del recorte: dos caras pegadas, dentro del sólido. Feo, invisible, y muy
            // distinto de un agujero — por ahí no se ve ni se cae nadie. La comprobación de siempre
            // mete las dos cosas en el mismo saco, así que aquí se separan y se afirma la que
            // importa.
            Assert.AreEqual(0, OpenEdges(m), "the platform left holes in the shell");

            // Contra la MISMA sala sin plataforma, no contra cero. Esta planta en L ya trae dos
            // caras del revés por su cuenta —bug del generador de salas, anterior a todo esto y sin
            // relación con el recorte—, y exigir cero aquí estaría midiendo ese fallo ajeno en vez
            // del que toca. Lo que se afirma es lo que importa: la plataforma no añade ninguna.
            Assert.AreEqual(InvertedFaces(RoomMeshBuilder.Build(LPlan())), InvertedFaces(m),
                "the platform added faces pointing the wrong way");

            // Nada de la plataforma puede quedar dentro de la MUESCA: ahí no hay sala, y es el
            // trozo que el recorte tiene que haberse comido.
            //
            // Se mira la muesca y no "fuera del contorno" en general: la sala tiene su propia cara
            // exterior, que por el grosor del muro sobresale del contorno interior por definición,
            // y confundirla con geometría escapada hace fallar el test por donde no es.
            //
            // La muesca ocupa los tiles 3..6 en los dos ejes; con la sala centrada, eso es de 0 a
            // 15 m. Se mete un margen hacia dentro para no discutir con la pared que la bordea.
            const float tile = 5f;
            float m0 = 3f * tile - 15f + d.wallThickness + 0.05f;   // 0 m + margen
            float m1 = 6f * tile - 15f - 0.05f;                     // 15 m − margen

            foreach (var v in m.vertices)
            {
                if (v.y < 0.05f) continue;                     // suelo y zócalo, fuera
                bool inNotch = v.x > m0 && v.x < m1 && v.z > m0 && v.z < m1;
                Assert.IsFalse(inNotch,
                    $"a vertex at ({v.x:F1}, {v.z:F1}) landed inside the notch, where there is no room");
            }
        }

        /// <summary>
        /// El límite conocido, escrito para que no se descubra dos veces: una plataforma cuyo borde
        /// COINCIDE con el contorno de la sala apoya su tapa inferior justo encima del suelo,
        /// arista por arista. Son dos superficies pegadas, y la malla deja de ser estanca.
        ///
        /// No se ve: esa cara está contra la losa, donde no se llega. Y se esquiva sola, porque
        /// dejar un dedo de margen basta. Se documenta aquí, con la forma exacta que lo dispara, en
        /// vez de arreglarlo — quitarle la tapa inferior a la plataforma la dejaría abierta por
        /// abajo en todos los demás casos, que es peor.
        ///
        /// El test afirma el estado REAL de hoy. Si algún día alguien lo cierra, este test falla y
        /// hay que venir a borrarlo: es la señal de que el límite ya no existe.
        /// </summary>
        [Test]
        public void A_platform_flush_with_the_walls_doubles_the_floor()
        {
            var d = Box(6, 5);
            d.holes = new[] { Door(0, 0.2f) };
            var inner = d.InnerContour();

            var pts = new RoomDefinition.PlatformPoint[inner.Length];
            for (int i = 0; i < inner.Length; i++)
                pts[i] = new RoomDefinition.PlatformPoint { position = inner[i], height = 1f };
            d.platforms = new[] { Plat(pts) };

            var m = RoomMeshBuilder.Build(d);
            Assert.IsFalse(ClosedManifold(m, ExpectedOpenEdges(d), out _),
                "the flush-platform limitation is gone — delete this test");
        }

        /// <summary>Una plataforma ENTERA fuera de la sala no deja nada: ni geometría suelta, ni
        /// una cáscara rota por haber emitido medio sólido.</summary>
        [Test]
        public void A_platform_fully_outside_leaves_nothing()
        {
            var with = WithPlatform(Plat(P(40f, 40f, 0f), P(48f, 40f, 0f), P(44f, 48f, 3f)));
            var without = WithPlatform();

            var va = RoomMeshBuilder.Build(with).vertices;
            var vb = RoomMeshBuilder.Build(without).vertices;
            Assert.AreEqual(vb.Length, va.Length, "a platform outside the room left geometry behind");
        }

        // ── alturas ───────────────────────────────────────────────────────────

        /// <summary>La altura del autor se respeta: sin esto, todo lo demás podría estar verde con
        /// una plataforma a ras de suelo, que es geometría que no se ve.</summary>
        [Test]
        public void A_platform_really_reaches_its_height()
        {
            var m = AssertRoom("platform height", WithPlatform(Ramp()));
            float highest = 0f;
            foreach (var v in m.vertices)
                if (v.y < 3.5f) highest = Mathf.Max(highest, v.y);
            Assert.Greater(highest, 2.9f, $"the platform tops out at {highest:F2} instead of 3");
        }

        /// <summary>
        /// La altura en los puntos que INVENTA el recorte tiene que casar con la que puso el autor.
        /// Se comprueba en un vértice del polígono, donde la interpolación debe dar el valor exacto
        /// — si ahí ya se desvía, en el interior no hay nada que salvar.
        /// </summary>
        [Test]
        public void Heights_are_exact_at_the_authored_points()
        {
            var poly = new List<Vector2> { new Vector2(-4f, -4f), new Vector2(4f, -4f), new Vector2(0f, 4f) };
            var hs = new List<float> { 0f, 1f, 3f };

            for (int i = 0; i < poly.Count; i++)
                Assert.AreEqual(hs[i], PolygonClipper.HeightAt(poly[i], poly, hs), 1e-3f,
                    $"height at authored point {i}");
        }

        /// <summary>Y sobre una arista, interpola entre sus dos extremos. Es lo que hace que dos
        /// trozos que compartan borde coincidan y no quede un escalón entre ellos.</summary>
        [Test]
        public void Heights_interpolate_along_an_edge()
        {
            var poly = new List<Vector2> { new Vector2(-4f, -4f), new Vector2(4f, -4f), new Vector2(0f, 4f) };
            var hs = new List<float> { 0f, 2f, 3f };

            var mid = new Vector2(0f, -4f);                    // media arista entre el 0 y el 1
            Assert.AreEqual(1f, PolygonClipper.HeightAt(mid, poly, hs), 0.05f);
        }

        // ── colisión ──────────────────────────────────────────────────────────

        /// <summary>Se sube andando: bajo la superficie pintada tiene que haber sólido.</summary>
        [TestCase(0.3f)]
        [TestCase(0.6f)]
        [TestCase(0.9f)]
        public void You_can_walk_up_a_platform(float f)
        {
            var d = WithPlatform(Ramp());
            var cb = RoomColliderBuilder.Build(d);

            // Del centro de la base hacia el punto alto.
            var from = new Vector2(0f, -4f);
            var to = new Vector2(0f, 4f);
            Vector2 at = Vector2.Lerp(from, to, f);
            float seen = PolygonClipper.HeightAt(at, new List<Vector2>
                { new Vector2(-4f, -4f), new Vector2(4f, -4f), new Vector2(0f, 4f) },
                new List<float> { 0f, 0f, 3f });

            Assert.IsTrue(Inside(cb, new Vector3(at.x, seen - 0.15f, at.y)),
                $"nothing solid under the ramp at {f:P0} (y={seen - 0.15f:F2})");
        }

        /// <summary>Lo que no puede pasar es que la plataforma selle la sala: fuera de su polígono
        /// se sigue andando por el suelo.</summary>
        [Test]
        public void A_platform_does_not_fill_the_room()
        {
            var cb = RoomColliderBuilder.Build(WithPlatform(Ramp()));
            Assert.IsFalse(Inside(cb, new Vector3(11f, 1.7f, 9f)),
                "the platform filled the far corner of the room");
        }

        // ── que nada viejo se mueva ───────────────────────────────────────────

        /// <summary>Sin plataformas, la malla es la EXACTA de antes. Es la garantía de que añadir
        /// el tipo no ha movido ninguna sala ya horneada.</summary>
        [Test]
        public void No_platforms_means_the_old_mesh()
        {
            var withField = Box(5, 4);
            withField.holes = new[] { Door(0, 0.5f) };
            withField.platforms = new RoomDefinition.Platform[0];

            var plain = Box(5, 4);
            plain.holes = new[] { Door(0, 0.5f) };

            var va = RoomMeshBuilder.Build(withField).vertices;
            var vb = RoomMeshBuilder.Build(plain).vertices;

            Assert.AreEqual(vb.Length, va.Length);
            for (int i = 0; i < va.Length; i++)
                Assert.Less(Vector3.Distance(va[i], vb[i]), 1e-5f, $"vertex {i} moved");
        }

        /// <summary>
        /// Formas rotas que el autor va a producir sin querer arrastrando puntos con el ratón: un
        /// polígono que se cruza a sí mismo, tres puntos en línea, dos puntos encima del otro.
        /// Ninguna puede romper la sala — como mucho, no salir.
        /// </summary>
        [Test]
        public void Broken_shapes_do_not_break_the_room()
        {
            AssertRoom("self-crossing", WithPlatform(
                Plat(P(-4f, -4f, 0f), P(4f, 4f, 2f), P(4f, -4f, 0f), P(-4f, 4f, 2f))));
            AssertRoom("collinear", WithPlatform(
                Plat(P(-4f, 0f, 0f), P(0f, 0f, 1f), P(4f, 0f, 2f))));
            AssertRoom("coincident", WithPlatform(
                Plat(P(2f, 2f, 0f), P(2f, 2f, 1f), P(2f, 2f, 2f))));
        }

        /// <summary>Una plataforma a ras de suelo no tiene volumen. No puede dejar una lámina de
        /// caras de área cero: o sale con altura, o no sale.</summary>
        [Test]
        public void A_zero_height_platform_is_harmless() =>
            AssertRoom("zero height", WithPlatform(
                Plat(P(-4f, -4f, 0f), P(4f, -4f, 0f), P(0f, 4f, 0f))));

        /// <summary>
        /// Caras del REVÉS, y solo esas: la normal apuntando al lado contrario que la geometría.
        ///
        /// Es el subconjunto que importa de lo que mira <c>RoomGeometry.WindingOk</c>, que además
        /// marca las caras muy inclinadas respecto a su normal declarada. Una cara del revés se ve
        /// como un agujero desde el lado bueno; una inclinada, no se ve.
        /// </summary>
        /// <summary>
        /// Aristas sin pareja: por ahí se ve el otro lado del mundo. Es el subconjunto GRAVE de lo
        /// que mira <c>RoomGeometry.ClosedManifold</c>, que además cuenta las duplicadas — dos
        /// caras pegadas, que quedan dentro del sólido y no se ven.
        ///
        /// Se cuenta por posición redondeada al milímetro, igual que la comprobación compartida:
        /// dos vértices calculados por caminos distintos casi nunca salen idénticos bit a bit.
        /// </summary>
        private static int OpenEdges(Mesh m)
        {
            var use = new Dictionary<(long, long), int>();
            var v = m.vertices;

            (long, long) Key(Vector3 p) =>
                ((long)Mathf.Round(p.x * 1000f) * 1_000_000L + (long)Mathf.Round(p.y * 1000f),
                 (long)Mathf.Round(p.z * 1000f));

            void Count(Vector3 a, Vector3 b)
            {
                var ka = Key(a);
                var kb = Key(b);
                var key = ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
                var flat = (key.Item1.Item1 * 31L + key.Item1.Item2,
                            key.Item2.Item1 * 31L + key.Item2.Item2);
                use.TryGetValue(flat, out int n);
                use[flat] = n + 1;
            }

            for (int s = 0; s < m.subMeshCount; s++)
            {
                var t = m.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                {
                    Count(v[t[i]], v[t[i + 1]]);
                    Count(v[t[i + 1]], v[t[i + 2]]);
                    Count(v[t[i + 2]], v[t[i]]);
                }
            }

            int open = 0;
            foreach (var kv in use) if (kv.Value == 1) open++;
            return open;
        }

        private static int InvertedFaces(Mesh m)
        {
            int bad = 0;
            var v = m.vertices;
            var n = m.normals;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var t = m.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 geo = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                    if (geo.sqrMagnitude < 1e-12f) continue;
                    if (Vector3.Dot(geo.normalized, n[t[i]]) < -0.5f) bad++;
                }
            }
            return bad;
        }
    }
}
