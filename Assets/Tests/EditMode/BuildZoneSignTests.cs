using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-081 enmienda 5 — el cartel es la única forma que tiene el jugador de saber que una sala
    /// se puede edificar, así que lo que se prueba aquí no es decorado: que la señal aparezca donde
    /// debe, no aparezca donde no, y sea la misma en todas las máquinas.
    /// </summary>
    [TestFixture]
    public class BuildZoneSignTests
    {
        /// <summary>Un chunk SIN habitación: es lo que dice el backend en la inmensa mayoría.</summary>
        private const int NoRoom = -1;

        private Transform _root;

        [SetUp]
        public void SetUp() => _root = new GameObject("SignTestRoot").transform;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.DestroyImmediate(_root.gameObject);
        }

        /// <summary>Rejilla donde todos los tiles tienen pared sur y ninguno es macizo ni tiene
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
        public void NoSignInAChunkWithoutARoom()
        {
            BuildZoneSign.Place(_root, OpenTilesWithSouthWall(), NoRoom, NoRoom, 0, 0);

            Assert.AreEqual(0, SignCount(),
                "un cartel de 'se puede construir' donde NO se puede es peor que no tener cartel");
        }

        [Test]
        public void ARoomGetsSignsAndNeverMoreThanTwo()
        {
            BuildZoneSign.Place(_root, OpenTilesWithSouthWall(), 3, 4, 0, 0);

            int count = SignCount();
            Assert.Greater(count, 0, "una habitación sin ningún cartel deja la regla invisible");
            Assert.LessOrEqual(count, 2, "más de dos es decorado repetido, no señalización");
        }

        /// <summary>
        /// Los carteles cuelgan DENTRO de los 3 × 3 tiles de la sala. Antes de la enmienda 5 barrían
        /// el chunk entero, y con la habitación tallada eso los dejaría por los pasillos de fuera,
        /// anunciando que se puede construir justo donde no.
        /// </summary>
        [Test]
        public void SignsHangInsideTheRoomAndNowhereElse()
        {
            const int tileX = 3, tileZ = 4;
            BuildZoneSign.Place(_root, OpenTilesWithSouthWall(), tileX, tileZ, 0, 0);

            float tile = GridVisualConstants.TileSize;
            float minX = tileX * tile, minZ = tileZ * tile;
            float side = GridChunkDataMsg_BuildRoomTiles * tile;

            Assert.Greater(_root.childCount, 0);
            for (int i = 0; i < _root.childCount; i++)
            {
                var p = _root.GetChild(i).localPosition;
                Assert.GreaterOrEqual(p.x, minX - tile, $"cartel {i} al oeste de la sala");
                Assert.LessOrEqual(p.x, minX + side + tile, $"cartel {i} al este de la sala");
                Assert.GreaterOrEqual(p.z, minZ - tile, $"cartel {i} al sur de la sala");
                Assert.LessOrEqual(p.z, minZ + side + tile, $"cartel {i} al norte de la sala");
            }
        }

        /// <summary>Espejo local de `GridChunkDataMsg.BuildRoomTiles` para no arrastrar el
        /// namespace de red a este fixture.</summary>
        private const int GridChunkDataMsg_BuildRoomTiles = 3;

        /// <summary>
        /// El cartel se deriva de datos que todos los clientes reciben igual: si dos derivasen
        /// distinto, un jugador vería la señal donde otro no.
        /// </summary>
        [Test]
        public void SignPlacementIsDeterministicForTheSameRoom()
        {
            var walls = OpenTilesWithSouthWall();
            BuildZoneSign.Place(_root, walls, 2, 5, 3, -7);
            var first = Poses();

            var second = new GameObject("SecondPass").transform;
            try
            {
                BuildZoneSign.Place(second, walls, 2, 5, 3, -7);

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
        /// Un tile macizo no tiene interior donde estar de pie, y uno sin aristas no tiene pared
        /// donde colgar nada. Sin estos dos rechazos el cartel acaba dentro de la geometría.
        /// </summary>
        [Test]
        public void SolidTilesAndTilesWithoutWallsAreRejected()
        {
            int n = GridChunkBuilder.TilesPerChunk;

            var solid = new byte[n, n];
            for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++)
                    solid[x, z] = 0x0F;
            BuildZoneSign.Place(_root, solid, 3, 4, 0, 0);
            Assert.AreEqual(0, SignCount(), "un tile macizo no puede alojar un cartel");

            var open = new byte[n, n]; // todo a 0: sin una sola pared
            BuildZoneSign.Place(_root, open, 3, 4, 0, 0);
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
