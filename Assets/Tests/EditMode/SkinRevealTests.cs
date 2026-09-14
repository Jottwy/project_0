using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Wearables;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R4b tanda 2 (Joel: «debe mostrar la prenda de debajo o el cuerpo»).</summary>
    public class SkinRevealTests
    {
        private const string VariantPath = "Assets/Prefabs/UI/BR_UI_Player.prefab";

        private static GarmentZonesData TShirt() => new GarmentZonesData(
            new GarmentZone(BodyZone.Chest, 0.05f), new GarmentZone(BodyZone.Abdomen, 0.05f));

        private static GarmentZonesData Jacket() => new GarmentZonesData(
            new GarmentZone(BodyZone.Chest, 0.2f), new GarmentZone(BodyZone.ForearmL, 0.2f));

        [Test]
        public void LaPielSeVeSoloDondeLaCapaDeDentroEstaAbierta()
        {
            var reveal = new bool[GarmentVisualState.Slots];
            var tshirt = TShirt();
            var jacket = Jacket();
            var inner = new GarmentState();
            var outer = new GarmentState();

            outer.ApplyHit(0, jacket.Zones[0], DamageType.Slash, 30f, out _);
            GarmentVisualState.SkinReveal(inner, tshirt, outer, jacket, reveal);
            Assert.IsFalse(reveal[(int)BodyZone.Chest], "chaqueta rota sobre camiseta sana: se ve la camiseta, no la piel");

            outer.ApplyHit(1, jacket.Zones[1], DamageType.Ballistic, 8f, out _);
            GarmentVisualState.SkinReveal(inner, tshirt, outer, jacket, reveal);
            Assert.IsTrue(reveal[(int)BodyZone.ForearmL], "la camiseta no llega al antebrazo: el agujero de la chaqueta enseña piel");

            inner.ApplyHit(0, tshirt.Zones[0], DamageType.Pierce, 8f, out _);
            GarmentVisualState.SkinReveal(inner, tshirt, outer, jacket, reveal);
            Assert.IsTrue(reveal[(int)BodyZone.Chest], "camiseta abierta: piel");
            Assert.IsFalse(reveal[(int)BodyZone.Abdomen]);

            inner.Repair(0, GarmentRepair.Tape);
            GarmentVisualState.SkinReveal(inner, tshirt, null, null, reveal);
            Assert.IsFalse(reveal[(int)BodyZone.Chest], "con cinta vuelve a tapar");
            Assert.IsFalse(reveal[(int)BodyZone.ForearmL], "sin capa de encima no hay nada roto en el antebrazo");
        }

        [Test]
        public void ElMunecoComponeLaMascaraDePiel()
        {
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.IsNotNull(variant, $"falta {VariantPath}");
            var clothing = variant.GetComponentInChildren<CharacterPreviewUI>(true)?.GetComponentInChildren<CharacterClothing>(true);
            Assert.IsNotNull(clothing);
            var visuals = clothing.GetComponent<BackroomsGarmentDamageVisuals>();
            Assert.IsNotNull(visuals);
            var so = new SerializedObject(visuals);

            int renderers = so.FindProperty("_renderers").arraySize;
            Assert.AreEqual(renderers, so.FindProperty("_points").arraySize, "cada renderer necesita su parte del cuerpo");
            Assert.AreEqual(renderers, so.FindProperty("_masks").arraySize, "cada renderer necesita su máscara de piel");
            int outers = 0;
            var points = so.FindProperty("_points");
            for (int i = 0; i < points.arraySize; i++)
                if (points.GetArrayElementAtIndex(i).intValue == BackroomsGarmentDamageVisuals.OuterPoint) outers++;
            Assert.AreEqual(1, outers, "la chaqueta es la capa de encima");

            Assert.IsNotNull(so.FindProperty("_body").objectReferenceValue, "sin el cuerpo no se destapa la piel");
            var shader = so.FindProperty("_composeShader").objectReferenceValue as Shader;
            Assert.IsNotNull(shader, "sin el shader que compone la máscara");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), $"{shader.name} tiene errores");

            var zoneMap = so.FindProperty("_zoneMap").objectReferenceValue as Texture2D;
            Assert.IsNotNull(zoneMap, "sin mapa de zonas del cuerpo");
            var texels = zoneMap.GetPixelData<byte>(0);
            var seen = new bool[GarmentVisualState.Slots + 1];
            foreach (byte b in texels) if (b < seen.Length) seen[b] = true;
            foreach (var zone in new[] { BodyZone.Chest, BodyZone.Abdomen, BodyZone.ForearmL, BodyZone.ThighR, BodyZone.FootL })
                Assert.IsTrue(seen[(int)zone + 1], $"el mapa de zonas no pinta {zone}");
        }
    }
}
