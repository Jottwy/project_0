#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// ADR-094 — same approach as <see cref="FacelingAdultAvatarBuilder"/> (see that class for the
    /// full rationale): clones the baked <c>RemotePlayerAvatar</c> and swaps its default body for
    /// the child faceling's own, instead of re-deriving the 20-hook vendor rig a third time.
    /// Always-active body, reveal hook disabled — a faceling never disguises.
    /// </summary>
    public static class FacelingChildAvatarBuilder
    {
        private const string BasePrefabPath =
            "Assets/_Migration/STPIntegration/Resources/RemotePlayerAvatar.prefab";
        private const string OutputDir = "Assets/_Migration/STPIntegration/Resources/Facelings";
        public const string OutputPath = OutputDir + "/FacelingChildAvatar.prefab";
        private const string BodyChildName = "FacelingBody";

        [MenuItem("Backrooms/Facelings/Build Child Avatar Prefab")]
        public static void Build()
        {
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath);
            if (basePrefab == null)
            {
                Debug.LogError($"[FacelingChildAvatarBuilder] Base prefab not found: '{BasePrefabPath}'. " +
                    "Run 'Backrooms ▸ Build Remote Avatar Prefab' first.");
                return;
            }

            var bodyPrefab = FacelingChildRealFormBuilder.BuildOrGet();
            if (bodyPrefab == null)
            {
                Debug.LogError("[FacelingChildAvatarBuilder] No faceling body built — " +
                    "see FacelingChildRealFormBuilder's own errors above.");
                return;
            }

            if (!Directory.Exists(OutputDir))
            {
                Directory.CreateDirectory(OutputDir);
                AssetDatabase.Refresh();
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                HideDefaultBody(instance);
                NestFacelingBody(instance, bodyPrefab);
                RetargetLocomotionFeeders(instance);
                DisableRevealHook(instance);
                WireVocalHook(instance);
                TightenFootsteps(instance);

                PrefabUtility.SaveAsPrefabAsset(instance, OutputPath, out bool ok);
                if (ok)
                {
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[FacelingChildAvatarBuilder] Saved '{OutputPath}'. " +
                        "RemoteAvatarProvider will Resources.Load it at runtime for species==2 peers.");
                }
                else
                {
                    Debug.LogError("[FacelingChildAvatarBuilder] SaveAsPrefabAsset failed.");
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static void HideDefaultBody(GameObject root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.gameObject.SetActive(false);
        }

        private static void NestFacelingBody(GameObject root, GameObject bodyPrefab)
        {
            var existing = root.transform.Find(BodyChildName);
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var body = (GameObject)PrefabUtility.InstantiatePrefab(bodyPrefab, root.transform);
            body.name = BodyChildName;
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.SetActive(true);

            var bodyAnimator = body.GetComponentInChildren<Animator>(true);
            if (bodyAnimator != null)
            {
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    ProxyAnimatorControllerBuilder.OutputPath);
                if (controller != null)
                {
                    bodyAnimator.runtimeAnimatorController = controller;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(bodyAnimator);
                }
                else
                {
                    Debug.LogError("[FacelingChildAvatarBuilder] No proxy controller found — " +
                        "the faceling WILL T-pose.");
                }
            }
        }

        private static void DisableRevealHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyRevealHook>();
            if (hook != null)
                hook.enabled = false;
        }

        // Same bug and same fix as FacelingAdultAvatarBuilder's own RetargetLocomotionFeeders —
        // see that method's comment for the full "misdirected, not missing" explanation.
        private static void RetargetLocomotionFeeders(GameObject root)
        {
            var bodyTransform = root.transform.Find(BodyChildName);
            var bodyAnimator = bodyTransform != null ? bodyTransform.GetComponentInChildren<Animator>(true) : null;
            if (bodyAnimator == null)
            {
                Debug.LogError("[FacelingChildAvatarBuilder] No Animator found on the nested body — " +
                    "locomotion feeders left pointing at the old (disabled) body; the faceling WILL " +
                    "look frozen.");
                return;
            }

            var locomotion = root.GetComponent<ProxyLocomotionFeeder>();
            if (locomotion != null)
            {
                var so = new SerializedObject(locomotion);
                var prop = so.FindProperty("_animator");
                if (prop != null)
                    prop.objectReferenceValue = bodyAnimator;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var jump = root.GetComponent<ProxyJumpFeeder>();
            if (jump != null)
            {
                var so = new SerializedObject(jump);
                var prop = so.FindProperty("_animator");
                if (prop != null)
                    prop.objectReferenceValue = bodyAnimator;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        /// <summary>
        /// ADR-094 point 3/6 — the child's OWN voice, own kind space (0=Giggle, 1=Scream, 2=Call;
        /// matches `FACELING_CHILD_VOCAL_*` in the backend's faceling.rs), read off the SAME
        /// generic `vocalSeq`/`vocalKind` wire fields as the robapieles by this proxy's OWN
        /// `ProxyVocalHook` instance and bank array — no collision, since kind is only ever
        /// interpreted against whichever hook instance the entity's own prefab carries.
        /// Bank ships UNAUTHORED (empty `AudioClip[]`) until Joel drops files matching the
        /// prefixes below into <see cref="AudioDir"/> — an empty bank is silent, not an error.
        /// </summary>
        private const string AudioDir = "Assets/_Migration/STPIntegration/Facelings/Audio";

        private static void WireVocalHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyVocalHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyVocalHook>();

            var so = new SerializedObject(hook);
            var voices = so.FindProperty("_voices");
            if (voices == null)
                return;

            if (voices.arraySize < 5)
                voices.arraySize = 5;

            var banks = new[]
            {
                "FacelingChild_Giggle",  // 0 — ambient telemetry giggle, PackRoam/PackStalk
                "FacelingChild_Scream",  // 1 — cerco opening, a death, and the screamer
                "FacelingChild_Call",    // 2 — the lone survivor's regroup cry
                // Enmienda 3: takes over from the giggle once the ring is shut. Its own bank
                // rather than a quieter giggle — the point is that the sound CHANGES when they
                // reach you, not just that it gets nearer.
                "FacelingChild_Whisper", // 3 — close-quarters whisper/chant
                // Enmienda 9: the distant chant. One voice at a time, every ~14 s, carrying much
                // further than anything else the child has.
                "FacelingChild_Chant",   // 4 — the far band
            };

            // ADR-094 Enmienda 9 — THE RANGES, per bank. This is the half of the fix that lives
            // on the client: the backend decides WHICH sound and how often, and these decide how
            // far each one reaches. Without them all five play on one curve, and a whisper you
            // can hear from forty metres is not a whisper.
            //
            //                          min    max   vol   pitch
            var ranges = new[,]
            {
                { 4f,  34f, 1.0f, 1.0f },   // 0 Giggle  — near, ordinary
                { 8f,  60f, 1.0f, 1.0f },   // 1 Scream  — an event; it should carry
                { 10f, 70f, 1.0f, 0.95f },  // 2 Call    — a cry FOR other packs, so widest but one
                { 1.5f, 11f, 1.0f, 1.0f },  // 3 Whisper — next to your ear or nowhere
                // The chant reaches nearly as far as the robapieles' answer roar and sits lower:
                // low frequencies are what actually survive distance, and the drop also stops it
                // reading as the same child that giggles at you from six metres.
                { 14f, 80f, 1.15f, 0.88f }, // 4 Chant
            };

            if (!Directory.Exists(AudioDir))
            {
                Directory.CreateDirectory(AudioDir);
                AssetDatabase.Refresh();
            }

            for (int bank = 0; bank < banks.Length && bank < voices.arraySize; bank++)
            {
                var element = voices.GetArrayElementAtIndex(bank);
                var found = LoadVoiceClips(banks[bank]);

                SetBankFloat(element, "MinDistance", ranges[bank, 0]);
                SetBankFloat(element, "MaxDistance", ranges[bank, 1]);
                SetBankFloat(element, "Volume", ranges[bank, 2]);
                SetBankFloat(element, "Pitch", ranges[bank, 3]);
                var over = element.FindPropertyRelative("OverrideRange");
                if (over != null)
                    over.boolValue = true;

                var clips = element.FindPropertyRelative("Clips");
                if (clips == null)
                    continue;

                clips.arraySize = found.Length;
                for (int i = 0; i < found.Length; i++)
                    clips.GetArrayElementAtIndex(i).objectReferenceValue = found[i];

                if (found.Length == 0)
                    Debug.LogWarning($"[FacelingChildAvatarBuilder] Voice bank {bank} " +
                        $"('{banks[bank]}*') has no clips in {AudioDir} — that voice is silent.");
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Writes one float on a serialized <c>VoiceBank</c> element, if it has it.</summary>
        private static void SetBankFloat(SerializedProperty element, string name, float value)
        {
            var prop = element.FindPropertyRelative(name);
            if (prop != null)
                prop.floatValue = value;
        }

        /// <summary>
        /// ADR-094 punto 6 — "footsteps ligeros y rápidos". The clips themselves stay STP's
        /// surface-driven ones (that path already works and is the reason a child audibly walks at
        /// all); what makes it read as a CHILD is the CADENCE. Stride is the distance between
        /// footfalls, so shortening it makes the same speed produce more, closer-together steps —
        /// which is exactly the difference between an adult's walk and a kid's scurry.
        ///
        /// Range is tightened too. A child is small and light: hearing one from as far as you hear
        /// a grown peer would give away a pack that is supposed to arrive before you notice it.
        /// </summary>
        private static void TightenFootsteps(GameObject root)
        {
            var hook = root.GetComponent<ProxyFootstepHook>();
            if (hook == null)
                return;

            var so = new SerializedObject(hook);
            SetHookFloat(so, "_walkStride", 0.48f); // vs the adult prefab's 0.85
            SetHookFloat(so, "_runStride", 0.72f);  // vs 1.35
            SetHookFloat(so, "_maxDistance", 15f);  // vs 22
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetHookFloat(SerializedObject so, string field, float value)
        {
            var prop = so.FindProperty(field);
            if (prop != null)
                prop.floatValue = value;
        }

        private static AudioClip[] LoadVoiceClips(string prefix)
        {
            var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { AudioDir });
            var paths = new System.Collections.Generic.List<string>(guids.Length);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path).StartsWith(prefix, System.StringComparison.Ordinal))
                    paths.Add(path);
            }
            paths.Sort(System.StringComparer.Ordinal);

            var clips = new System.Collections.Generic.List<AudioClip>(paths.Count);
            foreach (var path in paths)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                    clips.Add(clip);
            }
            return clips.ToArray();
        }
    }
}
#endif
