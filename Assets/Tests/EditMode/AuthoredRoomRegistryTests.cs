using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-085 — el registro de salas autoradas guarda POR CAPA, no por columna de chunk.
    ///
    /// Antes de ADR-085 el registro rechazaba todo lo que no fuera la capa 0, porque era la única en
    /// la que el backend tallaba. Una sala más alta que una capa llega también en el payload de las
    /// capas que invade: esas capas la necesitan para NO pintarle geometría encima, y una capa que
    /// no la tenga le planta su laberinto dentro.
    ///
    /// Se prueba aquí y no en Play porque el registro se puebla de un mensaje que se fabrica a mano
    /// —misma frontera que <see cref="BuildPermissionTests"/>—, y porque el modo de fallo es
    /// silencioso: no peta nada, simplemente aparece un muro dentro de la sala.
    /// </summary>
    [TestFixture]
    public class AuthoredRoomRegistryTests
    {
        [SetUp]
        public void ResetRegistry() => AuthoredRoomRegistry.Clear_EditorTestsOnly();

        private static GridChunkDataMsg ChunkWithRoom(int cx, int cz, byte layer, int entry)
        {
            return new GridChunkDataMsg
            {
                cx = cx,
                cz = cz,
                layer = layer,
                authoredRooms = new[]
                {
                    new GridChunkDataMsg.AuthoredRoom
                    {
                        tileX = 2, tileZ = 3, entry = entry, quarter = 0,
                    },
                },
            };
        }

        /// LO QUE ADR-085 NECESITA: la capa invadida tiene que poder preguntar por la sala.
        [Test]
        public void A_room_that_arrives_for_an_upper_layer_is_kept_for_that_layer()
        {
            AuthoredRoomRegistry.Observe(ChunkWithRoom(4, 5, 1, 0));

            var rooms = AuthoredRoomRegistry.GetRooms(4, 5, 1);
            Assert.IsNotNull(rooms, "la capa invadida no ve la sala: le pintara el laberinto dentro");
            Assert.AreEqual(1, rooms.Length);
            Assert.AreEqual(2, rooms[0].tileX);
        }

        /// Y LA MITAD QUE LO MANTIENE SANO: quien decide en qué capas está la sala es el backend.
        /// El cliente no la extiende hacia arriba por su cuenta, o una sala de una capa taparía el
        /// laberinto de la de encima.
        [Test]
        public void A_room_registered_for_one_layer_does_not_leak_into_the_others()
        {
            AuthoredRoomRegistry.Observe(ChunkWithRoom(4, 5, 0, 0));

            Assert.IsNotNull(AuthoredRoomRegistry.GetRooms(4, 5, 0));
            Assert.IsNull(AuthoredRoomRegistry.GetRooms(4, 5, 1),
                "la sala se colo en una capa a la que el backend no la mando");
            Assert.IsNull(AuthoredRoomRegistry.GetRooms(4, 5, 2));
        }

        /// Las capas de una misma columna son independientes: que llegue una no puede pisar a otra.
        /// Es el fallo que el registro por columna tenia servido — cada chunk nuevo sobrescribia la
        /// entrada de toda la columna.
        [Test]
        public void Layers_of_the_same_column_do_not_overwrite_each_other()
        {
            for (byte layer = 0; layer <= 2; layer++)
                AuthoredRoomRegistry.Observe(ChunkWithRoom(7, 7, layer, 0));

            for (int layer = 0; layer <= 2; layer++)
                Assert.IsNotNull(AuthoredRoomRegistry.GetRooms(7, 7, layer), $"capa {layer} perdida");

            Assert.AreEqual(3, AuthoredRoomRegistry.KnownRoomCount,
                "una sala de tres capas cuenta una vez por capa");
        }

        /// Un chunk sin salas no borra lo que ya se sabia de esa capa: el backend omite el campo
        /// entero cuando no hay ninguna (`skip_serializing_if`), y eso no es "ya no hay sala".
        [Test]
        public void An_empty_chunk_message_does_not_erase_a_known_room()
        {
            AuthoredRoomRegistry.Observe(ChunkWithRoom(1, 1, 0, 0));
            AuthoredRoomRegistry.Observe(new GridChunkDataMsg { cx = 1, cz = 1, layer = 0 });

            Assert.IsNotNull(AuthoredRoomRegistry.GetRooms(1, 1, 0));
        }
    }
}
