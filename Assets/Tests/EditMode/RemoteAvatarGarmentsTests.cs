using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-149 R4a en el avatar remoto (sin wire): el pantalón y el calzado de trabajo viajan en <c>equipment</c> (ADR-022), así
    /// que el guardarropa del proxy tiene que conocerlos o <c>SetClothing</c> deja el hueco vacío (piernas desnudas).
    /// </summary>
    public class RemoteAvatarGarmentsTests
    {
        private const string AvatarPath = "Assets/_Migration/STPIntegration/Resources/RemotePlayerAvatar.prefab";

        private static readonly (string Item, BodyPoint Point, string Renderer)[] Looks =
        {
            ("BR_Work Trousers", BodyPoint.Legs, "BR_WorkTrousers"),
            ("BR_Work Boots", BodyPoint.Feet, "BR_WorkBoots"),
            ("BR_Running Shoes", BodyPoint.Feet, "BR_RunningShoes"),
        };

        [Test]
        public void ElProxyConoceNuestrasPrendas()
        {
            var avatar = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            Assert.IsNotNull(avatar, $"falta {AvatarPath}");
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

            // Los componentes del muñeco leen el inventario del personaje: en el proxy no hay y reventarían.
            Assert.IsNull(avatar.GetComponentInChildren<Wearables.BackroomsOuterClothing>(true), "el proxy no lleva el componente de UI de la prenda de encima");
            Assert.IsNull(avatar.GetComponentInChildren<Wearables.BackroomsGarmentDamageVisuals>(true), "el proxy no lleva el componente de UI de la rotura");
        }
    }
}
