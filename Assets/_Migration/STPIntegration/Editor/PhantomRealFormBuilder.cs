#if UNITY_EDITOR
using System.Linq;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// ADR-038 V2: builds the robapieles' REAL FORM — the body the phantom breaks out into while its
    /// networked <c>revealed</c> flag is true (SPRINT lunge / STATUE freeze). V1 swapped the proxy's
    /// MATERIAL because the vendor mesh is skinned to the MTP_PlayerViewer skeleton and a separately
    /// rigged humanoid has a different bone hierarchy. That constraint is real and unchanged; the way
    /// out of it is not to share the skeleton but to share the ANIMATION.
    ///
    /// THE WHOLE DESIGN RESTS ON ONE FACT ABOUT THE EXISTING PIPELINE: the proxy's clips are already
    /// HUMANOID. <c>MaleSurvivor.fbx</c> imports as Human/CreateFromThisModel and every clip FBX under
    /// RemoteAvatar/Animations as Human/CopyFromOther. Humanoid clips are stored as muscle curves, not
    /// as per-bone paths, so they retarget onto ANY valid Humanoid avatar. Rig this creature as
    /// Humanoid and it inherits idle, walk, run, crouch, jump and pickup for free — the SAME
    /// ProxyLocomotionController the disguise runs, no duplicated clips, no second controller to keep
    /// in sync. (The doc comment on ProxyAnimatorControllerBuilder claiming the pipeline is Generic is
    /// stale; the .meta files are the authority.)
    ///
    /// WHICH EXPORT, AND WHY IT IS NOT INTERCHANGEABLE. Meshy produced two files. This builder uses
    /// "Character_output" — the T-POSE — because a Humanoid avatar is auto-mapped from the bind pose,
    /// and its sibling "…Animation…withSkin" is frozen mid-stride, which is a bad pose to map from.
    /// The sibling's one clip (a travelling charge) is not needed at all now: the body animates from
    /// the player's own clip set.
    ///
    /// THE AVATAR IS VALIDATED, NOT ASSUMED. Auto-mapping is a name/hierarchy heuristic and it can
    /// fail. A failed map produces an invalid Avatar and a body that T-poses forever with no error
    /// anywhere, so <see cref="EnsureHumanoidRig"/> checks <c>isValid</c>/<c>isHuman</c> and logs an
    /// error naming the file.
    ///
    /// WHAT THIS BUILDER OWNS vs WHAT THE AUTHOR OWNS: it owns the rig setup, the Animator wiring and
    /// the body's SCALE (see <see cref="RealFormScale"/> — a scale that survived only until the next
    /// re-bake would be a trap). If a prefab already exists at <see cref="PrefabPath"/> its hierarchy
    /// and material assignments are used AS-IS. The material ASSET is created if absent and only ever
    /// repaired when it sits on a shader whose render pipeline is not active — the magenta case.
    /// </summary>
    public static class PhantomRealFormBuilder
    {
        private const string OutputDir = "Assets/_Migration/STPIntegration/Resources";

        /// <summary>Hand-authored body prefab. Present ⇒ used as-is; absent ⇒ minted from the FBX.</summary>
        public const string PrefabPath = OutputDir + "/PhantomRealForm.prefab";

        private const string MaterialPath = OutputDir + "/PhantomRealForm.mat";

        // The T-POSE export. See the class doc for why the animated sibling is not a substitute.
        private const string SourceDir = "Assets/MeshyImports/The Lithe Pale One_20260802_005247";
        private const string SourceFbxPath =
            SourceDir + "/Meshy_AI_The_Lithe_Pale_One_biped_Character_output.fbx";

        /// <summary>Screams played when the disguise drops. Baked into the hook by the prefab builder.</summary>
        public const string ScreamDir = "Assets/_Migration/STPIntegration/RemoteAvatar/Audio";

        // Native height of the asset (~1.655 m). 0 = do not rescale. Set to a positive metre value to
        // normalise the revealed body to that height at bake time (a taller silhouette reads as
        // "it grew" rather than "its skin came off" — deliberate call, not a default).
        // static readonly and not const: as a const the compiler folds the `<= 0` guard and reports the
        // whole measuring routine as unreachable code on every reload.
        private static readonly float TargetHeight = 0f;

        /// <summary>
        /// Uniform scale of the revealed body. ×2 on a 1.655 m asset gives a 3.31 m creature: the thing
        /// that stole your silhouette does not just change texture, it UNFOLDS to twice your height.
        ///
        /// The builder owns this field and re-stamps it on every bake, unlike the materials — a scale
        /// that survived only until the next re-bake would be a trap. NOTE for whoever changes it: the
        /// rendered ceiling is 3.3 m (ChunkRenderer.ceilingHeight), so ×2 is already ABOVE it and the
        /// head grazes/pokes through; ×1.95 (3.23 m) is the tallest that clears a standard corridor.
        /// </summary>
        private const float RealFormScale = 2f;

        [MenuItem("Backrooms/Build Phantom Real Form")]
        public static void BuildMenu() => BuildOrGet();

        /// <summary>
        /// Returns the real-form body prefab, rigging the source as Humanoid and building the prefab
        /// and material if they are missing. Null with an error logged if the FBX is absent or its
        /// avatar cannot be mapped.
        /// </summary>
        public static GameObject BuildOrGet()
        {
            var avatar = EnsureHumanoidRig();
            if (avatar == null)
                return null;

            // Before the prefab: an ALREADY-minted prefab points at this material asset, so repairing
            // the asset in place is what un-magentas a body this builder is no longer allowed to
            // re-create.
            var material = EnsureMaterial();

            var prefab = LoadOrCreatePrefab(material);
            if (prefab == null)
                return null;

            ConfigureBody(prefab, avatar);
            return prefab;
        }

        // ----------------------------------------------------------------- rig

        // Flips the import to Humanoid and returns the generated Avatar. The reimport is GUARDED: the
        // source is a 75 MB FBX and re-importing it on every prefab bake would cost minutes for
        // nothing.
        private static Avatar EnsureHumanoidRig()
        {
            var importer = AssetImporter.GetAtPath(SourceFbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[PhantomRealFormBuilder] Source FBX not found: '{SourceFbxPath}'.");
                return null;
            }

            if (importer.animationType != ModelImporterAnimationType.Human ||
                importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.autoGenerateAvatarMappingIfUnspecified = true;
                importer.SaveAndReimport();
                Debug.Log("[PhantomRealFormBuilder] Re-imported the real-form FBX as Humanoid " +
                          "(auto-mapped avatar). This is the step that lets it play the player's clips.");
            }

            var avatar = AssetDatabase.LoadAllAssetsAtPath(SourceFbxPath)
                .OfType<Avatar>()
                .FirstOrDefault();

            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError("[PhantomRealFormBuilder] Humanoid auto-mapping FAILED for " +
                    $"'{SourceFbxPath}' (avatar={(avatar != null ? avatar.name : "<none>")}, " +
                    $"valid={(avatar != null && avatar.isValid)}, human={(avatar != null && avatar.isHuman)}). " +
                    "Open the FBX's Rig tab ▸ Configure and map the missing bones by hand — without a " +
                    "valid avatar the revealed body T-poses and no clip will ever play on it.");
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
                Debug.LogError($"[PhantomRealFormBuilder] Source FBX not loadable: '{SourceFbxPath}'.");
                return null;
            }

            EnsureOutputDir();

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            try
            {
                instance.name = "PhantomRealForm";

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
                    Debug.LogError($"[PhantomRealFormBuilder] SaveAsPrefabAsset failed for '{PrefabPath}'.");
                    return null;
                }

                Debug.Log($"[PhantomRealFormBuilder] Minted '{PrefabPath}' from the FBX. " +
                          "Materials and scale on it are now YOURS — re-bakes never touch them again.");
                return saved;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // An existing prefab is honoured as-is, so it had better be built on the rig this builder rigs
        // as Humanoid. A body skinned to a DIFFERENT export gets no avatar, retargets nothing and
        // T-poses through the whole lunge without a single error — so the check is by mesh provenance
        // (the mesh can only come from its own FBX) and it REFUSES rather than guessing or deleting
        // someone's asset.
        private static bool VerifyProvenance(GameObject prefab)
        {
            var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            string meshPath = smr != null && smr.sharedMesh != null
                ? AssetDatabase.GetAssetPath(smr.sharedMesh)
                : null;

            if (meshPath == SourceFbxPath)
                return true;

            Debug.LogError($"[PhantomRealFormBuilder] '{PrefabPath}' is skinned to " +
                $"'{meshPath ?? "<no skinned mesh>"}', not to '{SourceFbxPath}'. Only the latter is " +
                "rigged as Humanoid, so this body would T-pose through every reveal. Delete the prefab " +
                "and re-run the bake to mint a correct one, or rebuild yours from that FBX.");
            return false;
        }

        // Animator setup is the builder's job even on a hand-authored prefab: nobody is going to wire an
        // AnimatorController by hand, and ProxyAnimatorControllerBuilder mints a fresh asset GUID on
        // every rebuild, so the reference has to be re-stamped each bake (the same guarantee
        // RemoteAvatarPrefabBuilder.EnsureControllerBound gives the proxy).
        private static void ConfigureBody(GameObject prefab, Avatar avatar)
        {
            var animator = prefab.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = prefab.AddComponent<Animator>();

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ProxyAnimatorControllerBuilder.OutputPath);
            if (controller == null)
                Debug.LogWarning("[PhantomRealFormBuilder] Proxy controller asset not found at " +
                    $"'{ProxyAnimatorControllerBuilder.OutputPath}'; the real form will not animate. " +
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
            // Same fix ADR-038's first amendment applied to the proxy: culled skinned meshes drift and
            // vanish. A body that only ever appears in a lunge must not be the one that flickers.
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

            var scale = Vector3.one * RealFormScale;
            if (prefab.transform.localScale != scale)
            {
                prefab.transform.localScale = scale;
                dirty = true;
                Debug.Log($"[PhantomRealFormBuilder] Real form scaled ×{RealFormScale:0.00} " +
                          "(the reveal is a growth, not just a skin change).");
            }

            if (dirty)
                PrefabUtility.SavePrefabAsset(prefab);
        }

        // Scale is measured, never assumed: the two Meshy exports declare different FBX unit factors
        // and still land at the same ~1.655 m, so a hard multiplier would be a guess that silently rots.
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
            Debug.Log($"[PhantomRealFormBuilder] Measured {bounds.size.y:0.000} m, scaled ×{factor:0.000} " +
                      $"to the {TargetHeight:0.00} m target.");
        }

        /// <summary>
        /// The material, created if absent and REPAIRED if it is on a shader whose render pipeline is
        /// not the active one. That repair is not optional book-keeping: a URP shader in a Built-in
        /// project (or the reverse) compiles fine, imports fine, logs nothing, and renders MAGENTA.
        /// This project has the URP package installed but NO pipeline asset assigned, so
        /// <c>Shader.Find("Universal Render Pipeline/Lit")</c> succeeds while URP is inactive — the
        /// exact trap this method exists to close. Same test MaterialHelper uses at runtime.
        ///
        /// Repair only touches a material still on one of the two shaders this builder would itself
        /// have set. Anything else is the author's choice and is left alone.
        /// </summary>
        private static Material EnsureMaterial()
        {
            var shader = ResolveLitShader();
            if (shader == null)
            {
                Debug.LogWarning("[PhantomRealFormBuilder] No lit shader found for the active pipeline; " +
                    "leaving the FBX materials in place.");
                return null;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "PhantomRealForm" };
                ApplyTextures(mat);
                AssetDatabase.CreateAsset(mat, MaterialPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[PhantomRealFormBuilder] Created '{MaterialPath}' on shader '{shader.name}'.");
                return mat;
            }

            if (mat.shader != shader && IsBuilderShader(mat.shader))
            {
                string was = mat.shader != null ? mat.shader.name : "<null>";
                mat.shader = shader;
                // Property names differ between the two pipelines, so a pipeline switch has to re-bind
                // the maps or the body comes back untextured instead of magenta.
                ApplyTextures(mat);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssets();
                Debug.Log($"[PhantomRealFormBuilder] Repaired '{MaterialPath}': shader '{was}' → " +
                          $"'{shader.name}' (it belonged to a pipeline that is not active — that is " +
                          "what renders magenta).");
            }

            return mat;
        }

        // Built-in and URP name the same maps differently, so both sets are written; HasProperty makes
        // the ones that do not apply free.
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
            // An SRP asset assigned is the ONLY thing that makes a URP shader renderable. The package
            // being installed says nothing — it is installed here and URP is not active.
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
                return true; // a missing shader is broken by definition; repairing it can only help
            return shader.name == "Standard" || shader.name.StartsWith("Universal Render Pipeline/");
        }

        private static bool AssignTexture(Material mat, string property, string fileName)
        {
            if (!mat.HasProperty(property))
                return false;

            var tex = AssetDatabase.LoadAssetAtPath<Texture>($"{SourceDir}/{fileName}");
            if (tex == null)
            {
                Debug.LogWarning($"[PhantomRealFormBuilder] Texture '{fileName}' not found next to the FBX.");
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
