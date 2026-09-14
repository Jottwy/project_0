using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Naturalidad de un brazo en TERCERA persona: la misma medida y los mismos rangos cómodos que ya validó la
    /// herramienta de agarres de primera persona (<c>HandGripSolver.Measure</c>, ADR-150; docs/systems/hand-interaction.md
    /// «Naturalidad: qué se puntúa»), portados a runtime para el avatar remoto. Aritmética pura sobre posiciones.
    ///
    /// LO QUE SE HEREDA TAL CUAL, porque costó capturas en primera persona:
    ///  - la muñeca se mide con VECTORES del cuerpo (antebrazo codo→muñeca contra metacarpo muñeca→nudillo del
    ///    corazón), no con los ejes del hueso: la descomposición torsión-columpio daba 95° de extensión a un
    ///    agarre bueno;
    ///  - el doblez TOTAL se reparte por su dirección (flexión hacia la palma, cubital hacia el meñique); dos atan2
    ///    contra el antebrazo se disparaban pasados 90°;
    ///  - la prono-supinación se mide contra el CUERPO (0 = mano de apretón, palma a la línea media; + palma arriba);
    ///  - codo entre 70° y 160°, por debajo de la muñeca y del hombro.
    /// Motivo del port (Joel, 2026-09-14): la linterna del vecino llevaba «la muñeca medio torcida» porque la mano se
    /// orientaba a la fuerza después del IK y la muñeca se comía lo que no cuadraba.
    /// </summary>
    public static class ProxyArmNaturalness
    {
        // Rangos CÓMODOS (grados), los de primera persona.
        public const float ExtensionComfort = 35f, FlexionComfort = 15f, UlnarComfort = 25f, RadialComfort = 8f;
        public const float WristHardLimit = 70f, DeviationHardLimit = 35f;
        public const float SupinationComfort = 30f, PronationComfort = 70f;
        public const float ElbowClosed = 70f, ElbowLocked = 160f;

        public struct Measure
        {
            /// <summary>+ hacia la palma (flexión), − extensión.</summary>
            public float Flexion;
            /// <summary>+ hacia el meñique (cubital), − radial.</summary>
            public float Ulnar;
            /// <summary>0 = apretón, + palma arriba (supinación), − palma abajo (pronación).</summary>
            public float ForearmRotation;
            public float ElbowAngle;
            public float ElbowOverWrist;
            public float ElbowOverShoulder;
        }

        /// <summary>
        /// Mide un brazo. <paramref name="palmNormal"/> hacia donde cierran los dedos; <paramref name="knuckleAxis"/>
        /// del meñique al índice (el de <see cref="ProxyGripSolver.HandFrame"/>); <paramref name="rootRight"/> y
        /// <paramref name="rootUp"/> del cuerpo; <paramref name="right"/> si es el brazo derecho.
        /// </summary>
        public static Measure Take(Vector3 shoulder, Vector3 elbow, Vector3 wrist, Vector3 middleKnuckle,
            Vector3 palmNormal, Vector3 knuckleAxis, Vector3 rootRight, Vector3 rootUp, bool right)
        {
            var m = new Measure();
            Vector3 fore = (wrist - elbow).normalized;
            Vector3 meta = (middleKnuckle - wrist).normalized;
            Vector3 palmDir = Vector3.ProjectOnPlane(palmNormal, fore).normalized;
            Vector3 pinkyDir = Vector3.ProjectOnPlane(-knuckleAxis, fore);
            pinkyDir = Vector3.ProjectOnPlane(pinkyDir, palmDir).normalized;

            float bend = Vector3.Angle(fore, meta);
            Vector3 across = Vector3.ProjectOnPlane(meta, fore);
            float direction = across.sqrMagnitude > 1e-8f
                ? Mathf.Atan2(Vector3.Dot(across, pinkyDir), Vector3.Dot(across, palmDir))
                : 0f;
            m.Flexion = bend * Mathf.Cos(direction);
            m.Ulnar = bend * Mathf.Sin(direction);

            Vector3 medial = Vector3.ProjectOnPlane(right ? -rootRight : rootRight, fore).normalized;
            Vector3 upRef = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(rootUp, fore), medial).normalized;
            m.ForearmRotation = Mathf.Atan2(Vector3.Dot(palmDir, upRef), Vector3.Dot(palmDir, medial)) * Mathf.Rad2Deg;

            m.ElbowAngle = Vector3.Angle(shoulder - elbow, wrist - elbow);
            m.ElbowOverWrist = Vector3.Dot(elbow - wrist, rootUp);
            m.ElbowOverShoulder = Vector3.Dot(elbow - shoulder, rootUp);
            return m;
        }

        /// <summary>Coste de naturalidad: 0 dentro de los rangos cómodos; los términos y escalas de primera persona.</summary>
        public static float Cost(Measure m)
        {
            float c = 0f;
            c += Sq(Mathf.Max(0f, m.ForearmRotation - SupinationComfort) / 15f);
            c += Sq(Mathf.Max(0f, -m.ForearmRotation - PronationComfort) / 15f);
            if (Mathf.Abs(m.ForearmRotation) > 110f) c += 25f;
            c += Sq(Mathf.Max(0f, -m.Flexion - ExtensionComfort) / 12f);
            c += Sq(Mathf.Max(0f, m.Flexion - FlexionComfort) / 12f);
            c += Sq(Mathf.Max(0f, m.Ulnar - UlnarComfort) / 10f);
            c += Sq(Mathf.Max(0f, -m.Ulnar - RadialComfort) / 10f);
            if (Mathf.Abs(m.Flexion) > WristHardLimit || Mathf.Abs(m.Ulnar) > DeviationHardLimit) c += 25f;
            c += Sq(Mathf.Max(0f, ElbowClosed - m.ElbowAngle) / 20f);
            c += Sq(Mathf.Max(0f, m.ElbowAngle - ElbowLocked) / 10f);
            // «codo-alto» (codo por encima de la muñeca) NO se hereda: en primera persona castiga un codo levantado
            // con la mano delante de la cara, pero en tercera una mano baja lleva el codo por encima de la muñeca por
            // pura anatomía. Medido: el brazo izquierdo colgando del clip de idle del vendor costaba 21,7 sólo por ese
            // término, y la búsqueda cambiaba muñeca por codo. Se queda el codo por encima del HOMBRO.
            c += Sq(Mathf.Max(0f, m.ElbowOverShoulder) / 0.05f);
            return c;
        }

        /// <summary>
        /// Torsión de la mano sobre el antebrazo que se pasa al propio antebrazo. El esqueleto de tercera NO tiene
        /// huesos de torsión (MaleSurvivor: UpperArm, LowerArm, Hand), así que toda la prono-supinación en la mano
        /// retorcería la piel de la muñeca; girando el antebrazo sobre su eje la mano no se mueve de sitio.
        /// </summary>
        public const float ForearmTakesTwist = 0.5f;

        /// <summary>
        /// Gira el antebrazo sobre su propio eje la parte <see cref="ForearmTakesTwist"/> de la torsión que hay entre
        /// él y la mano, conservando la rotación de la mano en mundo. La torsión se mide como el ángulo, alrededor del
        /// eje del antebrazo, entre la normal de la palma y la referencia que la mano tendría sin torsión.
        /// </summary>
        public static void ShareForearmTwist(Transform lowerArm, Transform hand, Vector3 palmNormal, Vector3 palmNormalUntwisted)
        {
            if (lowerArm == null || hand == null)
                return;
            Vector3 axis = (hand.position - lowerArm.position).normalized;
            Vector3 a = Vector3.ProjectOnPlane(palmNormalUntwisted, axis);
            Vector3 b = Vector3.ProjectOnPlane(palmNormal, axis);
            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f)
                return;
            float twist = Vector3.SignedAngle(a, b, axis) * ForearmTakesTwist;
            Quaternion handWorld = hand.rotation;
            lowerArm.rotation = Quaternion.AngleAxis(twist, axis) * lowerArm.rotation;
            hand.rotation = handWorld;
        }

        private static float Sq(float x) => x * x;
    }
}
