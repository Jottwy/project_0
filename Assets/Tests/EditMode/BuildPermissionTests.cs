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
    }
}
