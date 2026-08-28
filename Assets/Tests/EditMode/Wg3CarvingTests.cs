using System.Collections.Generic;
using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// ADR-101 verificación (b) — la resta del cliente deja libre exactamente lo que el vano quita.
    ///
    /// Es la mitad del contrato que no tiene oráculo: el servidor resta la caja sobre su ráster y este
    /// lado la resta sobre los volúmenes, y las dos operaciones tienen que coincidir o el jugador ve
    /// una pared donde no la hay. Aquí se comprueba con álgebra —volumen restado y punto libre— en vez
    /// de con una captura, porque el fallo es justamente el que no se ve en una.
    /// </summary>
    public class Wg3CarvingTests
    {
        /// <summary>Una pared de 6 m × 0,15 m × 3 m de alto, con la cara interior en z = 0.</summary>
        private static List<Wg3Volume> Wall() => new List<Wg3Volume>
        {
            new Wg3Volume
            {
                center = new Vector3(3f, 1.5f, 0.075f),
                size = new Vector3(6f, 3f, 0.15f),
                yawDegrees = 0f,
                kind = Wg3VolumeKind.Wall
            }
        };

        private static Wg3CarveMsg Carve(int xCm, int zCm, int sizeXCm, int sizeZCm,
            int bottomCm, int topCm) => new Wg3CarveMsg
        {
            xCm = xCm,
            zCm = zCm,
            sizeXCm = sizeXCm,
            sizeZCm = sizeZCm,
            bottomYCm = bottomCm,
            topYCm = topCm
        };

        private static float SolidVolume(List<Wg3Volume> volumes)
        {
            float total = 0f;
            foreach (Wg3Volume v in volumes)
                if (v.IsSolid) total += v.size.x * v.size.y * v.size.z;
            return total;
        }

        private static bool AnySolidContains(List<Wg3Volume> volumes, Vector3 point)
        {
            foreach (Wg3Volume v in volumes)
            {
                if (!v.IsSolid) continue;
                Vector3 min = v.center - v.size * 0.5f;
                Vector3 max = v.center + v.size * 0.5f;
                if (point.x > min.x && point.x < max.x
                    && point.y > min.y && point.y < max.y
                    && point.z > min.z && point.z < max.z) return true;
            }
            return false;
        }

        [Test]
        public void A_carve_opens_a_hole_and_leaves_the_rest_of_the_wall()
        {
            // Vano de 2,4 m centrado en x = 3, de 5 cm sobre el suelo hasta 3,2 m, atravesando la
            // pared entera en Z: es la forma exacta que emite `wg3::fill::carve_for`.
            var carves = new List<Wg3CarveMsg> { Carve(180, -50, 240, 100, 5, 320) };
            List<Wg3Volume> carved = Wg3Carving.Apply(Wall(), carves);

            Assert.IsFalse(
                AnySolidContains(carved, new Vector3(3f, 1.5f, 0.075f)),
                "el centro del vano sigue macizo: la puerta se dibuja cerrada y el servidor deja pasar");

            // Los dos extremos de la pared siguen ahí. Sin esto, un fallo que se llevara la pared
            // entera pasaría el test de arriba con nota.
            Assert.IsTrue(AnySolidContains(carved, new Vector3(0.5f, 1.5f, 0.075f)), "falta la jamba izquierda");
            Assert.IsTrue(AnySolidContains(carved, new Vector3(5.5f, 1.5f, 0.075f)), "falta la jamba derecha");

            // Y la guarda de suelo: los 5 cm de abajo NO se excavan, o el vano se lleva la losa sobre
            // la que se anda y abre un agujero por el que se cae.
            Assert.IsTrue(
                AnySolidContains(carved, new Vector3(3f, 0.02f, 0.075f)),
                "se excavó por debajo de la guarda de suelo");
        }

        [Test]
        public void The_carve_removes_exactly_the_intersection_and_not_a_gram_more()
        {
            var carves = new List<Wg3CarveMsg> { Carve(180, -50, 240, 100, 5, 320) };
            List<Wg3Volume> original = Wall();
            List<Wg3Volume> carved = Wg3Carving.Apply(original, carves);

            // La intersección real: 2,4 m de ancho × (3,00 − 0,05) de alto × 0,15 m de grosor. El
            // vano pide más profundidad y más altura de las que la pared tiene, y de eso sólo cuenta
            // lo que se solapa.
            float expected = 2.4f * (3f - 0.05f) * 0.15f;
            float removed = SolidVolume(original) - SolidVolume(carved);

            Assert.AreEqual(expected, removed, 1e-4f,
                "la resta no quita exactamente la intersección: o deja muro dentro del vano o se " +
                "come pared de más, y las dos cosas divergen del ráster del servidor");
        }

        [Test]
        public void A_carve_that_misses_leaves_the_geometry_untouched()
        {
            // Diez metros más allá de la pared: no la toca.
            var carves = new List<Wg3CarveMsg> { Carve(1800, -50, 240, 100, 5, 320) };
            List<Wg3Volume> original = Wall();
            List<Wg3Volume> carved = Wg3Carving.Apply(original, carves);

            Assert.AreEqual(1, carved.Count, "un vano que no toca nada partió geometría igualmente");
            Assert.AreEqual(SolidVolume(original), SolidVolume(carved), 1e-5f);
        }

        /// <summary>
        /// **Restar dos veces la misma caja tiene que dar lo mismo que restarla una.**
        ///
        /// No es una curiosidad: ADR-101 D3 manda los vanos a todos los chunks que TOCAN, así que uno
        /// que caiga en una frontera llega dos veces. La idempotencia es la razón de que eso sea
        /// barato y de que viaje la caja en vez de una referencia.
        /// </summary>
        [Test]
        public void Carving_the_same_box_twice_is_the_same_as_once()
        {
            Wg3CarveMsg c = Carve(180, -50, 240, 100, 5, 320);
            float once = SolidVolume(Wg3Carving.Apply(Wall(), new List<Wg3CarveMsg> { c }));
            float twice = SolidVolume(Wg3Carving.Apply(Wall(), new List<Wg3CarveMsg> { c, c }));

            Assert.AreEqual(once, twice, 1e-5f, "el vano duplicado se comió pared de más");
        }

        /// <summary>
        /// Una pared girada 90° tiene sus lados intercambiados vistos desde el mundo. Omitir esa
        /// corrección deja paredes de grosor equivocado — el fallo que no revienta nada y deja gente
        /// encajada.
        /// </summary>
        [Test]
        public void A_wall_turned_ninety_degrees_is_carved_in_world_axes()
        {
            var turned = new List<Wg3Volume>
            {
                new Wg3Volume
                {
                    // Misma pared, declarada en su eje local y puesta atravesada por el yaw.
                    center = new Vector3(0.075f, 1.5f, 3f),
                    size = new Vector3(6f, 3f, 0.15f),
                    yawDegrees = 90f,
                    kind = Wg3VolumeKind.Wall
                }
            };
            var carves = new List<Wg3CarveMsg> { Carve(-50, 180, 100, 240, 5, 320) };
            List<Wg3Volume> carved = Wg3Carving.Apply(turned, carves);

            Assert.IsFalse(
                AnySolidContains(carved, new Vector3(0.075f, 1.5f, 3f)),
                "el vano no alcanzó a la pared girada: se restó contra sus ejes locales");
            Assert.IsTrue(AnySolidContains(carved, new Vector3(0.075f, 1.5f, 0.5f)), "falta la jamba");
        }
    }
}
