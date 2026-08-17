using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Enmienda a ADR-081 — el cartel de "zona construible" es la ÚNICA forma que tiene el jugador
    /// de saber que una sala se puede edificar, así que lo que se prueba aquí no es decorado: es que
    /// la señal aparezca donde debe, no aparezca donde no, y sea la misma en todas las máquinas.
    /// </summary>
    [TestFixture]
    public class BuildZoneSignTests
    {
        /// <summary>Espejo de `ZONE_NORMAL` — cualquier zona que no sea la construible.</summary>
        private const int ZoneNormal = 0;

        private Transform _root;

        [SetUp]
        public void SetUp() => _root = new GameObject("SignTestRoot").transform;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.DestroyImmediate(_root.gameObject);
        }

        /// <summary>Rejilla de tiles donde todos tienen pared sur y ninguno es macizo ni tiene
        /// columna: el caso más favorable posible, para que un cero signifique "no coloca" y no
        /// "no había sitio".</summary>
        private static byte[,] OpenTilesWithSouthWall()
        {
            int n = GridChunkBuilder.TilesPerChunk;
            var walls = new byte[n, n];
            for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++)
                    walls[x, z] = GridChunkBuilder.EdgeSouth;
            return walls;
        }

        private int SignCount() => _root.childCount;

        [Test]
        public void NoSignOutsideABuildableZone()
        {
            BuildZoneSign.Place(_root, OpenTilesWithSouthWall(), ZoneNormal, 0, 0);

            Assert.AreEqual(0, SignCount(),
                "un cartel de 'se puede construir' en una zona donde NO se puede es peor que no tener cartel");
        }

        /// <summary>
        /// El chunk (0,0) es zona segura en el seed por defecto y es donde aparece el jugador, así
        /// que es el primer sitio donde tiene que ver la señal.
        /// </summary>
        [Test]
        public void ABuildableZoneGetsSignsAndNeverMoreThanTwo()
        {
            BuildZoneSign.Place(_root, OpenTilesWithSouthWall(), BuildZoneSign.ZoneSafe, 0, 0);

            int count = SignCount();
            Assert.Greater(count, 0, "una sala construible sin ningún cartel deja la regla invisible");
            Assert.LessOrEqual(count, 2, "más de dos por chunk es decorado repetido, no señalización");
        }

        /// <summary>
        /// El cartel se deriva por hash puro de las coordenadas del chunk, sin wire: si dos clientes
        /// no derivasen lo mismo, un jugador vería la señal donde otro no. Esto es lo que hace que
        /// no haga falta protocolo.
        /// </summary>
        [Test]
        public void SignPlacementIsDeterministicForTheSameChunk()
        {
            var walls = OpenTilesWithSouthWall();
            BuildZoneSign.Place(_root, walls, BuildZoneSign.ZoneSafe, 3, -7);
            var first = Poses();

            var second = new GameObject("SecondPass").transform;
            try
            {
                BuildZoneSign.Place(second, walls, BuildZoneSign.ZoneSafe, 3, -7);

                Assert.AreEqual(first.Length, second.childCount, "distinto número de carteles");
                for (int i = 0; i < first.Length; i++)
                {
                    Assert.AreEqual(first[i].pos, second.GetChild(i).localPosition, $"cartel {i}: posición");
                    Assert.AreEqual(first[i].rot, second.GetChild(i).localRotation.eulerAngles.y, 0.01f,
                        $"cartel {i}: orientación");
                }
            }
            finally
            {
                Object.DestroyImmediate(second.gameObject);
            }
        }

        /// <summary>
        /// Un tile macizo (las cuatro aristas con pared) no tiene interior donde estar de pie, y uno
        /// sin ninguna arista no tiene pared donde colgar nada. Los dos rechazos son los mismos que
        /// aplican props y escalera, y sin ellos el cartel acaba dentro de la geometría.
        /// </summary>
        [Test]
        public void SolidTilesAndTilesWithoutWallsAreRejected()
        {
            int n = GridChunkBuilder.TilesPerChunk;

            var solid = new byte[n, n];
            for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++)
                    solid[x, z] = 0x0F;
            BuildZoneSign.Place(_root, solid, BuildZoneSign.ZoneSafe, 0, 0);
            Assert.AreEqual(0, SignCount(), "un tile macizo no puede alojar un cartel");

            var open = new byte[n, n]; // todo a 0: sin una sola pared
            BuildZoneSign.Place(_root, open, BuildZoneSign.ZoneSafe, 0, 0);
            Assert.AreEqual(0, SignCount(), "sin pared no hay dónde colgarlo");
        }

        /// <summary>La chapa no lleva collider: cuelga a la altura de los ojos justo en la sala donde
        /// el jugador se pasa el rato construyendo, y ahí un collider es un estorbo permanente.</summary>
        [Test]
        public void TheSignHasNoCollider()
        {
            var sign = BuildZoneSign.Build(_root, Vector3.zero, 0f);

            Assert.IsEmpty(sign.GetComponentsInChildren<Collider>(true), "el cartel es decorado, no obstáculo");
        }

        private (Vector3 pos, float rot)[] Poses()
        {
            var poses = new (Vector3, float)[_root.childCount];
            for (int i = 0; i < poses.Length; i++)
            {
                var child = _root.GetChild(i);
                poses[i] = (child.localPosition, child.localRotation.eulerAngles.y);
            }
            return poses;
        }
    }
}
