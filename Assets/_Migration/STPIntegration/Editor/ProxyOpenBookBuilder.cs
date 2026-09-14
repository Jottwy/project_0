#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// Hornea <c>Resources/ProxyOpenBook.prefab</c>: el libro de supervivencia ABIERTO que el vecino sujeta cuando
    /// lleva el bit <c>BookOpen</c> (2026-09-14). Idempotente: re-ejecutarlo reescribe la malla en el mismo asset.
    ///
    /// DE DÓNDE SALE: el único libro abierto del vendor es el de primera persona, <c>FP_Book.fbx</c>, una malla con
    /// piel cuyas tapas abren sus huesos <c>BookPages.l/.r</c>. En la pose de reposo sale casi cerrado; el clip
    /// <c>Book_Hold</c> lo abre (medido: 32 × 24 cm y 4,5 cm de fondo en V). Se muestrea ese clip, se hornea la malla
    /// a estática y se deja sin brazos. El libro cerrado de <c>SurvivalBook.fbx</c> es la misma malla sin huesos, así
    /// que no sirve abierto.
    ///
    /// EL MATERIAL es el que el propio FBX remapea, <c>SurvivalBook.mat</c> (URP/Lit de mundo). El de primera persona,
    /// <c>FP_SurvivalBook.mat</c>, lleva el alabeo de FOV del viewmodel y deformaría el libro en un proxy (ADR-077 enm. 2).
    ///
    /// EL MARCO: origen en el centro del libro abierto, +Z hacia las PÁGINAS (medido: el −Y del hueso Book; por +Y se
    /// ven las tapas), +Y a lo largo del lomo y +X de tapa a tapa. Es el marco que espera <c>ProxyBookHold</c>.
    ///
    /// Estática y no con piel: nada del libro se mueve en el vecino, y una malla estática no paga skinning por proxy.
    /// </summary>
    public static class ProxyOpenBookBuilder
    {
        private const string ModelPath = "Assets/PolymindGames/STP/Art/Models/Wieldables/SurvivalBook/FP_Book.fbx";
        private const string ClipName = "Book_Hold";
        private const string RendererName = "Survival Book";
        private const string BookBoneName = "Book";
        private const string OutputDir = "Assets/_Migration/STPIntegration/Resources";
        private const string MeshPath = OutputDir + "/ProxyOpenBook_Mesh.asset";
        private const string PrefabPath = OutputDir + "/ProxyOpenBook.prefab";

        [MenuItem("Backrooms/Build Proxy Open Book")]
        public static void Build() => BuildInternal();

        /// <summary>Para un Unity en batch: <c>-executeMethod ...ProxyOpenBookBuilder.BuildAndExit</c>.</summary>
        public static void BuildAndExit()
        {
            bool ok = false;
            try { ok = BuildInternal(); }
            finally { EditorApplication.Exit(ok ? 0 : 1); }
        }

        private static bool BuildInternal()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            AnimationClip clip = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            {
                if (o is AnimationClip c && c.name == ClipName)
                {
                    clip = c;
                    break;
                }
            }
            if (model == null || clip == null)
            {
                Debug.LogError($"[ProxyOpenBookBuilder] Falta el modelo o el clip {ClipName} en {ModelPath}.");
                return false;
            }

            var instance = Object.Instantiate(model);
            try
            {
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                clip.SampleAnimation(instance, 0f);

                SkinnedMeshRenderer book = null;
                foreach (var r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (r.name == RendererName) book = r;
                Transform bone = null;
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                    if (t.name == BookBoneName) bone = t;
                if (book == null || bone == null)
                {
                    Debug.LogError($"[ProxyOpenBookBuilder] No encuentro '{RendererName}' o el hueso '{BookBoneName}' en {ModelPath}.");
                    return false;
                }

                var baked = new Mesh();
                book.BakeMesh(baked, true);
                Matrix4x4 bakedToWorld = Matrix4x4.TRS(book.transform.position, book.transform.rotation, Vector3.one);

                // Centro del libro abierto: centro de su caja en el espacio del hueso Book, que es donde sale ajustada.
                Vector3[] vertices = baked.vertices;
                var inBone = new Bounds(bone.InverseTransformPoint(bakedToWorld.MultiplyPoint3x4(vertices[0])), Vector3.zero);
                for (int i = 1; i < vertices.Length; i++)
                    inBone.Encapsulate(bone.InverseTransformPoint(bakedToWorld.MultiplyPoint3x4(vertices[i])));
                Vector3 origin = bone.TransformPoint(inBone.center);
                Quaternion frame = Quaternion.LookRotation(-bone.up, bone.right);
                Matrix4x4 toBook = Matrix4x4.TRS(origin, frame, Vector3.one).inverse * bakedToWorld;

                var normals = baked.normals;
                var tangents = baked.tangents;
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = toBook.MultiplyPoint3x4(vertices[i]);
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = toBook.MultiplyVector(normals[i]).normalized;
                for (int i = 0; i < tangents.Length; i++)
                {
                    Vector3 t = toBook.MultiplyVector(tangents[i]).normalized;
                    tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w);
                }

                var mesh = new Mesh { name = "ProxyOpenBook", indexFormat = baked.indexFormat };
                mesh.SetVertices(vertices);
                if (normals.Length == vertices.Length) mesh.SetNormals(normals);
                if (tangents.Length == vertices.Length) mesh.SetTangents(tangents);
                var uvs = new List<Vector2>();
                for (int channel = 0; channel < 4; channel++)
                {
                    uvs.Clear();
                    baked.GetUVs(channel, uvs);
                    if (uvs.Count == vertices.Length)
                        mesh.SetUVs(channel, uvs);
                }
                mesh.subMeshCount = baked.subMeshCount;
                for (int s = 0; s < baked.subMeshCount; s++)
                    mesh.SetTriangles(baked.GetTriangles(s), s);
                mesh.RecalculateBounds();
                Object.DestroyImmediate(baked);

                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
                if (existing != null)
                {
                    // Mismo asset, mismo GUID: el prefab no pierde la referencia al re-hornear.
                    EditorUtility.CopySerialized(mesh, existing);
                    Object.DestroyImmediate(mesh);
                    mesh = existing;
                    EditorUtility.SetDirty(mesh);
                }
                else
                {
                    AssetDatabase.CreateAsset(mesh, MeshPath);
                }
                AssetDatabase.SaveAssets();

                var go = new GameObject("ProxyOpenBook");
                try
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = book.sharedMaterials;
                    PrefabUtility.SaveAsPrefabAsset(go, PrefabPath, out bool saved);
                    if (!saved)
                    {
                        Debug.LogError($"[ProxyOpenBookBuilder] No se pudo guardar {PrefabPath}.");
                        return false;
                    }
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }

                AssetDatabase.SaveAssets();
                Debug.Log($"[ProxyOpenBookBuilder] {PrefabPath}: {vertices.Length} vértices, caja {mesh.bounds.size:F3} " +
                    $"centrada en {mesh.bounds.center:F3}, material {(book.sharedMaterial != null ? book.sharedMaterial.name : "<ninguno>")}.");
                return true;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }
}
#endif
