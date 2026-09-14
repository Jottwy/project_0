using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Agarre de un objeto en la mano de un proxy, MEDIDO sobre la mano y no escrito a mano. Aritmética
    /// pura sobre posiciones y Transforms, sin Animator ni MonoBehaviour, para que la vean los tests
    /// EditMode (el hook vive en Assembly-CSharp; mismo motivo que <see cref="ProxyLocomotionMath"/>).
    ///
    /// POR QUÉ (arnés de poses, 2026-09-14): con los valores de <c>GripPoseSet</c> a cero la linterna del
    /// vecino quedaba metida a lo largo del antebrazo y la mano colgaba con los dedos abiertos. Lo aprendido
    /// en los agarres de primera persona (memoria tool-grip-baking-traps) se aplica tal cual:
    ///  1. el objeto va a (radio + piel) de la LÍNEA DE NUDILLOS, hacia donde cierran los dedos — ni en el
    ///     origen del hueso ni en el centro de la palma;
    ///  2. el signo del cierre de los dedos NO se supone: se prueba hacia los dos lados y se queda el que
    ///     acerca la yema al objeto;
    ///  3. se cierra hasta TOCAR, no un ángulo fijo.
    /// </summary>
    public static class ProxyGripSolver
    {
        /// <summary>Piel entre el objeto y la línea de nudillos.</summary>
        public const float SkinMetres = 0.012f;

        /// <summary>Paso del barrido de cierre de los dedos.</summary>
        public const float CurlStepDegrees = 3f;

        /// <summary>Tope del cierre: pasado esto un dedo ya está hecho un puño.</summary>
        public const float MaxCurlDegrees = 100f;

        /// <summary>La yema no es un hueso: se estima prolongando la última falange esta fracción de su largo.</summary>
        public const float TipExtension = 0.8f;

        public struct HandFrame
        {
            /// <summary>Punto medio entre los nudillos del índice y del meñique.</summary>
            public Vector3 KnuckleCentre;
            /// <summary>Del meñique al índice, unitario.</summary>
            public Vector3 KnuckleAxis;
            /// <summary>De la muñeca hacia los nudillos, perpendicular a <see cref="KnuckleAxis"/>.</summary>
            public Vector3 FingerAxis;
            /// <summary>Hacia fuera de la PALMA, que es hacia donde cierran los dedos.</summary>
            public Vector3 PalmNormal;
            /// <summary>Distancia entre los nudillos del índice y del meñique.</summary>
            public float Width;
        }

        /// <summary>
        /// Marco de la mano a partir de cinco puntos en mundo. El lado de la palma lo decide la base del
        /// pulgar, que en cualquier mano humana queda por delante del plano de los metacarpianos: así el
        /// signo no depende de la lateralidad ni de cómo se autoró el rig.
        /// </summary>
        public static HandFrame Frame(Vector3 wrist, Vector3 indexKnuckle, Vector3 middleKnuckle,
            Vector3 pinkyKnuckle, Vector3 thumbBase)
        {
            Vector3 across = indexKnuckle - pinkyKnuckle;
            float width = across.magnitude;
            across = width > 1e-6f ? across / width : Vector3.right;

            Vector3 finger = Vector3.ProjectOnPlane(middleKnuckle - wrist, across);
            finger = finger.sqrMagnitude > 1e-10f ? finger.normalized : Vector3.forward;

            Vector3 palm = Vector3.Cross(finger, across).normalized;
            Vector3 centre = (indexKnuckle + pinkyKnuckle) * 0.5f;
            if (Vector3.Dot(thumbBase - centre, palm) < 0f)
                palm = -palm;

            return new HandFrame
            {
                KnuckleCentre = centre,
                KnuckleAxis = across,
                FingerAxis = finger,
                PalmNormal = palm,
                Width = width,
            };
        }

        /// <summary>
        /// Pose en mundo de un objeto CILÍNDRICO cuyo eje es su +Y local (su extremo útil, la lente de una
        /// linterna, hacia +Y): el eje va paralelo a la línea de nudillos con el +Y del lado del índice, el
        /// punto <paramref name="gripAlongY"/> del eje queda frente al centro de los nudillos a (radio + piel)
        /// hacia la palma, y el +X local mira hacia fuera de la palma.
        /// </summary>
        /// <remarks>
        /// <paramref name="tiltDegrees"/> gira el eje del objeto sobre la normal de la palma: el objeto en DIAGONAL
        /// dentro del puño, como la «inclinación» de la búsqueda de primera persona. Con el eje obligado a ir por la
        /// línea de nudillos y el antebrazo hacia delante, la muñeca del vecino medía 80° de desviación radial.
        /// </remarks>
        public static void PlaceCylinder(HandFrame frame, float radius, float gripAlongY,
            out Vector3 position, out Quaternion rotation, float tiltDegrees = 0f)
        {
            Vector3 up = Quaternion.AngleAxis(tiltDegrees, frame.PalmNormal) * frame.KnuckleAxis;
            Vector3 right = frame.PalmNormal;
            Vector3 forward = Vector3.Cross(right, up);
            rotation = Quaternion.LookRotation(forward, up);
            position = frame.KnuckleCentre + frame.PalmNormal * (radius + SkinMetres) - up * gripAlongY;
        }

        /// <summary>
        /// Dónde agarrar a lo largo del eje de un objeto con una pieza lateral (la manivela) en
        /// <paramref name="obstacleAlongY"/>: la mano entera del lado de +Y, con su borde del meñique
        /// separado <paramref name="clearance"/> de la pieza. Sin pieza, el centro.
        /// </summary>
        public static float GripAlongAxis(float bodyCentreY, float halfLength, float handWidth,
            bool hasObstacle, float obstacleAlongY, float clearance)
        {
            if (!hasObstacle)
                return bodyCentreY;
            float grip = obstacleAlongY + handWidth * 0.5f + clearance;
            float maxGrip = bodyCentreY + halfLength - handWidth * 0.5f;
            return Mathf.Min(grip, maxGrip);
        }

        /// <summary>Distancia (positiva fuera, negativa dentro) de un punto a la superficie de un cilindro infinito.</summary>
        public static float DistanceToCylinder(Vector3 point, Vector3 axisPoint, Vector3 axisDir, float radius)
        {
            Vector3 d = point - axisPoint;
            return Vector3.ProjectOnPlane(d, axisDir).magnitude - radius;
        }

        /// <summary>
        /// Cierra una cadena de falanges girando cada segmento el mismo ángulo sobre
        /// <paramref name="axisWorld"/> (en espacio de mundo, raíz→hoja) hasta que la yema o una
        /// articulación toca el cilindro. Prueba los dos signos y se queda con el que acerca la yema.
        /// Deja la cadena cerrada y devuelve el ángulo aplicado con su signo.
        /// </summary>
        public static float CurlToTouch(Transform[] chain, Vector3 axisWorld, Vector3 axisPoint, Vector3 axisDir, float radius)
        {
            if (chain == null || chain.Length < 2)
                return 0f;

            var bind = new Quaternion[chain.Length];
            for (int i = 0; i < chain.Length; i++)
                bind[i] = chain[i] != null ? chain[i].localRotation : Quaternion.identity;

            float probe = 20f;
            float plus = TipDistance(chain, bind, axisWorld, probe, axisPoint, axisDir, radius);
            float minus = TipDistance(chain, bind, axisWorld, -probe, axisPoint, axisDir, radius);
            float sign = plus <= minus ? 1f : -1f;

            float applied = 0f;
            for (float a = CurlStepDegrees; a <= MaxCurlDegrees + 1e-3f; a += CurlStepDegrees)
            {
                Pose(chain, bind, axisWorld, sign * a);
                if (Touches(chain, axisPoint, axisDir, radius))
                    break;
                applied = sign * a;
            }
            Pose(chain, bind, axisWorld, applied);
            return applied;
        }

        /// <summary>
        /// Aplica un cierre ya resuelto sobre la pose ACTUAL de la cadena (la que ha dejado el Animator este
        /// fotograma): cada segmento gira <paramref name="degrees"/> sobre <paramref name="axisWorld"/>.
        /// </summary>
        public static void Curl(Transform[] chain, Vector3 axisWorld, float degrees)
        {
            if (chain == null || Mathf.Approximately(degrees, 0f))
                return;
            var q = Quaternion.AngleAxis(degrees, axisWorld);
            for (int i = 0; i < chain.Length; i++)
                if (chain[i] != null) chain[i].rotation = q * chain[i].rotation;
        }

        private static float TipDistance(Transform[] chain, Quaternion[] bind, Vector3 axisWorld, float degrees,
            Vector3 axisPoint, Vector3 axisDir, float radius)
        {
            Pose(chain, bind, axisWorld, degrees);
            float d = DistanceToCylinder(Tip(chain), axisPoint, axisDir, radius);
            Pose(chain, bind, axisWorld, 0f);
            return d;
        }

        private static void Pose(Transform[] chain, Quaternion[] bind, Vector3 axisWorld, float degrees)
        {
            for (int i = 0; i < chain.Length; i++)
                if (chain[i] != null) chain[i].localRotation = bind[i];
            if (Mathf.Approximately(degrees, 0f))
                return;
            var q = Quaternion.AngleAxis(degrees, axisWorld);
            for (int i = 0; i < chain.Length; i++)
                if (chain[i] != null) chain[i].rotation = q * chain[i].rotation;
        }

        private static bool Touches(Transform[] chain, Vector3 axisPoint, Vector3 axisDir, float radius)
        {
            for (int i = 1; i < chain.Length; i++)
                if (chain[i] != null && DistanceToCylinder(chain[i].position, axisPoint, axisDir, radius) <= 0f)
                    return true;
            return DistanceToCylinder(Tip(chain), axisPoint, axisDir, radius) <= 0f;
        }

        /// <summary>Yema estimada: la última falange prolongada.</summary>
        public static Vector3 Tip(Transform[] chain)
        {
            Transform last = chain[chain.Length - 1];
            Transform prev = chain[chain.Length - 2];
            return last.position + (last.position - prev.position) * TipExtension;
        }

        /// <summary>
        /// IK de dos huesos: gira <paramref name="upper"/> y <paramref name="lower"/> para que
        /// <paramref name="end"/> llegue a <paramref name="target"/> (o lo más cerca que permita el largo del
        /// brazo), con el codo hacia <paramref name="pole"/>. No toca la rotación de <paramref name="end"/> más
        /// que por herencia.
        /// </summary>
        public static void TwoBoneIk(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            if (upper == null || lower == null || end == null)
                return;

            Vector3 a = upper.position;
            float lenUpper = Vector3.Distance(a, lower.position);
            float lenLower = Vector3.Distance(lower.position, end.position);
            if (lenUpper < 1e-5f || lenLower < 1e-5f)
                return;

            Vector3 toTarget = target - a;
            float dist = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(lenUpper - lenLower) + 1e-4f, lenUpper + lenLower - 1e-4f);
            Vector3 dir = toTarget.sqrMagnitude > 1e-10f ? toTarget.normalized : upper.forward;

            Vector3 bend = Vector3.ProjectOnPlane(pole - a, dir);
            if (bend.sqrMagnitude < 1e-10f)
                bend = Vector3.ProjectOnPlane(lower.position - a, dir);
            if (bend.sqrMagnitude < 1e-10f)
                bend = Vector3.ProjectOnPlane(Vector3.down, dir);
            bend.Normalize();

            // Ley del coseno: ángulo en el hombro entre la recta al objetivo y el brazo.
            float cosShoulder = (lenUpper * lenUpper + dist * dist - lenLower * lenLower) / (2f * lenUpper * dist);
            float shoulder = Mathf.Acos(Mathf.Clamp(cosShoulder, -1f, 1f));
            Vector3 elbow = a + (dir * Mathf.Cos(shoulder) + bend * Mathf.Sin(shoulder)) * lenUpper;
            Vector3 reach = a + dir * dist;

            upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(end.position - lower.position, reach - lower.position) * lower.rotation;
        }
    }
}
