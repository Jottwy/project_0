using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BackroomsSurvival.Net;
using NUnit.Framework;
using PolymindGames.InventorySystem;
using PolymindGames.ResourceHarvesting;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-114 — los siete assets del desmontaje, comprobados como los usa el juego: por la ruta
    /// que carga el sembrador, por el nombre que resuelve el catálogo y por los dos números de la
    /// puerta de herramienta. Piden el editor (AssetDatabase/Resources): no corren en el arnés
    /// headless. Las rutas son el CONTRATO con `BackroomsDismantleAssetsCreator` y
    /// `StpWorldPropSpawner`, por eso están escritas aquí y no importadas.
    /// </summary>
    public class DismantleAssetsTests
    {
        private const string ItemFolder = "Assets/Resources/Definitions/Item";
        private const string PropFolder = "Assets/Resources/Props/Dismantle";
        private const string ScrewdriverDefinitionPath = ItemFolder + "/BR_Screwdriver.asset";
        private const string ScrewdriverWieldablePath = "Assets/Prefabs/Wieldables/BR_Wieldable_Screwdriver.prefab";
        private const string HuntingAxePath = "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_HuntingAxe.prefab";
        private const string SteelPickaxePath = "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_SteelPickaxe.prefab";

        private static readonly string[] Props = { "Desk", "Shelf", "Chair" };

        private static string DefinitionPath(string prop) => $"{PropFolder}/Definitions/BR_{prop}.asset";

        private static HarvestableResourceDefinition Definition(string prop)
        {
            var def = AssetDatabase.LoadAssetAtPath<HarvestableResourceDefinition>(DefinitionPath(prop));
            Assert.IsNotNull(def, $"falta la definición '{DefinitionPath(prop)}'");
            return def;
        }

        /// <summary>Los perfiles de cosecha de un wieldable, leídos como los guarda el vendor.</summary>
        private static List<(HarvestableResourceType type, float power)> HarvestProfiles(string prefabPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(prefab, $"falta el prefab '{prefabPath}'");
            var result = new List<(HarvestableResourceType, float)>();
            foreach (var attack in prefab.GetComponentsInChildren<MeleeHarvestAttack>(true))
            {
                var so = new SerializedObject(attack);
                var profiles = so.FindProperty("_resourceHarvestProfiles");
                if (profiles == null) continue;
                for (int i = 0; i < profiles.arraySize; i++)
                {
                    var p = profiles.GetArrayElementAtIndex(i);
                    result.Add(((HarvestableResourceType)p.FindPropertyRelative("ResourceType").intValue,
                        p.FindPropertyRelative("HarvestPower").floatValue));
                }
            }
            return result;
        }

        [Test]
        public void LasTresDefinicionesExistenYSonPlant()
        {
            foreach (string prop in Props)
            {
                var def = Definition(prop);
                Assert.AreEqual(HarvestableResourceType.Plant, def.ResourceType,
                    $"{prop}: ADR-114 D6 — el único tipo libre del enum cerrado del vendor");
                Assert.Greater(def.RequiredPower, 0f, $"{prop}: sin potencia exigida cualquier golpe desmonta");
                Assert.AreEqual(0, def.RespawnDays,
                    $"{prop}: el reloj de regeneración es del backend (D5); el vendor no debe resucitarlo por día");
                Assert.IsNotNull(def.Prefab, $"{prop}: la definición no apunta a su prefab");
            }
        }

        [Test]
        public void CadaMuebleRequiereElDestornillador()
        {
            var screwdriver = HarvestProfiles(ScrewdriverWieldablePath);
            var plant = screwdriver.Where(p => p.type == HarvestableResourceType.Plant).ToList();
            Assert.AreEqual(1, plant.Count, "el destornillador lleva exactamente un perfil Plant");

            foreach (string prop in Props)
            {
                float required = Definition(prop).RequiredPower;
                Assert.GreaterOrEqual(plant[0].power, required,
                    $"{prop}: el destornillador ({plant[0].power}) no llega a la potencia exigida ({required})");
            }

            // Y las otras dos herramientas de golpear NO abren muebles: medido antes de aprobar el ADR.
            foreach (string other in new[] { HuntingAxePath, SteelPickaxePath })
            {
                Assert.IsFalse(HarvestProfiles(other).Any(p => p.type == HarvestableResourceType.Plant),
                    $"'{other}' lleva perfil Plant: podría desmontar muebles sin destornillador");
            }
        }

        [Test]
        public void ElDestornilladorEsUnItemEmpunableConSuWieldable()
        {
            var def = AssetDatabase.LoadAssetAtPath<ItemDefinition>(ScrewdriverDefinitionPath);
            Assert.IsNotNull(def, $"falta '{ScrewdriverDefinitionPath}'");
            Assert.AreEqual("Screwdriver", def.Name);
            Assert.IsNotNull(def.Pickup, "sin pickup el destornillador resuelve por nombre y luego no aparece");
            Assert.AreSame(def, ItemDefinition.GetWithName("Screwdriver"));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScrewdriverWieldablePath);
            Assert.IsNotNull(prefab);
            Assert.IsNotNull(prefab.GetComponent<WieldableItem>(), "WieldableItem en la raíz, o el jugador no lo registra");

            int repointed = 0;
            foreach (var c in prefab.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var p = new SerializedObject(c).FindProperty("_referencedItem");
                if (p == null) continue;
                var v = p.FindPropertyRelative("_value") ?? p;
                Assert.AreEqual(def.Id, v.intValue, $"'{c.GetType().Name}' sigue apuntando al item del hacha");
                repointed++;
            }
            Assert.Greater(repointed, 0, "ningún componente referencia el item: equiparlo sacaría el hacha");
        }

        [Test]
        public void LosMaterialesResuelvenPorNombreSinDuplicados()
        {
            string[] names = { ChunkDismantleRoll.WoodenPlank, ChunkDismantleRoll.MetalBeam,
                               ChunkDismantleRoll.Cloth, ChunkDismantleRoll.Leather };
            var ids = new HashSet<int>();
            foreach (string name in names)
            {
                var def = ItemDefinition.GetWithName(name);
                Assert.IsNotNull(def, $"'{name}' no está en la base de definiciones: el sembrador lo saltaría");
                Assert.IsNotNull(def.Pickup, $"'{name}' sin pickup no aparece en el suelo al desmontar");
                Assert.IsTrue(ids.Add(def.Id), $"'{name}' comparte id con otro material");

                int sameName = ItemDefinition.Definitions.Count(d => d.Name == name);
                Assert.AreEqual(1, sameName, $"'{name}' está definido {sameName} veces: GetWithName cogería uno al azar");
            }

            foreach (string own in new[] { "BR_Wooden Plank", "BR_Metal Beam", "BR_Screwdriver" })
            {
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ItemDefinition>($"{ItemFolder}/{own}.asset"),
                    $"falta '{ItemFolder}/{own}.asset'");
            }
        }

        [Test]
        public void LasCantidadesCoincidenConAdr114()
        {
            // D8, literal — y cada nombre resuelto a un id, que es lo que el sembrador entrega al
            // sync manager como `ItemDrop`.
            AssertYield(DismantleProp.Desk, (ChunkDismantleRoll.WoodenPlank, 2), (ChunkDismantleRoll.MetalBeam, 1));
            AssertYield(DismantleProp.Shelf, (ChunkDismantleRoll.MetalBeam, 2), (ChunkDismantleRoll.WoodenPlank, 1));
            AssertYield(DismantleProp.Chair, (ChunkDismantleRoll.WoodenPlank, 1), (ChunkDismantleRoll.Cloth, 1),
                (ChunkDismantleRoll.Leather, 1));
        }

        private static void AssertYield(DismantleProp prop, params (string name, int count)[] expected)
        {
            var actual = ChunkDismantleRoll.MaterialsFor(prop);
            Assert.AreEqual(expected.Length, actual.Count, $"{prop}: número de materiales");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].name, actual[i].Name, $"{prop}[{i}]");
                Assert.AreEqual(expected[i].count, actual[i].Count, $"{prop}[{i}] {expected[i].name}");
                Assert.IsNotNull(ItemDefinition.GetWithName(actual[i].Name), $"{prop}: '{actual[i].Name}' no resuelve");
            }
            // Cinta adhesiva NUNCA sale de desmontar (D8), y no hay materiales fuera de los cinco.
            Assert.IsFalse(actual.Any(m => m.Name == "Duct Tape"), $"{prop}: la cinta es sólo de loot");
        }

        [Test]
        public void LosPrefabsEstanConectadosYSeCarganComoLosPideElSembrador()
        {
            foreach (string prop in Props)
            {
                var go = Resources.Load<GameObject>("Props/Dismantle/" + prop);
                Assert.IsNotNull(go, $"Resources.Load('Props/Dismantle/{prop}') es nulo: el sembrador avisa y no siembra");
                Assert.AreSame(go, AssetDatabase.LoadAssetAtPath<GameObject>($"{PropFolder}/{prop}.prefab"));

                var hr = go.GetComponent<HarvestableResource>();
                Assert.IsNotNull(hr, $"{prop}: HarvestableResource tiene que estar en la RAÍZ");
                var col = go.GetComponent<Collider>();
                Assert.IsNotNull(col, $"{prop}: collider en la raíz, o STP no encuentra el harvestable al golpear");
                Assert.IsTrue(col.enabled, $"{prop}: el collider de la raíz está apagado");

                var def = Definition(prop);
                Assert.AreSame(def, hr.ResourceDefinition, $"{prop}: el prefab apunta a otra definición");
                Assert.AreSame(hr, def.Prefab, $"{prop}: la definición apunta a otro prefab");

                var so = new SerializedObject(hr);
                var visual = so.FindProperty("_unharvestedObject").objectReferenceValue as GameObject;
                Assert.IsNotNull(visual, $"{prop}: sin objeto visual el mueble es invisible");
                Assert.AreSame(visual, so.FindProperty("_partiallyHarvestedObject").objectReferenceValue,
                    $"{prop}: el estado parcial debe mostrar el mismo visual, o el primer golpe lo esconde");
                Assert.IsTrue(so.FindProperty("_disableHitboxOnKilled").boolValue, $"{prop}: al agotarse deja de estorbar");

                foreach (var child in go.GetComponentsInChildren<Collider>(true))
                {
                    if (child.gameObject == go) continue;
                    Assert.IsFalse(child.enabled,
                        $"{prop}: collider hijo '{child.name}' encendido: un golpe ahí no encuentra el harvestable");
                }
            }
        }

        [Test]
        public void NoHayAssetsDuplicados()
        {
            foreach (string prop in Props)
            {
                Assert.AreEqual(1, AssetDatabase.FindAssets($"BR_{prop} t:HarvestableResourceDefinition").Length,
                    $"más de una definición de {prop}");
                Assert.AreEqual(1, AssetDatabase.FindAssets($"{prop} t:Prefab", new[] { PropFolder }).Length,
                    $"más de un prefab '{prop}' bajo {PropFolder}");
            }
            Assert.AreEqual(1, AssetDatabase.FindAssets("BR_Screwdriver t:ItemDefinition").Length);
            Assert.AreEqual(1, AssetDatabase.FindAssets("BR_Wieldable_Screwdriver t:Prefab").Length);
        }

        /// <summary>El drop es un objeto del MUNDO por el camino que ya existía (`stp_drop` → roster
        /// `stp_items` → pickup), no una escritura en el inventario. Sin backend en EditMode, lo que
        /// se puede afirmar es que la cadena sigue montada con esas piezas.</summary>
        [Test]
        public void LosDropsVanPorElCaminoExistente()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Assert.IsNotNull(typeof(StpHarvestableSyncManager).GetMethod("SpawnItemDropsOnDeplete", any),
                "el flanco a cero ya no reparte los materiales del mueble");
            Assert.IsNotNull(typeof(IPCClient).GetMethod("SendStpDrop", BindingFlags.Instance | BindingFlags.Public),
                "los materiales salen por `stp_drop`, la acción que el backend valida y materializa en `stp_items`");
            Assert.IsNotNull(typeof(NetworkHarvestableInstance).GetField("itemDrops", any),
                "el mueble lleva su lista de (defId, count), no el par tronco/cantidad de los árboles");
        }
    }
}
