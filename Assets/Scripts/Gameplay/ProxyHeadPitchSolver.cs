using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Cabeceo ABSOLUTO de la cabeza de un proxy: la cara acaba mirando al ángulo de la cámara del peer
    /// sea cual sea la postura que haya dejado el clip. Aritmética pura sobre Transforms, sin Animator ni
    /// MonoBehaviour, para que los tests EditMode la vean (el hook vive en Assembly-CSharp; mismo motivo
    /// que <see cref="ProxyLocomotionMath"/>).
    ///
    /// POR QUÉ ABSOLUTO Y NO SUMADO (medido el 2026-09-14 con el arnés de poses): el clip Mixamo de
    /// agachado deja la cabeza 70° hacia el suelo (pecho a 77°). El cabeceo se SUMABA encima, así que un
    /// peer agachado mirando al frente se veía mirando al suelo, y para verle la cara recta tenía que
    /// mirar 60° hacia arriba. Midiendo dónde mira la cabeza DESPUÉS del Animator y corrigiendo sólo la
    /// diferencia, la postura del clip deja de importar.
    ///
    /// REPARTO: una persona no mira arriba doblando sólo el cuello. Con todo el giro en cuello y cabeza,
    /// a ±60° el cuello se doblaba como una bisagra y la piel se estiraba (misma medición). El pecho se
    /// lleva una parte, con tope, y el resto se reparte entre cuello y cabeza.
    /// </summary>
    public static class ProxyHeadPitchSolver
    {
        /// <summary>
        /// Parte de la corrección que se lleva el pecho. Era 0,25: con la cabeza al 80 % el cuello seguía
        /// doblado a −26° mirando 60° arriba y a 45° mirando 89° abajo (arnés, 2026-09-14). Joel pidió
        /// que el pecho acompañe más.
        /// </summary>
        public const float ChestShare = 0.40f;

        /// <summary>Tope del pecho: pasado esto una espalda ya no se dobla más por mirar.</summary>
        public const float ChestCapDegrees = 35f;

        /// <summary>Parte de lo que queda tras el pecho que se lleva el cuello; el resto, la cabeza.</summary>
        public const float NeckShare = 0.45f;

        /// <summary>
        /// Fracción del cabeceo de la cámara que sigue la CABEZA; el resto lo ponen los ojos. Con la
        /// cabeza al 100 % el cuello quedaba doblado del todo a ±60° y con un pliegue a 89° (arnés,
        /// 2026-09-14). Propuesta de Joel: 80 %.
        /// </summary>
        public const float HeadFollowsPitch = 0.8f;

        /// <summary>Ángulo al que se apunta la cabeza para un cabeceo de cámara dado.</summary>
        public static float HeadTarget(float cameraPitchDegrees) => cameraPitchDegrees * HeadFollowsPitch;

        public struct Split
        {
            public float Chest;
            public float Neck;
            public float Head;
        }

        /// <summary>
        /// Eje de «mirar al frente» del hueso en SU espacio local, sacado con el cuerpo en la pose por
        /// defecto (de pie, mirando al frente del raíz). Así la medida no depende de cómo se autoró el
        /// eje del hueso en cada rig.
        /// </summary>
        public static Vector3 LocalLookAxis(Quaternion boneRotation, Vector3 rootForward) =>
            Quaternion.Inverse(boneRotation) * rootForward;

        /// <summary>
        /// Hacia dónde mira el hueso en el plano sagital del raíz, en grados: positivo = hacia abajo,
        /// la misma convención que el cabeceo de la cámara del vendor (límites −60 arriba, +90 abajo).
        /// </summary>
        public static float MeasurePitch(Quaternion boneRotation, Vector3 localLook, Vector3 rootForward, Vector3 rootRight)
        {
            Vector3 onPlane = Vector3.ProjectOnPlane(boneRotation * localLook, rootRight);
            if (onPlane.sqrMagnitude < 1e-8f)
                return 0f;
            return Vector3.SignedAngle(rootForward, onPlane, rootRight);
        }

        /// <summary>Reparte una corrección entre pecho, cuello y cabeza. Las tres partes suman el total.</summary>
        public static Split Distribute(float errorDegrees)
        {
            float chest = Mathf.Clamp(errorDegrees * ChestShare, -ChestCapDegrees, ChestCapDegrees);
            float rest = errorDegrees - chest;
            float neck = rest * NeckShare;
            return new Split { Chest = chest, Neck = neck, Head = rest - neck };
        }

        /// <summary>
        /// Deja la cabeza mirando a <paramref name="targetDegrees"/> respecto al frente del raíz. Hay que
        /// llamarlo con el cuerpo YA posado (LateUpdate). Pecho y cuello pueden ser null.
        ///
        /// Los giros son en espacio de mundo sobre el eje derecho del raíz, de la raíz a la hoja. Si el
        /// clip trae la cabeza girada o alabeada, rotar sobre ese eje no deja el cabeceo exacto a la
        /// primera; la última línea mide otra vez y cierra el resto sólo con la cabeza.
        /// </summary>
        public static void AimHead(Transform root, Transform chest, Transform neck, Transform head,
            Vector3 headLocalLook, float targetDegrees)
        {
            if (root == null || head == null)
                return;

            Vector3 forward = root.forward;
            Vector3 right = root.right;

            float error = targetDegrees - MeasurePitch(head.rotation, headLocalLook, forward, right);
            Split split = Distribute(error);
            if (chest == null)
            {
                split.Head += split.Chest;
                split.Chest = 0f;
            }
            if (neck == null)
            {
                split.Head += split.Neck;
                split.Neck = 0f;
            }

            Bend(chest, split.Chest, right);
            Bend(neck, split.Neck, right);
            Bend(head, split.Head, right);

            float residual = targetDegrees - MeasurePitch(head.rotation, headLocalLook, forward, right);
            Bend(head, residual, right);
        }

        private static void Bend(Transform bone, float degrees, Vector3 axis)
        {
            if (bone == null || Mathf.Approximately(degrees, 0f))
                return;
            bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }
    }
}
