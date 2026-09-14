using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-149 R4a + enm. 7 en el avatar remoto: el pantalón y el calzado de trabajo están en el guardarropa del proxy (viajan
    /// en <c>equipment</c>), la chaqueta va encima con su renderer propio y <c>ProxyGarmentHook</c> pinta la rotura que llega en
    /// la pose. Sin los componentes de UI del muñeco, que leen un inventario que el proxy no tiene.
    /// </summary>
    public class RemoteAvatarGarmentsTests
    {
        private const string AvatarPath = "Assets/_Migration/STPIntegration/Resources/RemotePlayerAvatar.prefab";
        private const string ShaderName = "Backrooms/Garment Lit";

        private static readonly (string Item, BodyPoint Point, string Renderer)[] Looks =
        {
            ("BR_Work Trousers", BodyPoint.Legs, "BR_WorkTrousers"),
            ("BR_Work Boots", BodyPoint.Feet, "BR_WorkBoots"),
            ("BR_Running Shoes", BodyPoint.Feet, "BR_RunningShoes"),
        };

        private static GameObject Avatar()
        {
            var avatar = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            Assert.IsNotNull(avatar, $"falta {AvatarPath}");
            return avatar;
        }

        [Test]
        public void ElProxyConoceNuestrasPrendas()
        {
            var avatar = Avatar();
            var clothing = avatar.GetComponentInChildren<CharacterClothing>(true);
            Assert.IsNotNull(clothing, "el proxy no trae CharacterClothing");

            var lists = new SerializedObject(clothing).FindProperty("_clothing");
            foreach (var look in Looks)
            {
                var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"Assets/Resources/Definitions/Item/{look.Item}.asset");
                Assert.IsNotNull(definition, look.Item);
                var items = lists.GetArrayElementAtIndex((int)look.Point).FindPropertyRelative("Items");
                SkinnedMeshRenderer renderer = null;
                for (int i = 0; i < items.arraySize; i++)
                {
                    var entry = items.GetArrayElementAtIndex(i);
                    if (entry.FindPropertyRelative("Item").FindPropertyRelative("_value").intValue == definition.Id)
                        renderer = entry.FindPropertyRelative("Renderer").objectReferenceValue as SkinnedMeshRenderer;
                }
                Assert.IsNotNull(renderer, $"{look.Item} no está en el guardarropa del proxy ({look.Point}): se vería el hueco vacío");
                Assert.AreEqual(look.Renderer, renderer.name);
                Assert.IsFalse(renderer.gameObject.activeSelf, $"{look.Item} nace apagada: la enciende SetClothing");
                Assert.IsNotNull(renderer.sharedMesh, $"{look.Item} sin malla");
            }

            Assert.IsNull(avatar.GetComponentInChildren<Wearables.BackroomsOuterClothing>(true), "el proxy no lleva el componente de UI de la prenda de encima");
            Assert.IsNull(avatar.GetComponentInChildren<Wearables.BackroomsGarmentDamageVisuals>(true), "el proxy no lleva el componente de UI de la rotura");
        }

        [Test]
        public void ElProxyPintaLaChaquetaYLaRoturaQueLlegan()
        {
            var avatar = Avatar();
            var hook = avatar.GetComponentInChildren(System.Type.GetType("BackroomsSurvival.Migration.STPIntegration.ProxyGarmentHook, Assembly-CSharp"), true);
            Assert.IsNotNull(hook, "sin ProxyGarmentHook: la rotura y la chaqueta de los demás no se verían");
            var so = new SerializedObject(hook);

            int renderers = so.FindProperty("_renderers").arraySize;
            Assert.Greater(renderers, 8, "el hook debe conocer toda la ropa del proxy");
            Assert.AreEqual(renderers, so.FindProperty("_itemIds").arraySize);
            Assert.AreEqual(renderers, so.FindProperty("_points").arraySize);
            Assert.AreEqual(renderers, so.FindProperty("_masks").arraySize);
            for (int i = 0; i < renderers; i++)
            {
                var r = so.FindProperty("_renderers").GetArrayElementAtIndex(i).objectReferenceValue as SkinnedMeshRenderer;
                Assert.IsNotNull(r);
                Assert.AreEqual(ShaderName, r.sharedMaterial.shader.name, $"{r.name} sin el shader de rotura");
                Assert.IsTrue(r.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord3), $"{r.name}: la malla no lleva su zona");
            }

            var jacket = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Work Jacket.asset");
            Assert.AreEqual(1, so.FindProperty("_outerIds").arraySize, "la chaqueta es la prenda de encima");
            Assert.AreEqual(jacket.Id, so.FindProperty("_outerIds").GetArrayElementAtIndex(0).intValue);
            var jacketRenderer = so.FindProperty("_outerRenderers").GetArrayElementAtIndex(0).objectReferenceValue as SkinnedMeshRenderer;
            Assert.IsNotNull(jacketRenderer);
            Assert.IsFalse(jacketRenderer.gameObject.activeSelf, "la chaqueta nace apagada");

            Assert.IsNotNull(so.FindProperty("_body").objectReferenceValue, "sin cuerpo no se destapa la piel");
            Assert.IsNotNull(so.FindProperty("_zoneMap").objectReferenceValue, "sin mapa de zonas");
            Assert.IsNotNull(so.FindProperty("_composeShader").objectReferenceValue, "sin shader de máscara");
        }
    }
}
