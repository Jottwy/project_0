using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-084 punto 5 — el prefab de una sala autorada sobrevive a la descarga de UN chunk.
    ///
    /// Una sala repartida entre hasta cuatro chunks la piden los cuatro; si se destruyera con el
    /// primero que se descarga, el jugador vería desaparecer media sala estando dentro. El modo de
    /// fallo contrario —que no se destruya nunca— es una fuga de memoria en un mundo persistente, y
    /// tampoco avisa.
    ///
    /// Se prueba en EditMode y no en Play porque el refcount es aritmética pura sobre un diccionario:
    /// arrancar el juego entero para verla no aporta nada y cuesta minutos. Lo que sí exige es que
    /// <c>AuthoredRoomInstances</c> destruya con <c>DestroyImmediate</c> fuera de play mode — ver el
    /// porqué en su <c>DestroyRoom</c>.
    /// </summary>
    [TestFixture]
    public class AuthoredRoomInstancesTests
    {
        private GameObject _prefab;

        [SetUp]
        public void Setup()
        {
            AuthoredRoomInstances.ClearAll();
            _prefab = new GameObject("FakeRoomPrefab");
        }

        [TearDown]
        public void Teardown()
        {
            AuthoredRoomInstances.ClearAll();
            if (_prefab != null)
                Object.DestroyImmediate(_prefab);
        }

        private void Acquire((int, int, int) chunk, AuthoredRoomInstances.Key key, bool isAnchor = false)
            => AuthoredRoomInstances.Acquire(chunk, key, _prefab, Vector3.zero, 0f, Color.white, isAnchor);

        /// EL TEST DE LO QUE SE PIDIO: cuatro chunks, UNA sala.
        [Test]
        public void Four_chunks_covering_one_room_instantiate_it_once()
        {
            var key = new AuthoredRoomInstances.Key(3, 7, 4, 6);
            Acquire((3, 7, 0), key, isAnchor: true);
            Acquire((4, 7, 0), key);
            Acquire((3, 8, 0), key);
            Acquire((4, 8, 0), key);

            Assert.AreEqual(1, AuthoredRoomInstances.LiveRoomCount, "se instancio mas de una sala");
            Assert.AreEqual(4, AuthoredRoomInstances.RefCount(key));
        }

        /// Descargar el chunk ANCLA con el jugador dentro no puede borrar la sala: la sostienen los
        /// otros tres. Es la verificacion (c) de ADR-084.
        [Test]
        public void Unloading_the_anchor_chunk_does_not_destroy_the_room()
        {
            var key = new AuthoredRoomInstances.Key(0, 0, 2, 2);
            Acquire((0, 0, 0), key, isAnchor: true);
            Acquire((1, 0, 0), key);

            AuthoredRoomInstances.ReleaseChunk((0, 0, 0));

            Assert.AreEqual(1, AuthoredRoomInstances.LiveRoomCount, "la sala murio con su ancla");
            Assert.AreEqual(1, AuthoredRoomInstances.RefCount(key));
        }

        /// Y el ULTIMO que la suelta si la destruye: si no, es una fuga.
        [Test]
        public void The_last_chunk_to_release_destroys_the_room()
        {
            var key = new AuthoredRoomInstances.Key(0, 0, 2, 2);
            Acquire((0, 0, 0), key, isAnchor: true);
            Acquire((1, 0, 0), key);

            AuthoredRoomInstances.ReleaseChunk((0, 0, 0));
            AuthoredRoomInstances.ReleaseChunk((1, 0, 0));

            Assert.AreEqual(0, AuthoredRoomInstances.LiveRoomCount, "la sala se quedo sin dueno y viva");
            Assert.AreEqual(0, AuthoredRoomInstances.RefCount(key));
        }

        /// <summary>
        /// El caso que rompe un refcount ingenuo: <c>RebuildChunk</c> construye el root NUEVO antes
        /// de destruir el viejo (a proposito, para no dejar al jugador sin suelo), asi que el mismo
        /// chunk pide la sala dos veces seguidas. Sin la guarda, el contador sube a 3 y baja a 2: la
        /// sala no se destruiria jamas.
        /// </summary>
        [Test]
        public void A_rebuild_of_the_same_chunk_does_not_double_count()
        {
            var key = new AuthoredRoomInstances.Key(0, 0, 2, 2);
            Acquire((0, 0, 0), key, isAnchor: true);
            Acquire((0, 0, 0), key, isAnchor: true); // el root nuevo de RebuildChunk

            Assert.AreEqual(1, AuthoredRoomInstances.RefCount(key));

            AuthoredRoomInstances.ReleaseChunk((0, 0, 0));
            Assert.AreEqual(0, AuthoredRoomInstances.LiveRoomCount);
        }

        /// Dos salas distintas del MISMO ancla son dos objetos. La identidad es (ancla, tile), no
        /// solo el ancla — un chunk puede anclar hasta tres.
        [Test]
        public void Two_rooms_of_the_same_anchor_are_two_objects()
        {
            Acquire((0, 0, 0), new AuthoredRoomInstances.Key(0, 0, 2, 2), isAnchor: true);
            Acquire((0, 0, 0), new AuthoredRoomInstances.Key(0, 0, 8, 8), isAnchor: true);

            Assert.AreEqual(2, AuthoredRoomInstances.LiveRoomCount);
        }

        /// Reconectar a otro mundo no puede dejar salas de la seed anterior flotando.
        [Test]
        public void Clear_all_takes_every_room_with_it()
        {
            Acquire((0, 0, 0), new AuthoredRoomInstances.Key(0, 0, 2, 2), isAnchor: true);
            Acquire((5, 5, 0), new AuthoredRoomInstances.Key(5, 5, 4, 4), isAnchor: true);

            AuthoredRoomInstances.ClearAll();

            Assert.AreEqual(0, AuthoredRoomInstances.LiveRoomCount);
            // Y el registro por chunk tambien: soltar despues de limpiar no puede petar ni revivir
            // contadores.
            AuthoredRoomInstances.ReleaseChunk((0, 0, 0));
            Assert.AreEqual(0, AuthoredRoomInstances.LiveRoomCount);
        }
    }
}
