using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-059 — overrides de luz y techo por <c>zone_kind</c>
    /// (<see cref="LayerVisualConfig.TryGetZoneLightSet"/> /
    /// <see cref="LayerVisualConfig.TryGetZoneCeilingSet"/>).
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
    }
}
