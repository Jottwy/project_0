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

            ("Left Upper Leg Front-Back", -0.75f),
            ("Right Upper Leg Front-Back", -0.75f),
            ("Left Upper Leg In-Out", 0.18f),
            ("Right Upper Leg In-Out", 0.18f),
            ("Left Lower Leg Stretch", -0.45f),
            ("Right Lower Leg Stretch", -0.45f),
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
                float ankleBind = AnkleAboveRoot(animator, instance.transform);
                clip.SampleAnimation(instance, 0f);
                float seated = HipHeight(animator);
                float ankleSeated = AnkleAboveRoot(animator, instance.transform);

                if (Mathf.Abs(bind - seated) < 0.02f)
                {
                    Debug.LogError("[FacelingSeatedPoseBuilder] LA POSE NO HIZO NADA: la cadera sigue " +
                        $"a {seated:0.000} m sobre los pies. O los nombres de músculo no son los de " +
                        "este rig, o el Animator no es Humanoid.");
                    return;
                }

                // **LA POSE SE MIDE POR EL MUSLO Y LA ESPINILLA, no por una sola altura.** Con la
                // cadera clavada (un clip de músculos no traslada la raíz), «la cadera bajó mucho»
                // puede significar tanto «se sentó» como «encogió las piernas hasta el pecho», y las
                // dos son la misma cifra. El muslo tiene que salir HORIZONTAL hacia delante y la
                // espinilla caer casi a plomo: eso, y sólo eso, es estar sentado.
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var knee = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                var foot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                if (hips == null || knee == null || foot == null)
                {
                    Debug.LogWarning("[FacelingSeatedPoseBuilder] Sin cadera, rodilla o pie: la pose " +
                        $"queda medida sólo por altura ({seated:0.000} m sobre los pies).");
                    return;
                }

                Vector3 down = -instance.transform.up;
                float thigh = Vector3.Angle(knee.position - hips.position, down);
                float shin = Vector3.Angle(foot.position - knee.position, down);
                float forward = Vector3.Dot(foot.position - hips.position, instance.transform.forward);

                // **CUÁNTO SE DESPLAZA EL CUERPO AL SENTARSE, para el que lea el log.**
                //
                // No es una constante que nadie escriba: `ProxySeatedHook.PlantFeet` la vuelve a
                // medir en juego, sobre el rig que de verdad se dibuja. Se imprime aquí porque el
                // signo desconcierta —un clip Humanoid lleva su propia posición de cuerpo y ésta
                // deja los tobillos POR DEBAJO del suelo, así que sentarse es SUBIR— y porque un
                // salto grande entre horneados es la primera señal de que la pose ha cambiado de
                // sitio. Con el rig del vendor sale ~0,67 m; el cuerpo del faceling, que es otro
                // esqueleto, pide otro número, y por eso el hook no se fía de éste.
                float suggestedLift = ankleBind - ankleSeated;

                string verdict =
                    thigh > 60f && thigh < 115f && shin < 35f && forward > 0.10f ? "OK" : "FUERA DE BANDA";
                string line = $"[FacelingSeatedPoseBuilder] Pose medida ({verdict}): muslo {thigh:0.} ° " +
                    $"de la vertical (banda 60-115), espinilla {shin:0.} ° (banda < 35), pie " +
                    $"{forward:0.00} m por delante de la cadera (banda > 0,10), cadera a " +
                    $"{seated:0.000} m sobre los pies (de pie: {bind:0.000}). " +
                    $"el cuerpo del vendor subiría {suggestedLift:0.00} m (tobillo sobre el raíz: " +
                    $"de pie {ankleBind:0.000}, sentado {ankleSeated:0.000}); con esa altura la cadera " +
                    $"queda a {hips.position.y - instance.transform.position.y + suggestedLift:0.000} m del suelo.";
                if (verdict == "OK")
                    Debug.Log(line);
                else
                    Debug.LogError(line + " Ajusta la tabla SeatedPose.");
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

        /// <summary>Altura del tobillo más bajo sobre el raíz del avatar, en metros.</summary>
        private static float AnkleAboveRoot(Animator animator, Transform root)
        {
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (left == null && right == null)
                return 0f;
            float y = Mathf.Min(
                left != null ? left.position.y : float.MaxValue,
                right != null ? right.position.y : float.MaxValue);
            return y - root.position.y;
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
