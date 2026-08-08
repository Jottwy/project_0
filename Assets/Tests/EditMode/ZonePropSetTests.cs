using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ZONE_OFFICE, pieza de cliente — catálogo de props por <c>zone_kind</c>
    /// (<see cref="LayerVisualConfig.PropsFor"/>) y longitud de los dos arrays indexados por
    /// zona.
    ///
    /// Los arrays son lo que más importa aquí y es lo que menos se nota si falla: tanto
    /// <see cref="LayerVisualConfig.ZoneTint"/> como <c>ZoneLootTable.Profile</c> resuelven
    /// fuera de rango con <c>Mathf.Clamp</c>, así que un array corto NO lanza — sirve la
    /// ÚLTIMA entrada. Con 12 entradas, un chunk OFFICE se habría tintado y looteado como
    /// ZONE_PIT sin un solo error en consola.
    /// </summary>
    [TestFixture]
    public class ZonePropSetTests
    {
        /// <summary>Espejo de `ZONE_OFFICE` (backend/src/world/chunk/surface_profiles.rs).</summary>
        private const int ZoneOffice = 12;
        private const int ZonePit = 11;

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
            _cfg.props = new[]
            {
                new PropEntry { placeholderType = "desk", spawnWeight = 1f },
            };
            return _cfg;
        }

        private static PropEntry[] OfficeProps() => new[]
        {
            new PropEntry { placeholderType = "partition", spawnWeight = 2f },
            new PropEntry { placeholderType = "filecab", spawnWeight = 1f },
        };

        // ── Los dos arrays indexados por zona cubren hasta ZONE_OFFICE ────────────────

        [Test]
        public void DefaultZoneTintsCoversOfficeWithItsOwnHue()
        {
            var cfg = Config();
            Assert.AreEqual(ZoneOffice + 1, cfg.zoneTints.Length,
                "zoneTints debe tener una entrada por zone_kind hasta OFFICE incluido");
            Assert.AreNotEqual(cfg.ZoneTint(ZonePit), cfg.ZoneTint(ZoneOffice),
                "OFFICE está cayendo en el tinte de ZONE_PIT — es exactamente lo que hace el " +
                "Clamp cuando el array se queda corto, y no lanza ningún error");
        }

        [Test]
        public void DefaultZoneLootProfilesCoversOfficeWithItsOwnProfile()
        {
            var profiles = Net.ChunkLootRoll.DefaultZoneLootProfiles();
            Assert.AreEqual(ZoneOffice + 1, profiles.Length,
                "DefaultZoneLootProfiles debe tener una entrada por zone_kind hasta OFFICE");
            Assert.AreNotEqual(profiles[ZonePit].itemCacheChance, profiles[ZoneOffice].itemCacheChance,
                "el perfil de OFFICE es el de ZONE_PIT — array corto o entrada copiada");
        }

        // ── Los ASSETS YA SERIALIZADOS, no solo los defaults de C# ───────────────────
        //
        // Los dos tests de arriba prueban los inicializadores de campo. Eso NO basta: un
        // asset que ya existe en disco lleva su propio array horneado en YAML y el
        // inicializador NUNCA vuelve a aplicarse. Añadir ZONE_OFFICE dejó cortos a
        // Layer0_Vestibulo.asset y a Loot/ZoneLootTable.asset, y ninguno de los dos habría
        // lanzado un error: el Clamp sirve la última entrada (ZONE_PIT) y ya. Estos dos
        // tests son la guarda para el siguiente zone_kind que se añada.

        [Test]
        public void SerializedLayerVisualAssetsCoverEveryZoneKind()
        {
            var assets = Resources.LoadAll<LayerVisualConfig>("LayerVisuals");
            Assert.IsNotEmpty(assets, "no se cargó ningún LayerVisualConfig de Resources/LayerVisuals");
            foreach (var asset in assets)
            {
                // Un asset SIN el bloque `zoneTints` en YAML vive del inicializador de campo y
                // ya está cubierto por DefaultZoneTintsCoversOfficeWithItsOwnHue. Lo que este
                // test caza es el que SÍ lo tiene horneado y se quedó corto.
                if (asset.zoneTints == null || asset.zoneTints.Length == 0) continue;
                Assert.GreaterOrEqual(asset.zoneTints.Length, ZoneOffice + 1,
                    $"{asset.name}.zoneTints tiene {asset.zoneTints.Length} entradas: OFFICE " +
                    "caerá en el tinte de la última zona por el Clamp, sin ningún error");
            }
        }

        [Test]
        public void Layer0ActuallyAuthorsTheOfficePropCatalogue()
        {
            // La lección de la primera corrida en juego: tener el MECANISMO no es tenerlo
            // CABLEADO. `zonePropSets` + `PropsFor` estaban implementados y con 8 tests en
            // verde, y un chunk OFFICE seguía saliendo con los props genéricos de la capa
            // porque ningún asset autorizaba un catálogo — exactamente lo que
            // `PropsForFallsBackToTheLayerCatalogueWhenNothingIsAuthored` documenta y
            // aprueba. Este test es el que distingue "cae bien al de capa" de "nadie lo
            // autorizó todavía".
            var layer0 = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            Assert.IsNotNull(layer0, "no se cargó Resources/LayerVisuals/Layer0_Vestibulo");

            var office = layer0.PropsFor(ZoneOffice);
            Assert.AreNotSame(layer0.props, office,
                "layer 0 no autoriza catálogo de props para OFFICE: un chunk de oficina se " +
                "amueblaría con los props genéricos de la capa");

            var types = new System.Collections.Generic.List<string>();
            foreach (var p in office) types.Add(p.placeholderType);
            CollectionAssert.Contains(types, "partition", "el catálogo de OFFICE no lleva mamparas");
            CollectionAssert.Contains(types, "filecab", "el catálogo de OFFICE no lleva archivadores");

            float total = 0f;
            foreach (var p in office) total += Mathf.Max(0f, p.spawnWeight);
            Assert.Greater(total, 0f, "todos los pesos del catálogo de OFFICE son 0: no saldría nada");
        }

        [Test]
        public void TheSerializedZoneLootTableCoversEveryZoneKind()
        {
            var table = Resources.Load<ZoneLootTable>("Loot/ZoneLootTable");
            Assert.IsNotNull(table, "Resources/Loot/ZoneLootTable no está en disco — " +
                                    "créalo con el menú Backrooms antes de correr esto");
            Assert.GreaterOrEqual(table.profiles.Length, ZoneOffice + 1,
                $"ZoneLootTable.asset tiene {table.profiles.Length} perfiles: un chunk OFFICE " +
                "se lootearía con el perfil de la última zona (ZONE_PIT: caches ricos, " +
                "weapon-heavy) sin ningún error en consola");
        }

        // ── PropsFor: lista dispersa, primera coincidencia gana, caída al de capa ─────

        [Test]
        public void PropsForFallsBackToTheLayerCatalogueWhenNothingIsAuthored()
        {
            var cfg = Config();
            // El estado de HOY en los 4 assets: `zonePropSets` sin autorar. Este test es el que
            // garantiza que añadir el campo no cambió el render de nadie.
            Assert.AreSame(cfg.props, cfg.PropsFor(ZoneOffice));
            Assert.AreSame(cfg.props, cfg.PropsFor(0));
            Assert.AreSame(cfg.props, cfg.PropsFor(-1));
        }

        [Test]
        public void PropsForReturnsTheZoneCatalogueOnlyForItsOwnZone()
        {
            var cfg = Config();
            var office = OfficeProps();
            cfg.zonePropSets = new[]
            {
                new ZonePropSet { zoneKind = ZoneOffice, props = office },
            };

            Assert.AreSame(office, cfg.PropsFor(ZoneOffice));
            Assert.AreSame(cfg.props, cfg.PropsFor(ZonePit));
            Assert.AreSame(cfg.props, cfg.PropsFor(0));
        }

        [Test]
        public void PropsForNeverMatchesASpecificSetWithAnUnknownZone()
        {
            var cfg = Config();
            cfg.zonePropSets = new[]
            {
                new ZonePropSet { zoneKind = ZoneOffice, props = OfficeProps() },
            };
            // −1 = ZoneRegistry aún no ha respondido. Misma degradación que ya toma el tinte
            // y el modelo de pared: el catálogo de capa, nunca el de una zona adivinada.
            Assert.AreSame(cfg.props, cfg.PropsFor(-1));
        }

        [Test]
        public void PropsForHonoursTheAnyZoneWildcardIncludingAnUnknownZone()
        {
            var cfg = Config();
            var any = OfficeProps();
            cfg.zonePropSets = new[]
            {
                new ZonePropSet { anyZoneKind = true, props = any },
            };
            Assert.AreSame(any, cfg.PropsFor(ZoneOffice));
            Assert.AreSame(any, cfg.PropsFor(0));
            // "Cualquier zona" incluye una desconocida — mismo criterio que WallVariantSet.
            Assert.AreSame(any, cfg.PropsFor(-1));
        }

        [Test]
        public void PropsForTakesTheFirstMatchNotTheMostSpecific()
        {
            var cfg = Config();
            var wildcard = OfficeProps();
            var specific = new[] { new PropEntry { placeholderType = "bin", spawnWeight = 1f } };
            cfg.zonePropSets = new[]
            {
                new ZonePropSet { anyZoneKind = true, props = wildcard },
                new ZonePropSet { zoneKind = ZoneOffice, props = specific },
            };
            // Documentado como PRIMERA coincidencia, no la más específica: la lista es corta y
            // se ordena a mano, y una regla de puntuación sería imposible de predecir desde el
            // Inspector. Mismo contrato que WallVariantSet.
            Assert.AreSame(wildcard, cfg.PropsFor(ZoneOffice));
        }

        [Test]
        public void PropsForTreatsAnEmptyAuthoredSetAsUnfinishedNotAsNoProps()
        {
            var cfg = Config();
            cfg.zonePropSets = new[]
            {
                new ZonePropSet { zoneKind = ZoneOffice, props = new PropEntry[0] },
                new ZonePropSet { zoneKind = ZoneOffice, props = null },
            };
            // Un set vacío es un autor a medias, no la intención "esta zona va pelada".
            Assert.AreSame(cfg.props, cfg.PropsFor(ZoneOffice));
        }

        // ── Los props de oficina son sólidos de verdad ────────────────────────────────

        [Test]
        public void OfficePlaceholdersKeepAPhysicalCollider()
        {
            foreach (string type in new[] { "partition", "filecab" })
            {
                var go = PlaceholderFactory.Create(type, null, 0.5f);
                try
                {
                    Assert.IsNotNull(go.GetComponentInChildren<Collider>(),
                        $"el placeholder '{type}' salió sin collider — sería mobiliario " +
                        "atravesable (regla del fix b0c5494)");
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        [Test]
        public void ThePartitionIsTallEnoughToNotBeJumpedOver()
        {
            var go = PlaceholderFactory.Create("partition", null, 0f);
            try
            {
                float top = 0f;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    top = Mathf.Max(top, r.bounds.max.y);
                // Mismo razonamiento y mismo umbral que LayerVisualConfig.MinKneeWallHeight:
                // lleva collider, así que su altura es una cantidad de jugabilidad. Por debajo
                // de ~1.15 m el jugador la salta (FPS_Player.prefab _jumpHeight 1.05,
                // m_StepOffset 0.275) y atraviesa un cubículo que se lee como cerrado.
                Assert.GreaterOrEqual(top, LayerVisualConfig.MinKneeWallHeight);
                // Y por debajo de la altura de ojos (~1.65 m): se ve POR ENCIMA de la planta,
                // que es lo que distingue un cubículo de una pared.
                Assert.Less(top, 1.65f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
