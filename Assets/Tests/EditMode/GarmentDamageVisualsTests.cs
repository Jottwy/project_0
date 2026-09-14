using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Wearables;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R4b: la rotura de la ropa se ve en el muñeco, también en la ropa de serie del vendor.</summary>
    public class GarmentDamageVisualsTests
    {
        private const string VariantPath = "Assets/Prefabs/UI/BR_UI_Player.prefab";
        private const string ShaderName = "Backrooms/Garment Lit";
        private const string VendorItems = "Assets/PolymindGames/STP/Data/Resources/Definitions/Item";

        [Test]
        public void ElEstadoViajaPorZonaDelCuerpo()
        {
            var data = new GarmentZonesData(new GarmentZone(BodyZone.Chest, 0.1f), new GarmentZone(BodyZone.FootR, 0.1f));
            var state = new GarmentState();
            state.ApplyHit(0, data.Zones[0], DamageType.Slash, 30f, out _);
            state.ApplyHit(1, data.Zones[1], DamageType.Ballistic, 5f, out _);
            state.Repair(1, GarmentRepair.Tape);

            var codes = new float[GarmentVisualState.Slots];
            codes[3] = 9f;
            GarmentVisualState.ZoneCodes(state, data, codes);
            // Código = daño + 8 · corte (R4b): el desgarro de un zarpazo y la cinta sobre un balazo.
            Assert.AreEqual((float)GarmentDamage.Torn + 8f * (float)GarmentCut.Slash, codes[(int)BodyZone.Chest]);
            Assert.AreEqual((float)GarmentDamage.Patched + 8f * (float)GarmentCut.Bullet, codes[(int)BodyZone.FootR]);
            Assert.AreEqual(0f, codes[3], "lo que no cubre la prenda vuelve a sana");

            var packed = new Vector4[4];
            GarmentVisualState.Pack(codes, packed);
            Assert.AreEqual(codes[(int)BodyZone.Chest], packed[(int)BodyZone.Chest / 4][(int)BodyZone.Chest % 4]);
            Assert.AreEqual(codes[(int)BodyZone.FootR], packed[(int)BodyZone.FootR / 4][(int)BodyZone.FootR % 4]);
        }

        [Test]
        public void LosHuesosSinZonaHeredanLaDeSuPadre()
        {
            var root = new GameObject("Pelvis");
            try
            {
                var spine = Child(Child(root.transform, "LowerSpine"), "MiddleSpine");
                var upper = Child(spine, "UpperSpine");
                var clavicle = Child(upper, "Clavicle.L");
                var hand = Child(Child(Child(clavicle, "UpperArm.L"), "LowerArm.L"), "Hand.L");
                var finger = Child(Child(hand, "IndexFinger.1.L"), "IndexFinger.2.L");
                Assert.AreEqual(BodyZone.HandL, GarmentVisualState.ZoneFromBone(finger));
                Assert.AreEqual(BodyZone.Chest, GarmentVisualState.ZoneFromBone(clavicle));
                Assert.AreEqual(BodyZone.Abdomen, GarmentVisualState.ZoneFromBone(root.transform.Find("LowerSpine")));
                Assert.AreEqual(BodyZone.Chest, GarmentVisualState.ZoneFromBone(null));
                Assert.AreEqual(2, GarmentVisualState.DominantBone(new BoneWeight
                    { boneIndex0 = 0, weight0 = 0.2f, boneIndex1 = 1, weight1 = 0.3f, boneIndex2 = 2, weight2 = 0.5f }));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ElShaderCompila()
        {
            var shader = Shader.Find(ShaderName);
            Assert.IsNotNull(shader, $"no existe {ShaderName}");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), $"{ShaderName} tiene errores de compilación");
        }

        [Test]
        public void LaRopaDeSerieSeRompePorZonas()
        {
            foreach (var asset in new[] { "STP_White T-Shirt", "STP_Military Pants", "STP_Boots" })
            {
                var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"{VendorItems}/{asset}.asset");
                Assert.IsNotNull(definition, asset);
                Assert.IsTrue(definition.TryGetDataOfType(out GarmentZonesData data), $"{asset} sin zonas");
                Assert.Greater(data.Zones.Count, 0, asset);
                Assert.AreEqual(0, data.PocketSlots, $"{asset}: la ropa de serie no trae bolsillos");
                Assert.IsTrue(definition.TryGetDataOfType(out CraftingData _), $"{asset} perdió sus datos del vendor");
            }
        }

        [Test]
        public void ElMunecoPintaLaRoturaEnTodaSuRopa()
        {
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.IsNotNull(variant, $"falta {VariantPath}");
            var clothing = variant.GetComponentInChildren<CharacterPreviewUI>(true)?.GetComponentInChildren<CharacterClothing>(true);
            Assert.IsNotNull(clothing, "el muñeco no trae CharacterClothing");

            var lists = new SerializedObject(clothing).FindProperty("_clothing");
            var wardrobe = new HashSet<SkinnedMeshRenderer>();
            for (int p = 0; p < lists.arraySize; p++)
            {
                var items = lists.GetArrayElementAtIndex(p).FindPropertyRelative("Items");
                for (int i = 0; i < items.arraySize; i++)
                    if (items.GetArrayElementAtIndex(i).FindPropertyRelative("Renderer").objectReferenceValue is SkinnedMeshRenderer r)
                        wardrobe.Add(r);
            }
            Assert.Greater(wardrobe.Count, 8, "el muñeco debería traer la ropa del vendor y la nuestra");

            foreach (var renderer in wardrobe)
            {
                Assert.AreEqual(ShaderName, renderer.sharedMaterial.shader.name, $"{renderer.name} sin el shader de rotura");
                Assert.IsTrue(renderer.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord3), $"{renderer.name}: la malla no lleva su zona en uv3");
                Assert.IsTrue(renderer.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord4), $"{renderer.name}: la malla no lleva sus metros en uv4");
                Assert.IsTrue(AssetDatabase.GetAssetPath(renderer.sharedMesh).StartsWith("Assets/Art/Garments/"),
                    $"{renderer.name}: la malla horneada debe ser una copia nuestra, nunca la del vendor");
            }

            var visuals = clothing.GetComponent<BackroomsGarmentDamageVisuals>();
            Assert.IsNotNull(visuals, "sin BackroomsGarmentDamageVisuals: la rotura no llega al material");
            var so = new SerializedObject(visuals);
            // Tanda 2: además de la lista del vendor, la ropa de encima (que se dibuja sobre el torso).
            int painted = so.FindProperty("_renderers").arraySize;
            Assert.Greater(painted, wardrobe.Count, "la chaqueta de encima también lleva rotura");
            Assert.AreEqual(painted, so.FindProperty("_itemIds").arraySize);
        }

        private static Transform Child(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }
    }
}
