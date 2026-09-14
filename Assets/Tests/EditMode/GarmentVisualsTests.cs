using BackroomsSurvival.Wearables;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R4a: nuestras prendas se ven en el muñeco del inventario; la de encima, sobre lo del torso (R4b tanda 2).</summary>
    public class GarmentVisualsTests
    {
        private const string VariantPath = "Assets/Prefabs/UI/BR_UI_Player.prefab";
        private const string JacketPath = "Assets/Resources/Definitions/Item/BR_Work Jacket.asset";

        // Espejo de BackroomsGarmentVisualsBuilder.Looks sin la de encima (el ensamblado de tests no ve el del editor).
        private static readonly (string Item, BodyPoint Point, string Renderer)[] Looks =
        {
            ("BR_Work Trousers", BodyPoint.Legs, "BR_WorkTrousers"),
            ("BR_Work Boots", BodyPoint.Feet, "BR_WorkBoots"),
            ("BR_Running Shoes", BodyPoint.Feet, "BR_RunningShoes"),
        };

        [Test]
        public void LaPrendaDeEncimaSeEnciendeSoloSiTieneMalla()
        {
            var withMesh = new[] { 7, 11 };
            Assert.AreEqual(0, BackroomsOuterClothing.OuterIndex(7, withMesh), "chaqueta puesta: su renderer");
            Assert.AreEqual(1, BackroomsOuterClothing.OuterIndex(11, withMesh));
            Assert.AreEqual(-1, BackroomsOuterClothing.OuterIndex(0, withMesh), "nada puesto: ninguno");
            Assert.AreEqual(-1, BackroomsOuterClothing.OuterIndex(9, withMesh), "una prenda de encima sin malla no enciende nada");
        }

        [Test]
        public void ElMunecoLlevaNuestrasPrendas()
        {
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.IsNotNull(variant, $"falta {VariantPath}");
            var preview = variant.GetComponentInChildren<CharacterPreviewUI>(true);
            Assert.IsNotNull(preview, "la variante no trae el muñeco");
            var clothing = preview.GetComponentInChildren<CharacterClothing>(true);
            Assert.IsNotNull(clothing, "el muñeco no trae CharacterClothing");

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
                    if (entry.FindPropertyRelative("Item").FindPropertyRelative("_value").intValue != definition.Id) continue;
                    renderer = entry.FindPropertyRelative("Renderer").objectReferenceValue as SkinnedMeshRenderer;
                    // Las botas del vendor tampoco llevan máscara: el pie no se recorta.
                    if (look.Point != BodyPoint.Feet)
                        Assert.IsNotNull(entry.FindPropertyRelative("OpacityMask").objectReferenceValue, $"{look.Item} sin máscara de piel");
                }
                Assert.IsNotNull(renderer, $"{look.Item} no está en la ropa del muñeco ({look.Point})");
                Assert.AreEqual(look.Renderer, renderer.name);
                Assert.IsFalse(renderer.gameObject.activeSelf, $"{look.Item} debe nacer apagada: la enciende CharacterClothing");
                Assert.IsNotNull(renderer.sharedMesh, $"{look.Item} sin malla");
                Assert.AreEqual($"Assets/Art/Garments/Materials/{look.Renderer}.mat", AssetDatabase.GetAssetPath(renderer.sharedMaterial),
                    $"{look.Item} con el material del vendor");
            }

            // La chaqueta no entra en la lista del torso del vendor (que pinta una sola prenda): va encima, aparte.
            var jacket = AssetDatabase.LoadAssetAtPath<ItemDefinition>(JacketPath);
            Assert.IsNotNull(jacket);
            var torso = lists.GetArrayElementAtIndex((int)BodyPoint.Torso).FindPropertyRelative("Items");
            for (int i = 0; i < torso.arraySize; i++)
                Assert.AreNotEqual(jacket.Id, torso.GetArrayElementAtIndex(i).FindPropertyRelative("Item").FindPropertyRelative("_value").intValue,
                    "la chaqueta sigue en la lista del torso: taparía la camiseta en vez de ir encima");

            var outer = clothing.GetComponent<BackroomsOuterClothing>();
            Assert.IsNotNull(outer, "sin BackroomsOuterClothing: la chaqueta no se vería");
            var os = new SerializedObject(outer);
            Assert.AreEqual(1, os.FindProperty("_outerIds").arraySize, "la chaqueta es la única prenda de encima con malla");
            Assert.AreEqual(jacket.Id, os.FindProperty("_outerIds").GetArrayElementAtIndex(0).intValue);
            var jacketRenderer = os.FindProperty("_outerRenderers").GetArrayElementAtIndex(0).objectReferenceValue as SkinnedMeshRenderer;
            Assert.IsNotNull(jacketRenderer, "la chaqueta sin renderer");
            Assert.AreEqual("BR_WorkJacket", jacketRenderer.name);
            Assert.IsFalse(jacketRenderer.gameObject.activeSelf, "la chaqueta nace apagada: la enciende BackroomsOuterClothing");
            Assert.Greater(jacketRenderer.sharedMaterial.GetFloat("_GarmentInflate"), 0f, "la de encima debe hincharse o pisa la camiseta");
        }
    }
}
