using BackroomsSurvival.Gameplay.Building;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-081 fase 1 — las dos mitades PURAS de la puerta de territorio del cliente: a qué columna
    /// de chunk pertenece una posición, y qué `zone_kind` es construible.
    ///
    /// `BuildPermission.CanBuildAt` entero queda FUERA de alcance a propósito: depende de
    /// <c>ZoneRegistry</c>, que se puebla del snapshot IPC en vivo y no es algo que fingir en un
    /// fixture headless. Misma frontera declarada que `ChunkLootRollTests` para su propia puerta de
    /// zona. Lo que sí se prueba aquí es todo lo que puede romperse en silencio sin Play.
    /// </summary>
    [TestFixture]
    public class BuildPermissionTests
    {
        /// <summary>Espejo de `ZONE_OFFICE`, el último zone_kind del backend
        /// (backend/src/world/chunk/surface_profiles.rs).</summary>
        private const byte LastZoneKind = 12;

        /// <summary>
        /// La regla completa, enumerada: `ZONE_SAFE` (2) construye y los otros doce NO. Escrito como
        /// barrido y no como un par de casos sueltos porque el fallo que importa es que alguien
        /// abra la puerta a una zona más sin decirlo.
        /// </summary>
        [Test]
        public void OnlyTheSafeZoneIsBuildable()
        {
            Assert.IsTrue(BuildPermission.IsBuildableZone(BuildPermission.BuildableZoneKind),
                "ZONE_SAFE es la zona construible de ADR-081");

            for (byte zoneKind = 0; zoneKind <= LastZoneKind; zoneKind++)
            {
                if (zoneKind == BuildPermission.BuildableZoneKind)
                    continue;

                Assert.IsFalse(BuildPermission.IsBuildableZone(zoneKind),
                    $"zone_kind {zoneKind} no es construible");
            }
        }

        /// <summary>
        /// El espejo del backend. `ZONE_SAFE` vale 2 en `surface_profiles.rs`; si allí cambiara y
        /// aquí no, el cliente avisaría de la zona equivocada mientras el host aplica la correcta —
        /// un desacuerdo sin excepción y sin log, que es la peor clase.
        /// </summary>
        [Test]
        public void BuildableZoneKindMirrorsBackendZoneSafe()
        {
            Assert.AreEqual(2, BuildPermission.BuildableZoneKind);
        }

        /// <summary>
        /// ADR-081 fase 3 — EL GUARDIÁN DEL `def_id` DEL MARCADOR, y el más valioso de este fichero.
        ///
        /// El id del marcador está hardcodeado en TRES sitios que tienen que coincidir: el asset que
        /// lo genera, `BuildPermission.ClaimMarkerDefId` (cliente) y `CLAIM_MARKER_DEF_ID`
        /// (backend/src/game_loop.rs). Si alguien regenera la definición, minta un id nuevo y las
        /// otras dos constantes dejan de casar: el marcador se vuelve un poste decorativo, nadie
        /// puede reclamar nada y NO hay ningún error — simplemente todo se rechaza. Este test lo
        /// convierte en un rojo inmediato leyendo el asset de verdad.
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

        /// <summary>
        /// La trampa real de esta función: al oeste/norte del origen las coordenadas son negativas, y
        /// un cast a int (truncado hacia cero) mandaría todo el intervalo (-50, 50) al chunk 0. Media
        /// zona construible leería la zona de su vecina.
        /// </summary>
        [Test]
        public void ChunkOfFloorsInsteadOfTruncating()
        {
            Assert.AreEqual((0, 0), BuildPermission.ChunkOf(new Vector3(0f, 0f, 0f)));
            Assert.AreEqual((0, 0), BuildPermission.ChunkOf(new Vector3(49.9f, 0f, 49.9f)));
            Assert.AreEqual((1, 1), BuildPermission.ChunkOf(new Vector3(50f, 0f, 50f)));

            // Sin FloorToInt estos tres darían (0,0) y (0,-1)/(−1,0) respectivamente.
            Assert.AreEqual((-1, -1), BuildPermission.ChunkOf(new Vector3(-0.1f, 0f, -0.1f)));
            Assert.AreEqual((-1, -1), BuildPermission.ChunkOf(new Vector3(-49.9f, 0f, -49.9f)));
            Assert.AreEqual((-2, -2), BuildPermission.ChunkOf(new Vector3(-50.1f, 0f, -50.1f)));
        }

        /// <summary>
        /// La altura no entra en la cuenta: la columna es cosa de XZ, y la zona se resuelve en la capa
        /// 0 tanto aquí como en el host (`position_is_buildable`). Es lo que mantiene en fase el aviso
        /// del cliente y la decisión del backend.
        /// </summary>
        [Test]
        public void ChunkOfIgnoresHeight()
        {
            Assert.AreEqual(
                BuildPermission.ChunkOf(new Vector3(10f, 0f, 20f)),
                BuildPermission.ChunkOf(new Vector3(10f, 128f, 20f)));
        }

        /// <summary>
        /// Enmienda 3 — la rejilla de claims. Misma trampa del `floor` que en `ChunkOf` y con la
        /// misma consecuencia: truncando hacia cero, el bloque del origen mediría 30 m de lado y se
        /// comería el de sus vecinos al oeste y al norte.
        ///
        /// Espejo EXACTO de `claim_blocks_west_and_north_of_the_origin_do_not_swallow_their_neighbour`
        /// (backend/src/game_loop/tests.rs): los mismos seis puntos, los mismos seis bloques. Si las
        /// dos puntas divergen, el cliente pinta un borde donde el host no lo aplica.
        /// </summary>
        [Test]
        public void ClaimBlockOfMirrorsTheBackendGrid()
        {
            Assert.AreEqual(10f, BuildPermission.ClaimBlockMeters, "espejo de CLAIM_BLOCK_M");

            Assert.AreEqual((0, 0), BuildPermission.ClaimBlockOf(new Vector3(1f, 0f, 1f)));
            Assert.AreEqual((0, 0), BuildPermission.ClaimBlockOf(new Vector3(9.9f, 0f, 9.9f)));
            Assert.AreEqual((1, 1), BuildPermission.ClaimBlockOf(new Vector3(10.1f, 0f, 10.1f)));
            Assert.AreEqual((-1, -1), BuildPermission.ClaimBlockOf(new Vector3(-0.1f, 0f, -0.1f)));
            Assert.AreEqual((-1, -1), BuildPermission.ClaimBlockOf(new Vector3(-9.9f, 0f, -9.9f)));
            Assert.AreEqual((-2, -2), BuildPermission.ClaimBlockOf(new Vector3(-10.1f, 0f, -10.1f)));
        }

        /// <summary>
        /// El bloque encaja exacto en el chunk de 50 m, que es la razón de ser de los 10 m frente a
        /// los 15 de la enmienda anterior. Espejo de `the_claim_block_tiles_the_chunk_exactly`.
        /// </summary>
        [Test]
        public void TheClaimBlockTilesTheChunkExactly()
        {
            float blocksPerChunk = BackroomsSurvival.Net.SprayMsg.ChunkSize / BuildPermission.ClaimBlockMeters;

            Assert.AreEqual(5f, blocksPerChunk,
                "un bloque que no divida al chunk deja claims a caballo de dos chunks");
        }

        /// <summary>La altura tampoco cuenta para el bloque: un claim es una casilla de suelo.</summary>
        [Test]
        public void ClaimBlockOfIgnoresHeight()
        {
            Assert.AreEqual(
                BuildPermission.ClaimBlockOf(new Vector3(52.5f, 0f, 52.5f)),
                BuildPermission.ClaimBlockOf(new Vector3(52.5f, 30f, 52.5f)));
        }

        /// <summary>El borde que dibuja el círculo/cuadrado sale de aquí, así que una esquina mal
        /// puesta pinta un territorio que no coincide con el que aplica el host.</summary>
        [Test]
        public void ClaimBlockOriginIsTheLowCorner()
        {
            Assert.AreEqual(new Vector3(0f, 0f, 10f), BuildPermission.ClaimBlockOrigin(0, 1));
            Assert.AreEqual(new Vector3(-10f, 0f, -10f), BuildPermission.ClaimBlockOrigin(-1, -1));
        }
    }
}
