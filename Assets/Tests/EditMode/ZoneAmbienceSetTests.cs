using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-059 — overrides de luz y techo por <c>zone_kind</c>
    /// (<see cref="LayerVisualConfig.TryGetZoneLightSet"/> /
    /// <see cref="LayerVisualConfig.TryGetZoneCeilingSet"/>) — y, desde ADR-066, el de
    /// ambiente (<see cref="LayerVisualConfig.TryGetZoneAmbienceSet"/>), que es el que
    /// por fin hace honor al nombre de este fixture.
    ///
    /// Lo que más importa aquí es la disciplina de los booleanos de override: en estos sets
    /// 0 ES un valor autorable (<c>brokenLampChance = 0</c>, <c>ceilingPanelVariety = 0</c>
    /// son exactamente lo que OFFICE quiere), así que el centinela "0 ⇒ sin cambio" de otros
    /// sets no aplica y un set sin ningún override habilitado debe tratarse como autoría a
    /// medias — NO captura la zona, se sigue buscando.
    /// </summary>
    [TestFixture]
    public class ZoneAmbienceSetTests
    {
        /// <summary>Espejo de `ZONE_OFFICE` (backend/src/world/chunk/surface_profiles.rs).</summary>
        private const int ZoneOffice = 12;
        private const int ZoneNormal = 0;
        /// <summary>Espejo de `ZONE_BLACKOUT` (mismo archivo del backend).</summary>
        private const int ZoneBlackout = 7;
        /// <summary>Espejo de `ZONE_SAFE` (mismo archivo del backend).</summary>
        private const int ZoneSafe = 2;
        private const int ZoneUnknown = -1;

        private LayerVisualConfig _cfg;

        [TearDown]
        public void TearDown()
        {
            if (_cfg != null) Object.DestroyImmediate(_cfg);
            _cfg = null;
        }

        private LayerVisualConfig Config()
        {
            _cfg = ScriptableObject.CreateInstance<LayerVisualConfig>();
            return _cfg;
        }

        private static ZoneLightSet OfficeLight() => new ZoneLightSet
        {
            zoneKind = ZoneOffice,
            overrideLampColor = true,
            lampColor = new Color(0.85f, 0.93f, 1f),
            overrideBrokenLampChance = true,
            brokenLampChance = 0f,
        };

        // ── ZoneLightSet ──────────────────────────────────────────────────────────

        [Test]
        public void LightSet_NullOrEmptyListNeverMatches()
        {
            var cfg = Config();
            Assert.IsFalse(cfg.TryGetZoneLightSet(ZoneOffice, out _), "lista null");
            cfg.zoneLightSets = new ZoneLightSet[0];
            Assert.IsFalse(cfg.TryGetZoneLightSet(ZoneOffice, out _), "lista vacía");
        }

        [Test]
        public void LightSet_SpecificZoneMatchesOnlyItsOwnZone()
        {
            var cfg = Config();
            cfg.zoneLightSets = new[] { OfficeLight() };
            Assert.IsTrue(cfg.TryGetZoneLightSet(ZoneOffice, out var m), "OFFICE debe casar");
            Assert.IsTrue(m.overrideBrokenLampChance);
            Assert.AreEqual(0f, m.brokenLampChance,
                "0 debe sobrevivir el viaje — es el valor que OFFICE autora de verdad");
            Assert.IsFalse(cfg.TryGetZoneLightSet(ZoneNormal, out _),
                "un set de OFFICE no debe capturar ZONE_NORMAL");
        }

        [Test]
        public void LightSet_UnknownZoneOnlyMatchesTheWildcard()
        {
            var cfg = Config();
            cfg.zoneLightSets = new[] { OfficeLight() };
            Assert.IsFalse(cfg.TryGetZoneLightSet(ZoneUnknown, out _),
                "−1 (zona aún desconocida) no puede casar con un set específico: se hornearía " +
                "la luz de una zona equivocada en un chunk blanco");

            var wildcard = OfficeLight();
            wildcard.anyZoneKind = true;
            cfg.zoneLightSets = new[] { wildcard };
            Assert.IsTrue(cfg.TryGetZoneLightSet(ZoneUnknown, out _),
                "un set comodín dice 'cualquier zona' y una desconocida es una de ellas — " +
                "misma regla que WallVariantSet");
        }

        [Test]
        public void LightSet_WithNoOverrideEnabledIsHalfAuthoredAndSkipped()
        {
            var cfg = Config();
            // Set que casa por zona pero sin NINGÚN override habilitado (elemento recién
            // añadido en el Inspector): no debe capturar la zona.
            var half = new ZoneLightSet { zoneKind = ZoneOffice };
            var real = OfficeLight();
            cfg.zoneLightSets = new[] { half, real };
            Assert.IsTrue(cfg.TryGetZoneLightSet(ZoneOffice, out var m),
                "el set a medias debe saltarse y el completo capturar");
            Assert.IsTrue(m.overrideLampColor, "debe devolver el set COMPLETO, no el vacío");
        }

        [Test]
        public void LightSet_FirstMatchWins()
        {
            var cfg = Config();
            var first = OfficeLight();
            first.lampIntensity = 111f; first.overrideLampIntensity = true;
            var second = OfficeLight();
            second.lampIntensity = 222f; second.overrideLampIntensity = true;
            cfg.zoneLightSets = new[] { first, second };
            Assert.IsTrue(cfg.TryGetZoneLightSet(ZoneOffice, out var m));
            Assert.AreEqual(111f, m.lampIntensity,
                "primera coincidencia gana — misma regla hand-ordered que WallPrefabFor");
        }

        // ── ZoneCeilingSet ────────────────────────────────────────────────────────

        [Test]
        public void CeilingSet_ZeroVarietyIsAnAuthorableValue()
        {
            var cfg = Config();
            cfg.ceilingPanelVariety = 0.5f; // la capa tiene variedad
            cfg.zoneCeilingSets = new[]
            {
                new ZoneCeilingSet
                {
                    zoneKind = ZoneOffice,
                    overridePanelVariety = true,
                    ceilingPanelVariety = 0f, // la oficina la apaga: rejilla uniforme
                },
            };
            Assert.IsTrue(cfg.TryGetZoneCeilingSet(ZoneOffice, out var m));
            Assert.IsTrue(m.overridePanelVariety);
            Assert.AreEqual(0f, m.ceilingPanelVariety,
                "variety 0 con override habilitado debe distinguirse de 'sin autorar'");
        }

        [Test]
        public void CeilingSet_WithNoOverrideEnabledIsSkipped()
        {
            var cfg = Config();
            cfg.zoneCeilingSets = new[] { new ZoneCeilingSet { zoneKind = ZoneOffice } };
            Assert.IsFalse(cfg.TryGetZoneCeilingSet(ZoneOffice, out _),
                "un set sin overrides habilitados es autoría a medias, no 'sin cambios'");
        }

        [Test]
        public void CeilingSet_UnknownZoneOnlyMatchesTheWildcard()
        {
            var cfg = Config();
            cfg.zoneCeilingSets = new[]
            {
                new ZoneCeilingSet
                {
                    zoneKind = ZoneOffice,
                    overrideCeilingTint = true,
                    ceilingTint = Color.white,
                },
            };
            Assert.IsFalse(cfg.TryGetZoneCeilingSet(ZoneUnknown, out _), "−1 vs set específico");

            var wc = cfg.zoneCeilingSets[0];
            wc.anyZoneKind = true;
            cfg.zoneCeilingSets = new[] { wc };
            Assert.IsTrue(cfg.TryGetZoneCeilingSet(ZoneUnknown, out _), "−1 vs comodín");
        }

        // ── ZoneAmbienceSet (ADR-066) ─────────────────────────────────────────────

        private static ZoneAmbienceSet BlackoutAmbience() => new ZoneAmbienceSet
        {
            zoneKind = ZoneBlackout,
            overrideAmbientLight = true,
            ambientLight = new Color(0.05f, 0.05f, 0.06f),
            overrideFogDensity = true,
            fogDensity = 0.070f,
        };

        [Test]
        public void AmbienceSet_NullOrEmptyListNeverMatches()
        {
            var cfg = Config();
            Assert.IsFalse(cfg.TryGetZoneAmbienceSet(ZoneBlackout, out _), "lista null");
            cfg.zoneAmbienceSets = new ZoneAmbienceSet[0];
            Assert.IsFalse(cfg.TryGetZoneAmbienceSet(ZoneBlackout, out _), "lista vacía");
        }

        [Test]
        public void AmbienceSet_SpecificZoneMatchesOnlyItsOwnZone()
        {
            var cfg = Config();
            cfg.zoneAmbienceSets = new[] { BlackoutAmbience() };
            Assert.IsTrue(cfg.TryGetZoneAmbienceSet(ZoneBlackout, out var m), "BLACKOUT debe casar");
            Assert.IsTrue(m.overrideFogDensity);
            Assert.AreEqual(0.070f, m.fogDensity, 1e-6f);
            Assert.IsFalse(cfg.TryGetZoneAmbienceSet(ZoneNormal, out _),
                "un set de BLACKOUT no debe capturar ZONE_NORMAL");
        }

        [Test]
        public void AmbienceSet_ZeroFogDensityIsAnAuthorableValue()
        {
            var cfg = Config();
            cfg.fogDensity = 0.04f; // la capa sí tiene niebla
            cfg.zoneAmbienceSets = new[]
            {
                new ZoneAmbienceSet
                {
                    zoneKind = ZoneOffice,
                    overrideFogDensity = true,
                    fogDensity = 0f, // aire limpio: la oficina la apaga del todo
                },
            };
            Assert.IsTrue(cfg.TryGetZoneAmbienceSet(ZoneOffice, out var m));
            Assert.IsTrue(m.overrideFogDensity);
            Assert.AreEqual(0f, m.fogDensity,
                "density 0 con override habilitado debe distinguirse de 'sin autorar' — misma " +
                "razón que brokenLampChance 0 en ZoneLightSet");
        }

        [Test]
        public void AmbienceSet_UnknownZoneOnlyMatchesTheWildcard()
        {
            var cfg = Config();
            cfg.zoneAmbienceSets = new[] { BlackoutAmbience() };
            Assert.IsFalse(cfg.TryGetZoneAmbienceSet(ZoneUnknown, out _),
                "−1 (zona aún desconocida) no puede casar con un set específico: el jugador " +
                "vería el ambiente de una zona equivocada mientras llega la de verdad");

            var wildcard = BlackoutAmbience();
            wildcard.anyZoneKind = true;
            cfg.zoneAmbienceSets = new[] { wildcard };
            Assert.IsTrue(cfg.TryGetZoneAmbienceSet(ZoneUnknown, out _),
                "un set comodín dice 'cualquier zona' y una desconocida es una de ellas");
        }

        [Test]
        public void AmbienceSet_WithNoOverrideEnabledIsHalfAuthoredAndSkipped()
        {
            var cfg = Config();
            var half = new ZoneAmbienceSet { zoneKind = ZoneBlackout };
            var real = BlackoutAmbience();
            cfg.zoneAmbienceSets = new[] { half, real };
            Assert.IsTrue(cfg.TryGetZoneAmbienceSet(ZoneBlackout, out var m),
                "el set a medias debe saltarse y el completo capturar");
            Assert.IsTrue(m.overrideAmbientLight, "debe devolver el set COMPLETO, no el vacío");
        }

        [Test]
        public void AmbienceSet_FirstMatchWins()
        {
            var cfg = Config();
            var first = BlackoutAmbience();
            first.fogDensity = 0.011f;
            var second = BlackoutAmbience();
            second.fogDensity = 0.022f;
            cfg.zoneAmbienceSets = new[] { first, second };
            Assert.IsTrue(cfg.TryGetZoneAmbienceSet(ZoneBlackout, out var m));
            Assert.AreEqual(0.011f, m.fogDensity, 1e-6f,
                "primera coincidencia gana — misma regla hand-ordered que el resto de sets");
        }

        // ── Guardas contra el asset REAL ─────────────────────────────────────────
        //
        // La lección de "tener el mecanismo no es tenerlo cableado" está pagada dos
        // veces (zonePropSets estuvo implementado con 8 tests en verde y la zona salía
        // sin muebles porque ningún asset autoraba catálogo). Estos tests cargan el
        // asset REAL por Resources — si alguien borra la autoría de OFFICE del YAML,
        // esto es lo único que lo cuenta. NO se destruye el asset cargado: es el
        // compartido de Resources, no una instancia del test.

        [Test]
        public void Layer0ActuallyAuthorsTheOfficeLightSet()
        {
            var real = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            Assert.IsNotNull(real, "Layer0_Vestibulo no aparece por Resources");
            Assert.IsTrue(real.TryGetZoneLightSet(ZoneOffice, out var zl),
                "ADR-059 pide que layer 0 autore luz de OFFICE — el mecanismo sin " +
                "autoría es exactamente el agujero que ya pasó con zonePropSets");
            Assert.IsTrue(zl.overrideBrokenLampChance && zl.brokenLampChance < real.brokenLampChance,
                "fluorescente institucional: menos lámparas rotas que la capa");
            Assert.IsTrue(zl.overrideLampColor && zl.lampColor.b > zl.lampColor.r,
                "el color autorado debe ser FRÍO (b > r); la capa es cálida (r > b)");
            if (zl.overrideLampRange)
                Assert.LessOrEqual(zl.lampRange, 6f,
                    "restricción dura de BackroomsLighting: √(range²−16) < 5 ⇒ ≤ 6");
        }

        [Test]
        public void Layer0ActuallyAuthorsAmbienceForEveryZone()
        {
            var real = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            Assert.IsNotNull(real, "Layer0_Vestibulo no aparece por Resources");

            // ADR-066 autora las 13 zonas de golpe: aquí el mecanismo sin autoría no es un
            // "no pasa nada", es el ADR entero sin efecto.
            for (int z = 0; z <= ZoneOffice; z++)
            {
                Assert.IsTrue(real.TryGetZoneAmbienceSet(z, out var za),
                    $"zone_kind {z} sin ZoneAmbienceSet — las 13 se autoran en ADR-066");
                Assert.IsTrue(za.overrideAmbientLight && za.overrideFogDensity,
                    $"zone_kind {z}: ambiente y niebla deben estar ambos habilitados");

                // Suelo de jugabilidad: 0.8326/density es la distancia de atenuación al 50 %.
                // 0.075 ≈ 11 m; por debajo de eso navegar un pasillo de 5 m es tanteo.
                Assert.LessOrEqual(za.fogDensity, 0.075f,
                    $"zone_kind {z}: niebla por encima del suelo de jugabilidad");
                Assert.GreaterOrEqual(za.fogDensity, 0.015f,
                    $"zone_kind {z}: menos niebla que la base plana de Fase 5A no aporta nada");

                // El mundo es oscuro por diseño: un ambient alto anula las lámparas y con
                // ellas todo el trabajo de luz por zona de ADR-059.
                float luma = 0.2126f * za.ambientLight.r
                           + 0.7152f * za.ambientLight.g
                           + 0.0722f * za.ambientLight.b;
                Assert.Less(luma, 0.30f, $"zone_kind {z}: ambient demasiado brillante ({luma:F3})");
            }

            Assert.IsTrue(real.TryGetZoneAmbienceSet(ZoneBlackout, out var blackout));
            Assert.Less(blackout.fogDensity, 1f, "sanity");
            Assert.IsTrue(real.TryGetZoneAmbienceSet(ZoneSafe, out var safe));
            Assert.Less(safe.fogDensity, blackout.fogDensity,
                "SAFE tiene que ser el alivio y BLACKOUT el castigo — si se invierte, la " +
                "intención del ADR está del revés en el asset");
        }

        [Test]
        public void Layer0ActuallyAuthorsTheOfficeCeilingSet()
        {
            var real = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            Assert.IsNotNull(real, "Layer0_Vestibulo no aparece por Resources");
            Assert.IsTrue(real.TryGetZoneCeilingSet(ZoneOffice, out var zc),
                "ADR-059 pide que layer 0 autore techo de OFFICE");
            Assert.IsTrue(zc.overridePanelVariety,
                "el override de variedad debe estar habilitado…");
            Assert.AreEqual(0f, zc.ceilingPanelVariety,
                "…y a 0: rejilla uniforme, sin paneles hundidos/caídos en un despacho");
        }
    }
}
