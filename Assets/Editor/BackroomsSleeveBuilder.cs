using System.Collections.Generic;
using System.IO;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Wearables;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-149 R4c: hornea las fundas de manga de primera persona y el registro que las usa en runtime
    /// (<see cref="SleeveRegistry"/>, en Resources).
    /// Por cada malla de brazo 1P de los wieldables (vendor y proyecto; cada FBX trae la suya) se hace una FUNDA: solo los
    /// triángulos de brazo y antebrazo (hueso dominante), empujados unos milímetros por su normal, con los mismos pesos y
    /// bindposes, y en cada vértice su zona y sus metros alrededor del eje del miembro (como la ropa en 3.ª persona; el frente
    /// es la cara que mira a la cámara). Por cada prenda de manga larga, un material de primera persona con el shader
    /// <c>Backrooms/Garment Lit FP</c> copiado de su material de 3.ª persona. Idempotente.
    /// </summary>
    public static class BackroomsSleeveBuilder
    {
        public const string ShaderName = "Backrooms/Garment Lit FP";
        public const string SleeveFolder = "Assets/Art/Garments/Sleeves";
        public const string RegistryPath = "Assets/Resources/BR_SleeveRegistry.asset";
        public const float ShellOffset = 0.006f;
        public const float FabricTiling = 2.5f;

        private static readonly string[] WieldableFolders =
        {
            "Assets/Prefabs/Wieldables",
            "Assets/Resources/Wieldables",
            "Assets/PolymindGames/STP/Prefabs/Wieldables",
        };

        /// <summary>Prendas de manga larga con material de primera persona: el item y su material de 3.ª persona.</summary>
        private static readonly (string Item, string WorldMaterial)[] Garments =
        {
            ("Assets/Resources/Definitions/Item/BR_Work Jacket.asset", BackroomsGarmentVisualsBuilder.MaterialFolder + "/BR_WorkJacket.mat"),
            ("Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Shirt.asset", BackroomsGarmentVisualsBuilder.MaterialFolder + "/Vendor_Shirt.mat"),
        };

        [MenuItem("Backrooms/Garments/Build 1P Sleeves")]
        public static void BuildMenu()
        {
            int n = Build();
            AssetDatabase.SaveAssets();
            Debug.Log($"[Sleeves] {n} fundas de manga 1P horneadas");
        }

        public static int Build()
        {
            var arms = new List<Mesh>();
            var sleeves = new List<Mesh>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", WieldableFolders))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab == null) continue;
                foreach (var arm in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (arm.name != "LeftArm" && arm.name != "RightArm") continue;
                    if (arm.sharedMesh == null || arms.Contains(arm.sharedMesh)) continue;
                    var sleeve = BakeSleeve(arm);
                    if (sleeve == null) continue;
                    arms.Add(arm.sharedMesh);
                    sleeves.Add(sleeve);
                }
            }

            var shader = Shader.Find(ShaderName);
            if (shader == null) Debug.LogError($"[Sleeves] no existe {ShaderName}");
            var ids = new List<int>();
            var materials = new List<Material>();
            foreach (var (itemPath, worldPath) in Garments)
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(itemPath);
                var world = AssetDatabase.LoadAssetAtPath<Material>(worldPath);
                if (item == null || world == null || shader == null)
                {
                    Debug.LogWarning($"[Sleeves] sin item o material para {itemPath}");
                    continue;
                }
                ids.Add(item.Id);
                materials.Add(EnsureMaterial(world, shader));
            }

            var registry = AssetDatabase.LoadAssetAtPath<SleeveRegistry>(RegistryPath);
            if (registry == null)
            {
                registry = ScriptableObject.CreateInstance<SleeveRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }
            var so = new SerializedObject(registry);
            SetObjects(so.FindProperty("_armMeshes"), arms);
            SetObjects(so.FindProperty("_sleeveMeshes"), sleeves);
            var idArray = so.FindProperty("_garmentIds");
            idArray.arraySize = ids.Count;
            for (int i = 0; i < ids.Count; i++) idArray.GetArrayElementAtIndex(i).intValue = ids[i];
            SetObjects(so.FindProperty("_garmentMaterials"), materials);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(registry);

            AssetDatabase.SaveAssets();
            foreach (var sleeve in sleeves) AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(sleeve), ImportAssetOptions.ForceUpdate);
            return sleeves.Count;
        }

        private static Mesh BakeSleeve(SkinnedMeshRenderer arm)
        {
            var source = arm.sharedMesh;
            var bones = arm.bones;
            var weights = source.boneWeights;
            var vertices = source.vertices;
            var normals = source.normals;
            var uv = source.uv;
            if (weights.Length != vertices.Length || normals.Length != vertices.Length)
            {
                Debug.LogWarning($"[Sleeves] {source.name}: sin pesos o normales por vértice");
                return null;
            }

            var zoneOf = new BodyZone[vertices.Length];
            var inSleeve = new bool[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                int bone = GarmentVisualState.DominantBone(weights[i]);
                inSleeve[i] = bone >= 0 && bone < bones.Length && bones[bone] != null
                              && GarmentVisualState.TryFirstPersonZone(bones[bone].name, out zoneOf[i]);
            }

            var remap = new int[vertices.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var keptTriangles = new List<int>();
            var tris = source.triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (!inSleeve[tris[t]] || !inSleeve[tris[t + 1]] || !inSleeve[tris[t + 2]]) continue;
                keptTriangles.Add(tris[t]);
                keptTriangles.Add(tris[t + 1]);
                keptTriangles.Add(tris[t + 2]);
            }
            if (keptTriangles.Count == 0)
            {
                Debug.LogWarning($"[Sleeves] {source.name}: ningún triángulo de brazo o antebrazo");
                return null;
            }

            var newVertices = new List<Vector3>();
            var newNormals = new List<Vector3>();
            var newUv = new List<Vector2>();
            var newWeights = new List<BoneWeight>();
            var newZones = new List<BodyZone>();
            for (int k = 0; k < keptTriangles.Count; k++)
            {
                int old = keptTriangles[k];
                if (remap[old] < 0)
                {
                    remap[old] = newVertices.Count;
                    newVertices.Add(vertices[old] + normals[old].normalized * ShellOffset);
                    newNormals.Add(normals[old]);
                    newUv.Add(old < uv.Length ? uv[old] : Vector2.zero);
                    newWeights.Add(weights[old]);
                    newZones.Add(zoneOf[old]);
                }
                keptTriangles[k] = remap[old];
            }

            // Ejes de miembro y «frente» (la cara que mira a la cámara, que está en la raíz del rig) desde el esqueleto en reposo.
            var bindposes = source.bindposes;
            var axes = new Vector3[BodyZones.Count];
            var references = new Vector3[BodyZones.Count];
            Vector3 BonePosition(string name)
            {
                for (int i = 0; i < bones.Length && i < bindposes.Length; i++)
                    if (bones[i] != null && bones[i].name == name) return bindposes[i].inverse.MultiplyPoint3x4(Vector3.zero);
                return Vector3.zero;
            }
            var root = arm.rootBone != null ? BonePosition(arm.rootBone.name) : Vector3.zero;
            foreach (var (zone, from, to) in new[]
                     {
                         (BodyZone.UpperArmL, "UpperArm.L", "Forearm.L"), (BodyZone.UpperArmR, "UpperArm.R", "Forearm.R"),
                         (BodyZone.ForearmL, "Forearm.L", "Hand.L"), (BodyZone.ForearmR, "Forearm.R", "Hand.R"),
                     })
            {
                var a = BonePosition(from);
                var b = BonePosition(to);
                axes[(int)zone] = b - a;
                references[(int)zone] = root - (a + b) * 0.5f;
            }

            var coords = new Vector2[newVertices.Count];
            var front = new float[newVertices.Count];
            GarmentVisualState.ProjectZones(newVertices.ToArray(), newZones.ToArray(), Vector3.up, Vector3.forward, coords, front, axes, references);

            var zoneUv = new List<Vector2>(newVertices.Count);
            for (int i = 0; i < newVertices.Count; i++) zoneUv.Add(new Vector2((int)newZones[i], front[i]));

            var mesh = new Mesh { name = $"{source.name}_Sleeve" };
            if (newVertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(newVertices);
            mesh.SetNormals(newNormals);
            mesh.SetUVs(0, newUv);
            mesh.SetUVs(3, zoneUv);
            mesh.SetUVs(4, new List<Vector2>(coords));
            mesh.boneWeights = newWeights.ToArray();
            mesh.bindposes = bindposes;
            mesh.SetTriangles(keptTriangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            EnsureFolder(SleeveFolder);
            string fbx = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(source));
            string path = $"{SleeveFolder}/{fbx}_{source.name}_Sleeve.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static Material EnsureMaterial(Material world, Shader shader)
        {
            string path = $"{BackroomsGarmentVisualsBuilder.MaterialFolder}/FP_{world.name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = $"FP_{world.name}" };
                AssetDatabase.CreateAsset(material, path);
            }
            if (material.shader != shader) material.shader = shader;
            material.CopyPropertiesFromMaterial(world);
            material.SetFloat("_GarmentInflate", 0f);
            material.SetFloat("_GarmentViewBias", 0f);
            material.SetFloat("_GarmentFabricTiling", FabricTiling);
            // Sin mapa de normales: la funda no tiene las UV de la prenda (el shader pone la normal plana).
            material.DisableKeyword("_NORMALMAP");
            if (world.GetTexture("_MetallicGlossMap") != null) material.EnableKeyword("_METALLICSPECGLOSSMAP");
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static void SetObjects<T>(SerializedProperty array, List<T> values) where T : Object
        {
            array.arraySize = values.Count;
            for (int i = 0; i < values.Count; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }
    }
}
