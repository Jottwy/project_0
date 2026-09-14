using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Wearables;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R4c: la manga de la prenda en los brazos de primera persona.</summary>
    public class FirstPersonSleeveTests
    {
        private const string RegistryPath = "Assets/Resources/BR_SleeveRegistry.asset";
        private const string ShaderName = "Backrooms/Garment Lit FP";

        // Espejo de BackroomsSleeveBuilder.WieldableFolders (el ensamblado de tests no ve el del editor).
        private static readonly string[] WieldableFolders =
        {
            "Assets/Prefabs/Wieldables",
            "Assets/Resources/Wieldables",
            "Assets/PolymindGames/STP/Prefabs/Wieldables",
        };

        [Test]
        public void LosHuesosDeLosBrazosDePrimeraPersonaTienenZona()
        {
            Assert.IsTrue(GarmentVisualState.TryFirstPersonZone("UpperArm.L", out var zone));
            Assert.AreEqual(BodyZone.UpperArmL, zone);
            Assert.IsTrue(GarmentVisualState.TryFirstPersonZone("Forearm.R", out zone));
            Assert.AreEqual(BodyZone.ForearmR, zone);
            Assert.IsTrue(GarmentVisualState.TryFirstPersonZone("ForearmTwist.3.L", out zone), "los huesos de giro son antebrazo");
            Assert.AreEqual(BodyZone.ForearmL, zone);
            Assert.IsFalse(GarmentVisualState.TryFirstPersonZone("Hand.L", out _), "la mano no lleva manga");
            Assert.IsFalse(GarmentVisualState.TryFirstPersonZone("Index.2.R", out _));
            Assert.IsFalse(GarmentVisualState.TryFirstPersonZone("Root", out _));
        }

        [Test]
        public void SoloLaMangaLargaSeVeEnPrimeraPersona()
        {
            Assert.IsTrue(FirstPersonSleeves.HasLongSleeves(new GarmentZonesData(new GarmentZone(BodyZone.Chest, 0.1f), new GarmentZone(BodyZone.ForearmR, 0.1f))));
            Assert.IsFalse(FirstPersonSleeves.HasLongSleeves(new GarmentZonesData(new GarmentZone(BodyZone.Chest, 0.1f), new GarmentZone(BodyZone.UpperArmL, 0.1f))),
                "una camiseta no llega al antebrazo, que es lo que se ve en primera persona");
            Assert.IsFalse(FirstPersonSleeves.HasLongSleeves(null));
        }

        [Test]
        public void ElShaderDePrimeraPersonaCompila()
        {
            var shader = Shader.Find(ShaderName);
            Assert.IsNotNull(shader, $"no existe {ShaderName}");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), $"{ShaderName} tiene errores de compilación");
            Assert.AreEqual(-1, shader.FindPropertyIndex("_FOV"), "_FOV es global del vendor: nunca en Properties (ADR-077 enm. 2)");
            Assert.AreEqual(-1, shader.FindPropertyIndex("_FOVEnabled"), "_FOVEnabled es global del vendor: nunca en Properties");
        }

        [Test]
        public void CadaBrazoDePrimeraPersonaTieneSuFunda()
        {
            var registry = AssetDatabase.LoadAssetAtPath<SleeveRegistry>(RegistryPath);
            Assert.IsNotNull(registry, $"falta {RegistryPath}: lanza Backrooms/Garments/Build 1P Sleeves");

            var arms = new HashSet<Mesh>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", WieldableFolders))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab == null) continue;
                foreach (var arm in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if ((arm.name == "LeftArm" || arm.name == "RightArm") && arm.sharedMesh != null)
                        arms.Add(arm.sharedMesh);
            }
            Assert.Greater(arms.Count, 0, "no se encontraron brazos de primera persona");

            foreach (var arm in arms)
            {
                var sleeve = registry.SleeveFor(arm);
                Assert.IsNotNull(sleeve, $"{AssetDatabase.GetAssetPath(arm)} ({arm.name}) sin funda de manga");
                Assert.AreEqual(arm.bindposes.Length, sleeve.bindposes.Length, $"{sleeve.name}: otros bindposes que su brazo");
                Assert.IsTrue(sleeve.HasVertexAttribute(VertexAttribute.TexCoord3), $"{sleeve.name}: sin zona en uv3");
                Assert.IsTrue(sleeve.HasVertexAttribute(VertexAttribute.TexCoord4), $"{sleeve.name}: sin metros en uv4");
                Assert.IsTrue(sleeve.HasVertexAttribute(VertexAttribute.BlendWeight), $"{sleeve.name}: sin pesos de hueso");
            }

            var jacket = AssetDatabase.LoadAssetAtPath<PolymindGames.InventorySystem.ItemDefinition>("Assets/Resources/Definitions/Item/BR_Work Jacket.asset");
            var material = registry.MaterialFor(jacket.Id);
            Assert.IsNotNull(material, "la chaqueta sin material de primera persona");
            Assert.AreEqual(ShaderName, material.shader.name);
        }
    }
}
