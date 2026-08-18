using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Gameplay.Building;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-081 enmienda 5 — las mitades PURAS de la puerta de territorio del cliente: a qué columna
    /// de chunk pertenece una posición, qué identifica un claim, y el registro de habitaciones que
    /// el backend manda con cada chunk.
    ///
    /// `CanPlaceAt` entero queda fuera de alcance a propósito: la parte que falta depende de
    /// `IPCClient.LatestState` (los marcadores replicados), que es Play-only. Misma frontera
    /// declarada que `ChunkLootRollTests`. Lo que sí se prueba aquí es todo lo que puede romperse en
    /// silencio sin Play — y el registro de habitaciones SÍ entra, porque se puebla de un mensaje
    /// que se puede fabricar a mano.
    /// </summary>
    [TestFixture]
    public class BuildPermissionTests
    {
        [SetUp]
        public void ResetRegistry() => BuildRoomRegistry.Clear_EditorTestsOnly();

        private static GridChunkDataMsg ChunkWithRoom(int cx, int cz, int tileX, int tileZ)
        {
            return new GridChunkDataMsg
            {
                cx = cx,
                cz = cz,
                layer = 0,
                hasBuildRoom = true,
                buildRoomTileX = tileX,
                buildRoomTileZ = tileZ,
                buildRoomDoorSide = 0,
            };
        }

        /// <summary>
        /// El espejo del backend. `ROOM_SIZE_M` vale 15 en `build_rooms.rs`; si allí cambiara y aquí
        /// no, el cliente pintaría un borde y aplicaría un rectángulo que el host no aplica — un
        /// desacuerdo sin excepción y sin log, la peor clase.
        /// </summary>
        [Test]
        public void RoomSizeMirrorsTheBackend()
        {
            Assert.AreEqual(15f, BuildRoomRegistry.RoomSizeMeters);
            Assert.AreEqual(3, GridChunkDataMsg.BuildRoomTiles);
        }

        /// <summary>
        /// El rectángulo de la habitación sale de (chunk, tile) y tiene que caer donde el backend la
        /// talló. Se comprueban las cuatro esquinas y un metro fuera de cada una: es donde un error
        /// de medio tile se nota y donde no se notaría probando solo el centro.
        /// </summary>
        [Test]
        public void TheRoomRectMatchesChunkAndTileOrigin()
        {
            // Chunk (2,3) → esquina en (100, 150); tile (2,4) → +10 m en x, +20 m en z.
            BuildRoomRegistry.Observe(ChunkWithRoom(2, 3, 2, 4));

            const float minX = 2 * 50f + 2 * 5f;   // 110
            const float minZ = 3 * 50f + 4 * 5f;   // 170
            float side = BuildRoomRegistry.RoomSizeMeters;

            Assert.IsTrue(BuildRoomRegistry.Contains(new Vector3(minX + 0.5f, 0f, minZ + 0.5f)));
            Assert.IsTrue(BuildRoomRegistry.Contains(new Vector3(minX + side - 0.5f, 0f, minZ + side - 0.5f)));

            Assert.IsFalse(BuildRoomRegistry.Contains(new Vector3(minX - 0.5f, 0f, minZ + 0.5f)));
            Assert.IsFalse(BuildRoomRegistry.Contains(new Vector3(minX + 0.5f, 0f, minZ - 0.5f)));
            Assert.IsFalse(BuildRoomRegistry.Contains(new Vector3(minX + side + 0.5f, 0f, minZ + 0.5f)));
            Assert.IsFalse(BuildRoomRegistry.Contains(new Vector3(minX + 0.5f, 0f, minZ + side + 0.5f)));
        }

        /// <summary>
        /// Un chunk que no trae la clave no crea habitación. Es lo que hace que el resto del mundo
        /// sea no construible por DEFECTO en vez de por una comprobación que alguien pueda olvidar.
        /// </summary>
        [Test]
        public void AChunkWithoutARoomRegistersNothing()
        {
            BuildRoomRegistry.Observe(new GridChunkDataMsg { cx = 0, cz = 0, layer = 0 });

            Assert.AreEqual(0, BuildRoomRegistry.KnownRoomCount);
            Assert.IsFalse(BuildRoomRegistry.Contains(new Vector3(25f, 0f, 25f)));
        }

        /// <summary>
        /// Las habitaciones solo existen en la capa 0. Un chunk de otra capa que trajera la clave no
        /// debe registrar nada: la columna se identifica por (cx,cz), así que aceptarlo sobrescribiría
        /// lo que se sabe del suelo con datos de un piso distinto.
        /// </summary>
        [Test]
        public void OnlyLayerZeroRoomsAreRegistered()
        {
            var upstairs = ChunkWithRoom(0, 0, 2, 2);
            upstairs.layer = 1;

            BuildRoomRegistry.Observe(upstairs);

            Assert.AreEqual(0, BuildRoomRegistry.KnownRoomCount);
        }

        /// <summary>La altura no cuenta: la habitación es una superficie de suelo, igual que en el
        /// host, donde la puerta se resuelve solo con XZ.</summary>
        [Test]
        public void TheRoomRectIgnoresHeight()
        {
            BuildRoomRegistry.Observe(ChunkWithRoom(0, 0, 2, 2));
            var onFloor = new Vector3(12f, 0f, 12f);

            Assert.AreEqual(
                BuildRoomRegistry.Contains(onFloor),
                BuildRoomRegistry.Contains(new Vector3(onFloor.x, 128f, onFloor.z)));
        }

        /// <summary>
        /// La esquina que se le da al dibujo del borde es la misma que usa la comprobación. Si
        /// divergieran, el jugador vería un rectángulo y el host aplicaría otro.
        /// </summary>
        [Test]
        public void TheDrawnOriginIsTheRectOrigin()
        {
            BuildRoomRegistry.Observe(ChunkWithRoom(1, 1, 3, 3));

            Assert.IsTrue(BuildRoomRegistry.TryGetRoomOrigin(new Vector3(70f, 0f, 70f), out var origin));
            Assert.AreEqual(new Vector3(65f, 0f, 65f), origin);
            Assert.IsTrue(BuildRoomRegistry.Contains(origin + new Vector3(0.5f, 0f, 0.5f)));
        }

        /// <summary>
        /// El claim es la HABITACIÓN, y como hay como mucho una por chunk, la coordenada de chunk lo
        /// identifica. Espejo de `claim_key` (backend/src/game_loop.rs): dos puntos de la misma sala
        /// son el mismo claim, y uno del chunk de al lado no.
        /// </summary>
        [Test]
        public void ClaimKeyIsTheChunkOfTheRoom()
        {
            Assert.AreEqual(
                BuildPermission.ClaimKeyOf(new Vector3(12f, 0f, 20f)),
                BuildPermission.ClaimKeyOf(new Vector3(24f, 30f, 33f)));

            Assert.AreNotEqual(
                BuildPermission.ClaimKeyOf(new Vector3(49f, 0f, 20f)),
                BuildPermission.ClaimKeyOf(new Vector3(51f, 0f, 20f)));
        }

        /// <summary>
        /// La trampa real de `ChunkOf`: al oeste/norte del origen las coordenadas son negativas, y un
        /// cast a int (truncado hacia cero) mandaría todo el intervalo (-50, 50) al chunk 0. Media
        /// zona leería el chunk de su vecina.
        /// </summary>
        [Test]
        public void ChunkOfFloorsInsteadOfTruncating()
        {
            Assert.AreEqual((0, 0), BuildPermission.ChunkOf(new Vector3(0f, 0f, 0f)));
            Assert.AreEqual((0, 0), BuildPermission.ChunkOf(new Vector3(49.9f, 0f, 49.9f)));
            Assert.AreEqual((1, 1), BuildPermission.ChunkOf(new Vector3(50f, 0f, 50f)));

            // Sin FloorToInt estos tres darían (0,0).
            Assert.AreEqual((-1, -1), BuildPermission.ChunkOf(new Vector3(-0.1f, 0f, -0.1f)));
            Assert.AreEqual((-1, -1), BuildPermission.ChunkOf(new Vector3(-49.9f, 0f, -49.9f)));
            Assert.AreEqual((-2, -2), BuildPermission.ChunkOf(new Vector3(-50.1f, 0f, -50.1f)));
        }

        /// <summary>La altura no entra en la columna tampoco.</summary>
        [Test]
        public void ChunkOfIgnoresHeight()
        {
            Assert.AreEqual(
                BuildPermission.ChunkOf(new Vector3(10f, 0f, 20f)),
                BuildPermission.ChunkOf(new Vector3(10f, 128f, 20f)));
        }

        /// <summary>
        /// `ResetForNewConnection` es lo que hace que reconectar a otro mundo (seed distinta) sin
        /// reiniciar Unity no deje salas fantasma — sin esto, solo `ResetStatics` (una vez por
        /// proceso) vacía el registro.
        /// </summary>
        [Test]
        public void ResetForNewConnectionClearsKnownRooms()
        {
            BuildRoomRegistry.Observe(ChunkWithRoom(5, 5, 2, 2));
            Assert.AreEqual(1, BuildRoomRegistry.KnownRoomCount);

            BuildRoomRegistry.ResetForNewConnection();

            Assert.AreEqual(0, BuildRoomRegistry.KnownRoomCount);
            Assert.IsFalse(BuildRoomRegistry.Contains(new Vector3(5 * 50f + 12f, 0f, 5 * 50f + 12f)));
        }

        /// <summary>
        /// EL GUARDIÁN DEL `def_id` DEL MARCADOR. El id está hardcodeado en TRES sitios que tienen
        /// que coincidir: el asset que lo genera, `BuildPermission.ClaimMarkerDefId` (cliente) y
        /// `CLAIM_MARKER_DEF_ID` (backend/src/game_loop.rs). Si alguien regenera la definición,
        /// minta un id nuevo y las otras dos constantes dejan de casar: el marcador se vuelve un
        /// poste decorativo, nadie puede reclamar nada y NO hay ningún error — simplemente todo se
        /// rechaza. Este test lo convierte en un rojo inmediato leyendo el asset de verdad.
        /// </summary>
        [Test]
        public void ClaimMarkerDefIdMatchesTheAuthoredDefinition()
        {
            var definition = Resources.Load<PolymindGames.BuildingSystem.BuildingPieceDefinition>(
                "Definitions/BuildingPiece/BR_Claim Marker");

            Assert.IsNotNull(definition,
                "no está el asset 'BR_Claim Marker' — sin él no se puede reclamar terreno. " +
                "Ejecuta \"Backrooms ▸ Create Building Pieces\".");
            Assert.AreEqual(BuildPermission.ClaimMarkerDefId, definition.Id,
                "el def_id del marcador cambió. Actualiza BuildPermission.ClaimMarkerDefId Y " +
                "CLAIM_MARKER_DEF_ID en backend/src/game_loop.rs, en el mismo commit.");
        }
    }
}
