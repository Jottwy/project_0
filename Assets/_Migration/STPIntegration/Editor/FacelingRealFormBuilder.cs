#if UNITY_EDITOR
using System.Linq;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// ADR-094 — builds the ADULT faceling's body from the Meshy import, mirroring
    /// <see cref="PhantomRealFormBuilder"/>'s pipeline (same reason it works: the proxy's clips are
    /// already Humanoid muscle curves, so rigging this asset as Humanoid retargets the SAME
    /// ProxyLocomotionController for free — no second controller, no duplicated clips).
    ///
    /// UNLIKE the robapieles, this body is never toggled by a "revealed" flag — a faceling never
    /// disguises (ADR-094: "los facelings no se disfrazan, así que lo dicen siempre"), so
    /// <see cref="FacelingAdultAvatarBuilder"/> nests it ALWAYS ACTIVE, not behind a reveal hook.
    ///
    /// SOURCE: the T-POSE export ("Character_output"), same reasoning as the robapieles' — a
    /// Humanoid avatar auto-maps from the bind pose, and the sibling "Animation_Walking_withSkin"
    /// export is frozen mid-stride, a bad pose to map from. That sibling's own walk clip is not
    /// needed: the body animates from the player's own retargeted clip set, exactly like the
    /// robapieles does.
    /// </summary>
    public static class FacelingRealFormBuilder
    {
        private const string OutputDir = "Assets/_Migration/STPIntegration/Facelings";

        public const string PrefabPath = OutputDir + "/FacelingAdultRealForm.prefab";
        private const string MaterialPath = OutputDir + "/FacelingAdultRealForm.mat";

        private const string SourceDir = "Assets/MeshyImports/faceling-multiview-rig_20260824_164303";
        private const string SourceFbxPath =
            SourceDir + "/Meshy_AI_faceling_multiview_ri_biped_Character_output.fbx";

        // UNLIKE PhantomRealFormBuilder's TargetHeight=0 (that asset's native scale was MEASURED and
        // found already correct): this is a brand new Meshy export with no such measurement on
        // record, and Meshy's own unit scale varies export to export. Normalising to a plausible
        // adult height is the safer default until someone measures this specific asset and can
        // justify trusting its native scale instead.
        private static readonly float TargetHeight = 1.78f;

        [MenuItem("Backrooms/Facelings/Build Adult Real Form")]
        public static void BuildMenu() => BuildOrGet();

        /// <summary>
        /// Returns the adult faceling's body prefab, rigging the source as Humanoid and building the
        /// prefab and material if they are missing. Null with an error logged if the FBX is absent or
        /// its avatar cannot be mapped.
        /// </summary>
        public static GameObject BuildOrGet()
        {
            var avatar = EnsureHumanoidRig();
            if (avatar == null)
                return null;

            var material = EnsureMaterial();

            var prefab = LoadOrCreatePrefab(material);
            if (prefab == null)
                return null;

            ConfigureBody(prefab, avatar);
            return prefab;
        }

        // ----------------------------------------------------------------- rig

        private static Avatar EnsureHumanoidRig()
        {
            var importer = AssetImporter.GetAtPath(SourceFbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[FacelingRealFormBuilder] Source FBX not found: '{SourceFbxPath}'.");
                return null;
            }

            if (importer.animationType != ModelImporterAnimationType.Human ||
                importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.autoGenerateAvatarMappingIfUnspecified = true;
                importer.SaveAndReimport();
                Debug.Log("[FacelingRealFormBuilder] Re-imported the faceling FBX as Humanoid " +
                          "(auto-mapped avatar). This is the step that lets it play the player's clips.");
            }

            var avatar = AssetDatabase.LoadAllAssetsAtPath(SourceFbxPath)
                .OfType<Avatar>()
                .FirstOrDefault();

            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError("[FacelingRealFormBuilder] Humanoid auto-mapping FAILED for " +
                    $"'{SourceFbxPath}' (avatar={(avatar != null ? avatar.name : "<none>")}, " +
                    $"valid={(avatar != null && avatar.isValid)}, human={(avatar != null && avatar.isHuman)}). " +
                    "Open the FBX's Rig tab ▸ Configure and map the missing bones by hand — without a " +
                    "valid avatar the body T-poses and no clip will ever play on it.");
                return null;
            }

            return avatar;
        }

        // -------------------------------------------------------------- prefab

        private static GameObject LoadOrCreatePrefab(Material material)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null)
                return VerifyProvenance(existing) ? existing : null;

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(SourceFbxPath);
            if (fbx == null)
            {
                Debug.LogError($"[FacelingRealFormBuilder] Source FBX not loadable: '{SourceFbxPath}'.");
                return null;
            }

            EnsureOutputDir();

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            try
            {
                instance.name = "FacelingAdultRealForm";

                if (material != null)
                {
                    foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                    {
                        var mats = new Material[renderer.sharedMaterials.Length];
                        for (int i = 0; i < mats.Length; i++)
                            mats[i] = material;
                        renderer.sharedMaterials = mats;
                    }
                }

                NormaliseHeight(instance);

                var saved = PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath, out bool ok);
                if (!ok)
                {
                    Debug.LogError($"[FacelingRealFormBuilder] SaveAsPrefabAsset failed for '{PrefabPath}'.");
                    return null;
                }

                Debug.Log($"[FacelingRealFormBuilder] Minted '{PrefabPath}' from the FBX. " +
                          "Material and scale on it are now YOURS — re-bakes never touch them again.");
                return saved;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static bool VerifyProvenance(GameObject prefab)
        {
            var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            string meshPath = smr != null && smr.sharedMesh != null
                ? AssetDatabase.GetAssetPath(smr.sharedMesh)
                : null;

            if (meshPath == SourceFbxPath)
                return true;

            Debug.LogError($"[FacelingRealFormBuilder] '{PrefabPath}' is skinned to " +
                $"'{meshPath ?? "<no skinned mesh>"}', not to '{SourceFbxPath}'. Only the latter is " +
                "rigged as Humanoid, so this body would T-pose forever. Delete the prefab and re-run " +
                "the bake to mint a correct one, or rebuild yours from that FBX.");
            return false;
        }

        // Same reasoning as PhantomRealFormBuilder.ConfigureBody: the Animator wiring is the
        // builder's job even on a hand-authored prefab, because ProxyAnimatorControllerBuilder mints
        // a fresh asset GUID on every rebuild and the reference has to be re-stamped each bake.
        private static void ConfigureBody(GameObject prefab, Avatar avatar)
        {
            var animator = prefab.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = prefab.AddComponent<Animator>();

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ProxyAnimatorControllerBuilder.OutputPath);
            if (controller == null)
                Debug.LogWarning("[FacelingRealFormBuilder] Proxy controller asset not found at " +
                    $"'{ProxyAnimatorControllerBuilder.OutputPath}'; the body will not animate. " +
                    "Run 'Backrooms ▸ Build Remote Avatar Prefab', which builds it first.");

            bool dirty = false;
            if (animator.runtimeAnimatorController != controller)
            {
                animator.runtimeAnimatorController = controller;
                dirty = true;
            }
            if (animator.avatar != avatar)
            {
                animator.avatar = avatar;
                dirty = true;
            }
            // The proxy's position comes from the network; root motion would fight it.
            if (animator.applyRootMotion)
            {
                animator.applyRootMotion = false;
                dirty = true;
            }
            if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                dirty = true;
            }

            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.updateWhenOffscreen)
                    continue;
                smr.updateWhenOffscreen = true;
                dirty = true;
            }

            if (dirty)
                PrefabUtility.SavePrefabAsset(prefab);
        }

        private static void NormaliseHeight(GameObject instance)
        {
            if (TargetHeight <= 0f)
                return;

            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            if (bounds.size.y <= 0.0001f)
                return;

            float factor = TargetHeight / bounds.size.y;
            instance.transform.localScale *= factor;
            Debug.Log($"[FacelingRealFormBuilder] Measured {bounds.size.y:0.000} m, scaled ×{factor:0.000} " +
                      $"to the {TargetHeight:0.00} m target.");
        }

        private static Material EnsureMaterial()
        {
            var shader = ResolveLitShader();
            if (shader == null)
            {
                Debug.LogWarning("[FacelingRealFormBuilder] No lit shader found for the active pipeline; " +
                    "leaving the FBX materials in place.");
                return null;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "FacelingAdultRealForm" };
                ApplyTextures(mat);
                EnsureOutputDir();
                AssetDatabase.CreateAsset(mat, MaterialPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[FacelingRealFormBuilder] Created '{MaterialPath}' on shader '{shader.name}'.");
                return mat;
            }

            if (mat.shader != shader && IsBuilderShader(mat.shader))
            {
                string was = mat.shader != null ? mat.shader.name : "<null>";
                mat.shader = shader;
                ApplyTextures(mat);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssets();
                Debug.Log($"[FacelingRealFormBuilder] Repaired '{MaterialPath}': shader '{was}' → " +
                          $"'{shader.name}' (it belonged to a pipeline that is not active — that is " +
                          "what renders magenta).");
            }

            return mat;
        }

        // This export shipped basecolor + metallic/smoothness, no normal map — AssignTexture already
        // no-ops (with a warning) on a missing file, so this list is safe to keep matching
        // PhantomRealFormBuilder's even though one entry will not resolve for this specific asset.
        private static void ApplyTextures(Material mat)
        {
            AssignTexture(mat, "_BaseMap", "meshy_basecolor.png");
            AssignTexture(mat, "_MainTex", "meshy_basecolor.png");
            if (AssignTexture(mat, "_BumpMap", "meshy_normal.png"))
                mat.EnableKeyword("_NORMALMAP");
            if (AssignTexture(mat, "_MetallicGlossMap", "meshy_metallic_smoothness.png"))
            {
                mat.EnableKeyword("_METALLICGLOSSMAP");     // Built-in
                mat.EnableKeyword("_METALLICSPECGLOSSMAP"); // URP
                if (mat.HasProperty("_Metallic"))
                    mat.SetFloat("_Metallic", 1f);
                if (mat.HasProperty("_Glossiness"))
                    mat.SetFloat("_Glossiness", 1f);        // Built-in smoothness
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", 1f);        // URP smoothness
            }
        }

        private static Shader ResolveLitShader()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var urp = Shader.Find("Universal Render Pipeline/Lit");
                if (urp != null)
                    return urp;
            }
            return Shader.Find("Standard");
        }

        private static bool IsBuilderShader(Shader shader)
        {
            if (shader == null)
                return true;
            return shader.name == "Standard" || shader.name.StartsWith("Universal Render Pipeline/");
        }

        private static bool AssignTexture(Material mat, string property, string fileName)
        {
            if (!mat.HasProperty(property))
                return false;

            var tex = AssetDatabase.LoadAssetAtPath<Texture>($"{SourceDir}/{fileName}");
            if (tex == null)
            {
                Debug.LogWarning($"[FacelingRealFormBuilder] Texture '{fileName}' not found next to the FBX.");
                return false;
            }

            mat.SetTexture(property, tex);
            return true;
        }

        private static void EnsureOutputDir()
        {
            if (Directory.Exists(OutputDir))
                return;

            Directory.CreateDirectory(OutputDir);
            AssetDatabase.Refresh();
        }
    }
}
#endif
