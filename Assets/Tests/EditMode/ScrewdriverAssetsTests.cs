using System.Linq;
using NUnit.Framework;
using PolymindGames.InventorySystem;
using PolymindGames.ResourceHarvesting;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El destornillador REAL de punta a punta: definición, icono propio, wieldable con el modelo
    /// de Meshy en la mano, pickup propio en el suelo, y ni una malla del hacha activa en ninguno de
    /// los dos. Pide el editor (AssetDatabase). Las rutas son el contrato con
    /// `BackroomsScrewdriverModelApplier` y `BackroomsScrewdriverPickupCreator`.
    /// </summary>
    public class ScrewdriverAssetsTests
    {
        private const int ExpectedId = 808575401;
        private const string DefinitionPath = "Assets/Resources/Definitions/Item/BR_Screwdriver.asset";
        private const string WieldablePath = "Assets/Prefabs/Wieldables/BR_Wieldable_Screwdriver.prefab";
        private const string PickupPath = "Assets/Prefabs/Items/BR_Pickup_Screwdriver.prefab";
        private const string IconPath = "Assets/Art/Items/BR_Screwdriver_Icon.png";
        private const string MeshPath = "Assets/Art/Items/Screwdriver/BR_Screwdriver_Mesh.asset";
        private const string MaterialPath = "Assets/Art/Items/Screwdriver/BR_Screwdriver_Mat.mat";
        /// <summary>El de la mano (ADR-077 enm. 2); <see cref="MaterialPath"/> es el del mundo.</summary>
        private const string FirstPersonMaterialPath = "Assets/Art/Items/Screwdriver/BR_Screwdriver_FP_Mat.mat";
        private const string AxeDefinitionPath = "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Hunting Axe.asset";
        private const string AxePickupPath = "Assets/PolymindGames/STP/Prefabs/Items/STP_Pickup_HuntingAxe.prefab";
        private const string PlayerPrefabPath = "Assets/PolymindGames/FPSCore/Prefabs/Core/FPS_Player.prefab";

        private static ItemDefinition Definition()
        {
            var def = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            Assert.IsNotNull(def, $"falta '{DefinitionPath}'");
            return def;
        }

        private static bool IsAxeMesh(Mesh m) => m != null && m.name.IndexOf("Axe", System.StringComparison.OrdinalIgnoreCase) >= 0;

        [Test]
        public void LaDefinicionConservaSuIdYResuelvePorNombre()
        {
            var def = Definition();
            Assert.AreEqual(ExpectedId, def.Id, "el id está en el wire y en los saves: no se regenera");
            Assert.AreEqual("Screwdriver", def.Name);
            Assert.AreSame(def, ItemDefinition.GetWithName("Screwdriver"));
            Assert.AreEqual(1, ItemDefinition.Definitions.Count(d => d.Name == "Screwdriver"), "definición duplicada");
            Assert.AreEqual(1, def.StackSize);
            Assert.IsNotNull(def.ParentGroup, "sin categoría el inventario no lo enumera como herramienta");
        }

        [Test]
        public void ElIconoEsPropioYSaleDelModelo()
        {
            var def = Definition();
            Assert.IsNotNull(def.Icon, "sin icono");
            Assert.AreEqual(IconPath, AssetDatabase.GetAssetPath(def.Icon), "el icono no es el propio");
            var axe = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AxeDefinitionPath);
            Assert.IsNotNull(axe);
            Assert.AreNotSame(axe.Icon, def.Icon, "el icono sigue siendo el del hacha");

            var importer = AssetImporter.GetAtPath(IconPath) as TextureImporter;
            Assert.IsNotNull(importer);
            Assert.AreEqual(TextureImporterType.Sprite, importer.textureType);
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            Assert.AreEqual(SpriteMeshType.FullRect, settings.spriteMeshType,
                "sin FullRect Unity recorta el margen transparente y el icono sale estirado");
            Assert.IsTrue(importer.alphaIsTransparency);
        }

        [Test]
        public void ElArteHorneadoEstaVersionadoYNoApuntaAlImport()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            Assert.IsNotNull(mesh, "falta la malla horneada");
            float widest = Mathf.Max(mesh.bounds.size.x, Mathf.Max(mesh.bounds.size.y, mesh.bounds.size.z));
            Assert.That(widest, Is.InRange(0.10f, 0.50f), "la malla canónica no mide lo que un destornillador");
            Assert.AreEqual(mesh.bounds.size.y, widest, 1e-4f, "el eje largo tiene que ser +Y (de pie)");
            Assert.Less(mesh.bounds.center.magnitude, 0.01f, "la malla no está centrada en su caja");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            Assert.IsNotNull(mat, "falta el material horneado");
            Assert.AreEqual("Universal Render Pipeline/Lit", mat.shader.name, "Built-in = magenta en URP");
            var baseMap = mat.GetTexture("_BaseMap");
            Assert.IsNotNull(baseMap, "material sin albedo");
            StringAssert.StartsWith("Assets/Art/Items/Screwdriver/", AssetDatabase.GetAssetPath(baseMap),
                "la textura apunta al import gitignored: invisible en otra máquina");
        }

        [Test]
        public void ElWieldableLlevaElModeloRealEnLaManoYElHachaApagada()
        {
            var def = Definition();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WieldablePath);
            Assert.IsNotNull(prefab, $"falta '{WieldablePath}'");
            Assert.IsNotNull(prefab.GetComponent<WieldableItem>(), "WieldableItem en la raíz");

            var all = prefab.GetComponentsInChildren<Transform>(true);
            var hand = all.FirstOrDefault(t => t.name == "Hand.R");
            Assert.IsNotNull(hand, "sin hueso Hand.R no hay de dónde colgar");
            // NO necesariamente hijo directo de Hand.R: el agarre se lee del hacha donante
            // (ADR-077 enm. 3, BackroomsDonorGrip) y cuelga de SU hueso dominante (p. ej.
            // 'AxeBase'), que es el que la animación de equipar mueve de verdad.
            var node = all.FirstOrDefault(t => t.name == "BR_ScrewdriverModel");
            Assert.IsNotNull(node, "el nodo del modelo real no está en el prefab");
            Assert.IsTrue(node.gameObject.activeSelf, "el nodo está apagado");
            for (var ancestor = node.parent; ancestor != null; ancestor = ancestor.parent)
                Assert.IsTrue(ancestor.gameObject.activeSelf,
                    $"'{ancestor.name}' está apagado: el nodo nace invisible aunque él mismo esté activo");
            var filter = node.GetComponent<MeshFilter>();
            Assert.IsNotNull(filter);
            Assert.AreEqual("BR_Screwdriver_Mesh", filter.sharedMesh != null ? filter.sharedMesh.name : null);
            Assert.AreEqual(FirstPersonMaterialPath, AssetDatabase.GetAssetPath(node.GetComponent<MeshRenderer>().sharedMaterial),
                "en la mano va el material de primera persona (warp del viewmodel), no el del mundo");

            var axeNode = all.FirstOrDefault(t => t.name == "Axe");
            Assert.IsNotNull(axeNode, "el nodo del hacha se apaga, no se borra (reversible)");
            Assert.IsFalse(axeNode.gameObject.activeSelf, "el hacha sigue encendida en la mano");
            Assert.AreEqual(axeNode.gameObject.layer, node.gameObject.layer, "capa del viewmodel: si no, la cámara de primera persona no lo dibuja");

            foreach (var r in prefab.GetComponentsInChildren<Renderer>(false))
            {
                Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                Assert.IsFalse(IsAxeMesh(m), $"renderer activo '{r.name}' dibuja una malla de hacha ('{m?.name}')");
            }

            int repointed = 0;
            foreach (var c in prefab.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var p = new SerializedObject(c).FindProperty("_referencedItem");
                if (p == null) continue;
                var v = p.FindPropertyRelative("_value") ?? p;
                Assert.AreEqual(def.Id, v.intValue, $"'{c.GetType().Name}' apunta a otro item");
                repointed++;
            }
            Assert.Greater(repointed, 0);
        }

        [Test]
        public void LaPuertaDeHerramientaNoCambia()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WieldablePath);
            var attacks = prefab.GetComponentsInChildren<MeleeHarvestAttack>(true);
            Assert.AreEqual(1, attacks.Length, "un MeleeHarvestAttack, como el hacha");
            var profiles = new SerializedObject(attacks[0]).FindProperty("_resourceHarvestProfiles");
            Assert.AreEqual(1, profiles.arraySize);
            var p = profiles.GetArrayElementAtIndex(0);
            Assert.AreEqual((int)HarvestableResourceType.Plant, p.FindPropertyRelative("ResourceType").intValue);
            Assert.AreEqual(0.5f, p.FindPropertyRelative("HarvestPower").floatValue, 1e-5f);
            Assert.AreEqual(0.25f, p.FindPropertyRelative("YieldPerHit").floatValue, 1e-5f);
        }

        [Test]
        public void ElPickupEsPropioYLlevaElModeloReal()
        {
            var def = Definition();
            Assert.IsNotNull(def.Pickup, "sin _pickup el item resuelve y luego no aparece");
            Assert.AreEqual(PickupPath, AssetDatabase.GetAssetPath(def.Pickup.gameObject),
                "el _pickup sigue siendo el del vendor: en el suelo y en la mano de un remoto sería un hacha");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PickupPath);
            Assert.IsNotNull(prefab);
            Assert.AreEqual(Vector3.one, prefab.transform.localScale, "root a escala 1 (resaltado del vendor en espacio de objeto)");
            Assert.IsNull(prefab.GetComponent<LODGroup>(), "LODGroup del hacha sigue ahí");
            Assert.AreEqual(0, prefab.GetComponentsInChildren<Renderer>(true).Count(r => r.gameObject != prefab),
                "quedan hijos con malla (LODs del hacha)");

            var filter = prefab.GetComponent<MeshFilter>();
            Assert.IsNotNull(filter);
            Assert.AreEqual("BR_Screwdriver_Mesh", filter.sharedMesh != null ? filter.sharedMesh.name : null);
            Assert.IsFalse(IsAxeMesh(filter.sharedMesh));
            Assert.AreEqual(MaterialPath, AssetDatabase.GetAssetPath(prefab.GetComponent<MeshRenderer>().sharedMaterial));

            var pickup = prefab.GetComponent<ItemPickup>();
            Assert.IsNotNull(pickup, "ItemPickup (de wieldable) en la raíz");
            var item = new SerializedObject(pickup).FindProperty("_item").FindPropertyRelative("_value");
            Assert.AreEqual(def.Id, item.intValue, "recoger el destornillador metería otra cosa en la mochila");

            Assert.IsNotNull(prefab.GetComponent<Rigidbody>(), "sin cuerpo no cae al soltarlo");
            var box = prefab.GetComponent<BoxCollider>();
            Assert.IsNotNull(box, "sin collider no se puede mirar ni recoger");
            Assert.That(box.size.y, Is.InRange(0.10f, 0.50f), "el collider no es el del destornillador");

            var axePickup = AssetDatabase.LoadAssetAtPath<GameObject>(AxePickupPath);
            Assert.AreEqual(axePickup.layer, prefab.layer, "misma capa que el pickup donante (interactuable)");
        }

        [Test]
        public void ElJugadorTieneElWieldableDadoDeAlta()
        {
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            Assert.IsNotNull(player);
            var node = player.GetComponentsInChildren<WieldableItem>(true)
                .FirstOrDefault(w => w.name.StartsWith("BR_Wieldable_Screwdriver"));
            Assert.IsNotNull(node, "sin el alta, equipar el destornillador no saca nada");
        }

        [Test]
        public void NoHayDuplicadosNiReferenciasSueltasAlHacha()
        {
            Assert.AreEqual(1, AssetDatabase.FindAssets("BR_Screwdriver t:ItemDefinition").Length);
            Assert.AreEqual(1, AssetDatabase.FindAssets("BR_Wieldable_Screwdriver t:Prefab").Length);
            Assert.AreEqual(1, AssetDatabase.FindAssets("BR_Pickup_Screwdriver t:Prefab").Length);
            Assert.AreEqual(1, AssetDatabase.FindAssets("BR_Screwdriver_Icon t:Sprite").Length);

            // Nadie fuera del vendor referencia el pickup del hacha: el destornillador ya no lo necesita.
            var axePickup = AssetDatabase.LoadAssetAtPath<GameObject>(AxePickupPath);
            var def = Definition();
            Assert.AreNotEqual(axePickup, def.Pickup != null ? def.Pickup.gameObject : null);
            var stackPickup = new SerializedObject(def).FindProperty("_stackPickup").objectReferenceValue;
            Assert.IsNotNull(stackPickup, "el saco genérico de montón se conserva");
        }
    }
}
