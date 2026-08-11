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

        /// <summary>Luminancia Rec.709 — el brillo percibido, no la media de canales.</summary>
        private static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

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

            // Enmienda ADR-066: el brillo del difusor va por lampEmission, NO por
            // lampIntensity. Estuvieron acoplados y el panel salía a 2.8–3.4 × blanco puro,
            // sin forma bajo ACES y desbordando el bloom por todo el techo. Techo en 1.6:
            // por encima vuelve a reventar con el umbral de bloom actual (1.05).
            Assert.LessOrEqual(real.lampEmission, 1.6f,
                $"lampEmission de capa {real.lampEmission} revienta el panel — es brillo de " +
                "superficie, no potencia de luz");
            if (zl.overrideLampEmission)
                Assert.LessOrEqual(zl.lampEmission, 1.6f,
                    $"lampEmission de OFFICE {zl.lampEmission} revienta el panel");
        }

        /// <summary>
        /// Enmienda a ADR-059 (2026-08-11, canon Level 0): ZONE_NORMAL es la ÚNICA zona
        /// autorizada a pasar de 6 de <c>lampRange</c>. El cap existe porque estas lámparas
        /// nacen con <c>LightShadows.None</c> (ADR-065 lo prohíbe cambiar) y el alcance a
        /// suelo √(range²−h²) por encima de 5 m ilumina a través de la propia pared. NORMAL
        /// acepta ese sangrado a cambio de cobertura continua; nadie más puede.
        ///
        /// Este test es el único sitio donde esa excepción es comprobable: si alguien copia
        /// el 9 de NORMAL a otra zona o lo sube al valor de CAPA (que gobierna las 12 zonas
        /// que no autoran rango), salta aquí.
        /// </summary>
        [Test]
        public void Layer0AuthorsNormalLightSetAndCapsLampRangeEverywhereElse()
        {
            var real = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            Assert.IsNotNull(real, "Layer0_Vestibulo no aparece por Resources");

            Assert.IsTrue(real.TryGetZoneLightSet(ZoneNormal, out var zn),
                "ZONE_NORMAL debe autorar su propio ZoneLightSet: sin él, la zona base cae a " +
                "los valores de capa y la reautoría del canon no llega a ninguna parte");
            Assert.IsTrue(zn.overrideLampRange && zn.lampRange > 6f,
                $"NORMAL necesita rango por encima del cap para que las lámparas se solapen " +
                $"(autorado {zn.lampRange})");
            Assert.IsTrue(zn.overrideLightDensity && zn.lightDensity >= 0.95f,
                "el rango grande sin densidad alta vuelve a dejar islas: van juntos");
            Assert.IsTrue(zn.overrideLampEmission && zn.lampEmission <= 1.6f,
                $"lampEmission de NORMAL {zn.lampEmission} revienta el panel");

            Assert.LessOrEqual(real.lampRange, 6f,
                $"el lampRange de CAPA ({real.lampRange}) gobierna las zonas que no lo " +
                "sobrescriben — subirlo aquí levantaría el cap de las 12 de golpe");

            foreach (var set in real.zoneLightSets)
            {
                if (set.zoneKind == ZoneNormal && !set.anyZoneKind) continue;
                if (!set.overrideLampRange) continue;
                Assert.LessOrEqual(set.lampRange, 6f,
                    $"zone_kind {set.zoneKind}: la excepción de la enmienda es de NORMAL y de " +
                    "nadie más (√(range²−11.9) < 5)");
            }
        }

        /// <summary>
        /// Segunda enmienda a ADR-059 (2026-08-12, canon Level 0): el TIPO de luz pasa a ser
        /// autorable por zona, y Point es de ZONE_NORMAL y de nadie más.
        ///
        /// Por qué hay test y no basta el asset: las intensidades de las otras zonas están
        /// calibradas para un Spot que NO ilumina el techo (OFFICE 3.2, SAFE 3.0, CLEANING
        /// 3.4). Marcar Point en cualquiera de ellas les entrega esa intensidad sobre la losa
        /// a 1 m — techo blanco y bloom desbordado — sin que nada falle ni avise. Es un fallo
        /// puramente visual, del tipo que solo se ve entrando a esa zona concreta en juego.
        /// </summary>
        [Test]
        public void OnlyNormalAuthorsAPointLampAndItsIntensityFitsTheCeiling()
        {
            var real = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            Assert.IsNotNull(real, "Layer0_Vestibulo no aparece por Resources");

            Assert.IsTrue(real.TryGetZoneLightSet(ZoneNormal, out var zn),
                "ZONE_NORMAL debe autorar su ZoneLightSet");
            Assert.IsTrue(zn.overrideLampPoint && zn.lampPoint,
                "sin Point, el Spot no puede iluminar el plano del que cuelga y el techo " +
                "vuelve a ser negro — que es justo lo que esta enmienda deja atrás");

            // La malla del difusor va a ceilingHeight − 0.3 y PointDropBelowPanel baja la Light
            // otros 0.70, así que hay 1.0 m EXACTO de la Light a la losa de techo. Con 1/d² = 1
            // la irradiancia sobre el techo es la intensidad tal cual, y sobre un albedo de 0.79
            // (M_Backrooms_Ceiling medido) el BloomThreshold de 1.15 se cruza en intensidad
            // 1.15/0.79 ≈ 1.45. Se deja margen: por encima de 1.0 el panel deja de tener forma.
            Assert.IsTrue(zn.overrideLampIntensity,
                "con Point el techo depende de la intensidad, así que NORMAL debe fijarla y no " +
                "heredar la de capa, calibrada para Spot");
            Assert.LessOrEqual(zn.lampIntensity, 1.0f,
                $"intensidad {zn.lampIntensity} con Point a 1 m del techo lo quema y desborda " +
                "el bloom — es el defecto que hizo abandonar Point en su día");
            Assert.Greater(zn.lampIntensity, 0.3f,
                $"intensidad {zn.lampIntensity}: por debajo de ~0.3 la lámpara no se distingue " +
                "del ambiente y el techo queda plano, sin panel");

            foreach (var set in real.zoneLightSets)
            {
                if (set.zoneKind == ZoneNormal && !set.anyZoneKind) continue;
                Assert.IsFalse(set.overrideLampPoint && set.lampPoint,
                    $"zone_kind {set.zoneKind} autora Point con una intensidad calibrada para " +
                    "Spot: el techo de esa zona saldría blanco. Point exige rebajar la " +
                    "intensidad en la misma fila, nunca por separado");
            }
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

                // Enmienda ADR-066 (2026-08-11): sin luz debe haber oscuridad. Un ambient
                // alto ilumina toda superficie mire donde mire y anula las lámparas, y con
                // ellas el trabajo de luz por zona de ADR-059. El techo era 0.30 y no
                // protegía nada — la autoría real vive en torno a 0.01–0.05.
                //
                // SEGUNDA ENMIENDA (2026-08-12, canon Level 0): ZONE_NORMAL sale de esa regla
                // y solo ella. La oscuridad es el recurso de las zonas HOSTILES; en la zona
                // base compite con el canon, que es luz plana, uniforme y casi sobreexpuesta
                // sin un solo píxel negro. Los dos objetivos son incompatibles en la misma
                // zona y la resolución del proyecto ya era por zona. Las otras doce mantienen
                // el techo intacto, así que el presupuesto de oscuridad no se toca donde
                // significa algo.
                float ambLuma = Luma(za.ambientLight);
                if (z == ZoneNormal)
                {
                    Assert.Greater(ambLuma, 0.15f,
                        $"ZONE_NORMAL con ambient {ambLuma:F3}: por debajo de ~0.15 el mundo " +
                        "vuelve a leerse negro fuera del cono de las lámparas y la paleta base " +
                        "de superficie deja de poder evaluarse");
                    Assert.Less(ambLuma, 0.45f,
                        $"ZONE_NORMAL con ambient {ambLuma:F3}: por encima de ~0.45 el ambiente " +
                        "aplana tanto que las lámparas dejan de aportar y el sitio deja de " +
                        "tener forma");
                }
                else
                {
                    Assert.Less(ambLuma, 0.08f,
                        $"zone_kind {z}: ambient demasiado brillante ({ambLuma:F3}) — sin luz hay que ver poco");
                }

                // La niebla de Unity es un lerp por distancia hacia fogColor SIN mirar la
                // iluminación: una niebla clara hace BRILLAR el fondo de un pasillo a
                // oscuras, que es exactamente el defecto que esta enmienda corrige. Atada al
                // ambient para que no pueda volver a despegarse de él.
                float fogLuma = Luma(za.fogColor);
                Assert.LessOrEqual(fogLuma, ambLuma * 2.5f + 0.01f,
                    $"zone_kind {z}: niebla ({fogLuma:F3}) demasiado clara para su ambient " +
                    $"({ambLuma:F3}) — el fondo sin luz brillaría");
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
