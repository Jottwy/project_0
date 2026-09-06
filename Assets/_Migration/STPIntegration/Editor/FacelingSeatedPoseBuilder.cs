#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// ADR-131 D5 — hornea la POSE SENTADA del vigilante como un <see cref="AnimationClip"/>
    /// Humanoid de UN SOLO FOTOGRAMA, con curvas de MÚSCULOS.
    ///
    /// **Por qué horneado y no un clip externo** (Joel: «no creo que deba ser para tanto»): una pose
    /// estática no tiene nada que animar. Un `.fbx` traería su rig, su `Avatar`, su retargeting y una
    /// dependencia binaria que nadie puede leer en un diff, a cambio de cero movimiento. Lo que hay
    /// aquí son cuarenta números en una tabla, y esos sí se leen y se revisan.
    ///
    /// **Por qué músculos y no curvas de transform**: el rig del proxy es HUMANOID
    /// (<see cref="ProxyAnimatorControllerBuilder"/> lo documenta: MaleSurvivor importa como
    /// Human/CreateFromThisModel), así que un clip de músculos retargetea solo al cuerpo del faceling
    /// igual que ya lo hacen el idle y la caminata. Curvas de transform atarían la pose a los nombres
    /// de hueso de UN esqueleto y se romperían en silencio con el siguiente cuerpo.
    ///
    /// El clip se aplica en juego con un `AnimatorOverrideController` que sustituye el clip de IDLE
    /// (ver <c>ProxySeatedHook</c>): sin estado nuevo en el controller, sin transición que sincronizar
    /// y sin re-hornear el controller — un vigilante nunca sale de idle porque nunca se mueve.
    /// </summary>
    public static class FacelingSeatedPoseBuilder
    {
        public const string OutputPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/Animations/FacelingSeated.anim";

        private const string AdultAvatarPath = FacelingAdultAvatarBuilder.OutputPath;

        /// <summary>
        /// LA POSE, en músculos normalizados [-1, 1]. Un nombre que este rig no tenga se ignora con
        /// un aviso en vez de romper el horneado: los nombres salen de <c>HumanTrait.MuscleName</c>,
        /// que es la única lista autoritativa, y esta tabla es una selección de ella.
        ///
        /// Las caderas y las rodillas son la pose; lo demás es lo que evita que se lea como un
        /// maniquí doblado: el tronco cae un pelo hacia delante, las rodillas se abren un poco y los
        /// antebrazos se recogen hacia el regazo.
        /// </summary>
        private static readonly (string Muscle, float Value)[] SeatedPose =
        {
            ("Spine Front-Back", 0.10f),
            ("Chest Front-Back", 0.05f),

            ("Left Upper Leg Front-Back", 0.75f),
            ("Right Upper Leg Front-Back", 0.75f),
            ("Left Upper Leg In-Out", 0.18f),
            ("Right Upper Leg In-Out", 0.18f),
            ("Left Lower Leg Stretch", -0.75f),
            ("Right Lower Leg Stretch", -0.75f),
            ("Left Foot Up-Down", 0.15f),
            ("Right Foot Up-Down", 0.15f),

            ("Left Arm Down-Up", -0.20f),
            ("Right Arm Down-Up", -0.20f),
            ("Left Arm Front-Back", 0.10f),
            ("Right Arm Front-Back", 0.10f),
            ("Left Forearm Stretch", 0.35f),
            ("Right Forearm Stretch", 0.35f),
        };

        [MenuItem("Backrooms/Facelings/Build Seated Pose Clip")]
        public static void Build()
        {
            var known = new HashSet<string>(HumanTrait.MuscleName);
            var clip = new AnimationClip { name = "FacelingSeated" };

            int written = 0;
            foreach (var (muscle, value) in SeatedPose)
            {
                if (!known.Contains(muscle))
                {
                    Debug.LogWarning($"[FacelingSeatedPoseBuilder] '{muscle}' no es un músculo de " +
                        "este rig — se ignora. Compara la tabla con HumanTrait.MuscleName.");
                    continue;
                }

                // UN FOTOGRAMA: dos claves con el MISMO valor, en 0 y en 1/30. Una sola clave
                // también define una constante, pero un clip de longitud cero desconcierta al
                // inspector y a cualquier herramienta que divida por su duración.
                var curve = new AnimationCurve(
                    new Keyframe(0f, value),
                    new Keyframe(1f / 30f, value));
                var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), muscle);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                written++;
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            // Sin `loopBlend`: no hay nada que mezclar entre el final y el principio de una constante.
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(OutputPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(clip, existing);
                Object.DestroyImmediate(clip);
                clip = existing;
                EditorUtility.SetDirty(clip);
            }
            else
            {
                AssetDatabase.CreateAsset(clip, OutputPath);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[FacelingSeatedPoseBuilder] '{OutputPath}' horneado: {written} curvas de músculo.");
            Verify(clip);
        }

        /// <summary>
        /// **UN CLIP QUE NO MUEVE NADA SE VE IGUAL QUE UNO QUE NO EXISTE**, así que el horneado se
        /// mide en vez de darse por bueno: se instancia el avatar del faceling, se muestrea el clip y
        /// se compara la altura de la CADERA sobre los pies contra la pose de bind. Sentado, la
        /// cadera baja; si el muestreo no la mueve, el binding de músculos no llegó al Animator y la
        /// pose sería un T-pose disfrazado de éxito.
        /// </summary>
        private static void Verify(AnimationClip clip)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AdultAvatarPath);
            if (prefab == null)
            {
                Debug.LogWarning("[FacelingSeatedPoseBuilder] No hay avatar de faceling horneado " +
                    $"('{AdultAvatarPath}'): la pose queda SIN medir. Ejecuta " +
                    "'Backrooms ▸ Facelings ▸ Build Adult Avatar Prefab' y vuelve a hornear.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var animator = FindHumanoidAnimator(instance);
                if (animator == null)
                {
                    Debug.LogError("[FacelingSeatedPoseBuilder] El avatar no tiene ningún Animator " +
                        "Humanoid: el clip de músculos no se le puede aplicar.");
                    return;
                }

                float bind = HipHeight(animator);
                clip.SampleAnimation(instance, 0f);
                float seated = HipHeight(animator);
                float drop = bind - seated;

                if (drop < 0.15f)
                {
                    Debug.LogError("[FacelingSeatedPoseBuilder] LA POSE NO HIZO NADA: la cadera bajó " +
                        $"{drop:0.000} m (de {bind:0.000} a {seated:0.000}). O los nombres de músculo " +
                        "no son los de este rig, o el Animator no es Humanoid.");
                }
                else
                {
                    Debug.Log($"[FacelingSeatedPoseBuilder] Pose medida: la cadera baja {drop:0.000} m " +
                        $"(de {bind:0.000} a {seated:0.000}). SeatDropM del hook debe casar con esto.");
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static Animator FindHumanoidAnimator(GameObject root)
        {
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a.isHuman)
                    return a;
            }
            return null;
        }

        /// <summary>Altura de la cadera sobre el pie más bajo, en metros de mundo.</summary>
        private static float HipHeight(Animator animator)
        {
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (hips == null)
                return 0f;
            float floor = Mathf.Min(
                left != null ? left.position.y : hips.position.y,
                right != null ? right.position.y : hips.position.y);
            return hips.position.y - floor;
        }
    }
}
#endif
