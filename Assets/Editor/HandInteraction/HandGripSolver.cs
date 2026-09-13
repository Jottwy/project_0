#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BackroomsSurvival.Gameplay.HandInteraction;
using UnityEngine;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    /// <summary>Lo que se mide de una mano puesta sobre el objeto. Es a la vez el coste de la búsqueda y
    /// el informe de validación: la herramienta nunca da por bueno algo que no haya medido así.</summary>
    [Serializable]
    public sealed class HandGripMetrics
    {
        public string hand;
        public string role;
        public float cost;
        public float alongAxis;
        public bool reachClamped;
        public float shoulderShiftMm;
        public float wristTwistDeg, wristFlexionDeg, wristDeviationDeg;
        /// <summary>Prono-supinación anatómica: 0 = mano de apretón (palma hacia el cuerpo), +90 = palma arriba, −90 = palma abajo.</summary>
        public float forearmRotationDeg;
        public float palmGapMm;
        public float elbowAngleDeg;
        public float[] tipGapMm = new float[5];
        public int fingersTouching;
        /// <summary>Yemas que cruzan la cara de la punta o de la culata (tapan la lente, la boquilla…).</summary>
        public int fingersOverEnd;
        public float worstFingerPenetrationMm;
        public string worstFingerJoint;
        public float thumbPenetrationMm;
        public float palmPenetrationMm;
        public float wrapCoverageDeg;
        public float wrapCoverageWantedDeg;
        public float handOverlapMm;
        public float targetErrorMm;
        public float viewAngleDeg;
        public List<string> costBreakdown = new();
    }

    /// <summary>La solución de una mano: su pose en el espacio de la malla de agarre (constante en todos
    /// los fotogramas) y los dedos, que tampoco cambian (por fotograma = tembleque, ADR-133 enm. 1).</summary>
    internal sealed class HandGripSolution
    {
        public bool Right;
        public float Along, Clock, Tilt, PalmOffset;
        public bool IndexTowardTip = true;
        /// <summary>0 codo del clip base, 1 abajo-fuera, 2 abajo, 3 <see cref="PoleWorld"/> (copiado de una referencia).</summary>
        public int Pole;
        public Vector3 PoleWorld;
        public Vector3 ShoulderShiftWorld;
        /// <summary>Hay pose de brazo propia (mano secundaria que agarra). La portadora y la relajada sólo traen dedos.</summary>
        public bool HasArmPose;
        public Vector3 HandPosInGrip;
        public Quaternion HandRotInGrip = Quaternion.identity;
        public Quaternion[][] Fingers;
        public HandGripMetrics Metrics;
    }

    /// <summary>
    /// El solver de agarre. La mano se coloca desde su OBJETIVO en el objeto (punto del eje, reloj,
    /// inclinación) usando el marco de agarre medido en la propia mano; el brazo sale por IK de dos huesos
    /// y los dedos cierran ACOPLADOS hasta tocar. Lo que no se sabe, se BARRE y se puntúa por naturalidad:
    /// muñeca dentro de su rango cómodo, codo abajo y sin bloquear, dedos que rodean y no atraviesan,
    /// pulgar y palma fuera de la malla, manos que no se pisan.
    /// </summary>
    internal static class HandGripSolver
    {
        public const float TipInside = 0.004f;
        public const float TipOutside = 0.012f;
        private const float TouchGap = 0.004f;
        private const float PalmSkin = 0.012f;

        // Rangos CÓMODOS de la muñeca (grados), no los topes: una mano que vive en el tope se ve forzada.
        // Signos del rig: +X = extensión en las dos manos (los dedos flexionan sobre −X); +Z = cubital en
        // la derecha y −Z en la izquierda (espejo). ADR-077 enm. 5 usa 20° de extensión y 10° cubital
        // como postura de fuerza.
        private const float ExtensionComfort = 35f, FlexionComfort = 15f, UlnarComfort = 25f, RadialComfort = 8f;
        private const float WristHardLimit = 70f, DeviationHardLimit = 35f;

        private static readonly Vector3 PoleRight = new(0.7f, -1f, -0.4f);
        private static readonly Vector3 PoleRightLow = new(0.3f, -1f, 0.0f);

        // ─── Colocar la mano ─────────────────────────────────────────────────────────────────────

        /// <summary>La rotación de mundo que lleva el marco de agarre de la mano al del objetivo.</summary>
        public static Quaternion HandRotationFor(HandInteractionRig rig, HandSide side, HandGripTarget target, float clock, float tilt)
        {
            Vector3 axis = rig.Axis;
            Vector3 radial = rig.Radial(clock);
            Vector3 palmNormal = -radial; // la palma mira AL eje
            Vector3 knuckleLine = target.indexTowardTip ? -axis : axis; // índice → meñique
            knuckleLine = Quaternion.AngleAxis(tilt, palmNormal) * knuckleLine;
            knuckleLine = Vector3.ProjectOnPlane(knuckleLine, palmNormal).normalized;

            var targetFrame = Quaternion.LookRotation(Vector3.Cross(knuckleLine, palmNormal), palmNormal);
            var handFrame = Quaternion.LookRotation(Vector3.Cross(side.KnuckleLineLocal, side.PalmNormalLocal), side.PalmNormalLocal);
            return targetFrame * Quaternion.Inverse(handFrame);
        }

        /// <summary>Separación inicial de la palma: con el eje en el hueco del puño genérico, la piel del objeto
        /// tiene que caer a una piel de los nudillos.</summary>
        public static float InitialPalmOffset(HandInteractionRig rig, HandSide side, float along)
        {
            float depth = side.PalmDepthLocal * side.Hand.lossyScale.x;
            return rig.RadiusAtAlong(along) + HandInteractionRig.FingerSkin - depth;
        }

        public static Vector3 GripPoint(HandInteractionRig rig, HandGripTarget target)
            => rig.AxisPoint(target.alongAxis) + rig.GripMesh.rotation * target.offsetMeters;

        public static Vector3 PoleFor(HandInteractionRig rig, HandSide side, int pole, Vector3 baseElbow, Vector3 baseShoulder, Vector3 baseHand)
        {
            Vector3 m(Vector3 v) => side.Right ? v : new Vector3(-v.x, v.y, v.z);
            switch (pole)
            {
                case 0:
                {
                    // El codo del clip base: preservarlo es lo más natural que hay, ya lo animó alguien.
                    Vector3 line = (baseHand - baseShoulder).normalized;
                    Vector3 off = Vector3.ProjectOnPlane(baseElbow - baseShoulder, line);
                    return off.sqrMagnitude > 1e-6f ? off.normalized : rig.InstanceRoot.TransformDirection(m(PoleRight));
                }
                case 1: return rig.InstanceRoot.TransformDirection(m(PoleRight));
                default: return rig.InstanceRoot.TransformDirection(m(PoleRightLow));
            }
        }

        /// <summary>
        /// Pone brazo y mano en un candidato. El hombro se adelanta sólo lo que falte para llegar (con
        /// tope): un rig de brazos 1P no tiene torso y nadie ve el hombro, pero un brazo estirado a tope sí.
        /// </summary>
        public static bool PlaceArm(HandInteractionRig rig, HandSide side, Vector3 gripPoint, Quaternion handRot,
            float palmOffset, float clock, Vector3 pole, Vector3 baseShoulder, float maxShift, out Vector3 shift)
        {
            Vector3 radial = rig.Radial(clock);
            Vector3 enclosed = handRot * Vector3.Scale(side.EnclosedLocal, side.Hand.lossyScale);
            Vector3 handPos = gripPoint + radial * palmOffset - enclosed;

            shift = Vector3.zero;
            side.Upper.position = baseShoulder;
            float a = (side.Fore.position - side.Upper.position).magnitude;
            float b = (side.Hand.position - side.Fore.position).magnitude;
            float reach = a + b - 0.01f;
            Vector3 toTarget = handPos - baseShoulder;
            float deficit = toTarget.magnitude - reach;
            if (deficit > 0f && maxShift > 0f)
            {
                shift = toTarget.normalized * Mathf.Min(maxShift, deficit + 0.03f);
                side.Upper.position = baseShoulder + shift;
            }
            bool ok = HandInteractionRig.SolveTwoBone(side, handPos, handRot, pole);
            HandInteractionRig.DistributeForearmTwist(side);
            return ok;
        }

        // ─── Dedos ───────────────────────────────────────────────────────────────────────────────

        private static float MidTolerance(float length, float radius, float skin)
        {
            // Una falange es RECTA y la superficie redonda: su medio se hunde L²/8R al rodearla (ADR-077 enm. 4).
            float sagitta = length * length / (8f * Mathf.Max(0.004f, radius + skin));
            return Mathf.Max(skin / 3f, sagitta * 0.75f);
        }

        /// <summary>¿Alguna falange desde <paramref name="fromJoint"/> se mete en el objeto? Devuelve la peor profundidad.</summary>
        private static float ChainPenetration(HandInteractionRig rig, HandSide side, int finger, int fromJoint, float radius)
        {
            bool thumb = finger == 0;
            float skin = thumb ? HandInteractionRig.ThumbSkin : HandInteractionRig.FingerSkin;
            float worst = 0f;
            for (int j = fromJoint; j < 3; j++)
            {
                Vector3 a = side.Fingers[finger][j].position, b = HandInteractionRig.EndOf(side, finger, j);
                float end = rig.SurfaceGap(b) - skin;
                float mid = rig.SurfaceGap(Vector3.Lerp(a, b, 0.5f)) - skin;
                float tol = MidTolerance((b - a).magnitude, radius, skin);
                worst = Mathf.Max(worst, Mathf.Max(-(end + 0.0015f), -(mid + tol)));
            }
            return worst;
        }

        private static void SetCoupled(HandSide side, int finger, float c, float[] caps, float sign, Vector3 axis, int firstJoint, Quaternion[] start)
        {
            for (int j = firstJoint; j < 3; j++)
                side.Fingers[finger][j].localRotation = start[j] * Quaternion.AngleAxis(caps[j] * c * sign, axis);
        }

        /// <summary>
        /// Cierra UN dedo como lo cierra una mano: las tres articulaciones a la vez (tendón), hasta que
        /// algo toca; después las dos de la punta siguen cerrando solas hasta que la yema llega, con un
        /// margen que conserva la cascada. Un dedo al que no le llega nada se queda donde MÁS se acerca,
        /// no cerrado en el aire (el meñique del bote, ADR-077 enm. 5).
        /// </summary>
        private static void CloseFinger(HandInteractionRig rig, HandSide side, int finger, float radius, float maxC, int firstJoint)
        {
            bool thumb = finger == 0;
            var caps = thumb ? HandInteractionRig.ThumbCaps : HandInteractionRig.FingerCaps;
            var axis = thumb ? Vector3.forward : Vector3.right;
            float sign = thumb ? side.ThumbCloseSign : side.FingerCloseSign;
            float skin = thumb ? HandInteractionRig.ThumbSkin : HandInteractionRig.FingerSkin;
            var start = side.Fingers[finger].Select(j => j.localRotation).ToArray();

            float stopC = maxC, nearestC = 0f, nearest = float.MaxValue;
            bool blocked = false;
            float previous = 0f;
            for (float c = 0f; c <= maxC + 1e-4f; c += 0.025f)
            {
                SetCoupled(side, finger, c, caps, sign, axis, firstJoint, start);
                if (ChainPenetration(rig, side, finger, firstJoint, radius) > 0f)
                {
                    stopC = previous;
                    blocked = true;
                    break;
                }
                float tip = Mathf.Abs(rig.SurfaceGap(HandInteractionRig.Tip(side, finger)) - skin - TouchGap);
                if (tip < nearest) { nearest = tip; nearestC = c; }
                previous = c;
            }
            float chosen = blocked ? stopC : nearestC;
            SetCoupled(side, finger, chosen, caps, sign, axis, firstJoint, start);

            // Las dos falanges de la punta rematan el contacto: +35° como mucho sobre el acoplado.
            for (int j = 2; j >= Mathf.Max(1, firstJoint); j--)
            {
                var baseRot = side.Fingers[finger][j].localRotation;
                Quaternion best = baseRot;
                for (float extra = 1f; extra <= 35f; extra += 1f)
                {
                    float total = caps[j] * chosen + extra;
                    if (total > caps[j]) break;
                    side.Fingers[finger][j].localRotation = baseRot * Quaternion.AngleAxis(extra * sign, axis);
                    if (ChainPenetration(rig, side, finger, j, radius) > 0f) break;
                    best = side.Fingers[finger][j].localRotation;
                    if (rig.SurfaceGap(HandInteractionRig.Tip(side, finger)) - skin <= TouchGap) break;
                }
                side.Fingers[finger][j].localRotation = best;
            }
        }

        /// <summary>El pulgar: se prueban unas pocas orientaciones de su base (oposición y cierre) y en cada una
        /// cierran las dos falanges de la punta. Gana la que toca por el lado CONTRARIO a los dedos sin meterse.</summary>
        private static void CloseThumb(HandInteractionRig rig, HandSide side, HandFingerStyle style, float radius, Vector3 gripPoint)
        {
            var chain = side.Fingers[0];
            Vector3 axis = rig.Axis;

            if (style == HandFingerStyle.ThumbAlongAxis)
            {
                for (int j = 0; j < 3; j++) chain[j].localRotation = Quaternion.identity;
                chain[0].rotation = Quaternion.FromToRotation(chain[0].up, axis) * chain[0].rotation;
                CloseFinger(rig, side, 0, radius, 1f, 1);
                return;
            }

            float bestCost = float.MaxValue;
            var best = new[] { Quaternion.identity, Quaternion.identity, Quaternion.identity };
            Vector3 fingersSide = Vector3.zero;
            for (int f = 1; f < 5; f++) fingersSide += HandInteractionRig.Tip(side, f);
            fingersSide /= 4f;

            for (float opp = -30f; opp <= 30f; opp += 15f)
            for (float close = 0f; close <= 45f; close += 15f)
            {
                chain[0].localRotation = Quaternion.AngleAxis(opp, Vector3.right) *
                                         Quaternion.AngleAxis(close * side.ThumbCloseSign, Vector3.forward);
                chain[1].localRotation = Quaternion.identity;
                chain[2].localRotation = Quaternion.identity;
                CloseFinger(rig, side, 0, radius, 1f, 1);

                Vector3 tip = HandInteractionRig.Tip(side, 0);
                float gap = rig.SurfaceGap(tip) - HandInteractionRig.ThumbSkin;
                float pen = ChainPenetration(rig, side, 0, 0, radius);
                // Un pulgar que entra en el objeto se descarta, no se pondera: con pen×400 un candidato que entraba
                // 12 mm ganaba a uno que no tocaba (medido en la linterna).
                float cost = Mathf.Abs(gap - TouchGap) * 100f + (pen > 0.002f ? 100f + pen * 1000f : pen * 400f);
                // Opuesto a los dedos alrededor del eje: es lo que convierte tocar en SUJETAR.
                Vector3 tDir = Vector3.ProjectOnPlane(tip - gripPoint, axis);
                Vector3 fDir = Vector3.ProjectOnPlane(fingersSide - gripPoint, axis);
                if (tDir.sqrMagnitude > 1e-8f && fDir.sqrMagnitude > 1e-8f)
                    cost += Mathf.Max(0f, 90f - Vector3.Angle(tDir, fDir)) / 90f;
                // Y sin pisar al índice.
                float toIndex = (tip - HandInteractionRig.Tip(side, 1)).magnitude;
                if (toIndex < 0.018f) cost += (0.018f - toIndex) * 200f;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = chain.Select(j => j.localRotation).ToArray();
                }
            }
            for (int j = 0; j < 3; j++) chain[j].localRotation = best[j];
        }

        public static void FitFingers(HandInteractionRig rig, HandSide side, HandGripTarget target)
        {
            float radius = rig.RadiusAtAlong(target.alongAxis);
            HandInteractionRig.SetNeutralFingers(side);
            for (int f = 1; f < 5; f++)
            {
                float maxC = f == 1 && target.fingers == HandFingerStyle.IndexExtended ? 0.18f : 1f;
                CloseFinger(rig, side, f, radius, maxC, 0);
            }
            CloseThumb(rig, side, target.fingers, radius, GripPoint(rig, target));
        }

        /// <summary>Una mano que no sujeta nada: cascada relajada, el meñique algo más cerrado que el índice.</summary>
        public static void RelaxFingers(HandSide side)
        {
            HandInteractionRig.SetNeutralFingers(side);
            float[] curl = { 0.18f, 0.22f, 0.28f, 0.34f, 0.40f };
            for (int f = 0; f < 5; f++)
            {
                bool thumb = f == 0;
                var caps = thumb ? HandInteractionRig.ThumbCaps : HandInteractionRig.FingerCaps;
                var axis = thumb ? Vector3.forward : Vector3.right;
                float sign = thumb ? side.ThumbCloseSign : side.FingerCloseSign;
                for (int j = thumb ? 1 : 0; j < 3; j++)
                    side.Fingers[f][j].localRotation = Quaternion.AngleAxis(caps[j] * curl[f] * sign, axis);
            }
        }

        // ─── Medir ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mide la mano TAL COMO ESTÁ y devuelve el coste de naturalidad con su desglose. La usan la búsqueda
        /// y la validación del resultado horneado, así que un número del informe es exactamente lo que se optimizó.
        /// </summary>
        public static HandGripMetrics Measure(HandInteractionRig rig, HandSide side, HandGripTarget target, float clock,
            bool reachClamped, Vector3 shoulderShift, float baseTwist, HandSide other, bool checkFingers, bool checkView,
            bool checkBodyContact = true)
        {
            var m = new HandGripMetrics { hand = side.Suffix, role = target.role.ToString(), reachClamped = reachClamped, alongAxis = target.alongAxis };
            float cost = 0f;
            void Add(string what, float c)
            {
                if (c <= 1e-4f) return;
                cost += c;
                m.costBreakdown.Add($"{what}={c:0.00}");
            }

            float radius = rig.RadiusAtAlong(target.alongAxis);
            Vector3 gripPoint = GripPoint(rig, target);

            // Brazo.
            if (reachClamped) Add("alcance", 60f);
            m.shoulderShiftMm = shoulderShift.magnitude * 1000f;
            Add("hombro", Mathf.Pow(shoulderShift.magnitude / 0.08f, 2f) * 0.5f);

            HandInteractionRig.WristAngles(side, out float twist, out _, out _);
            m.wristTwistDeg = twist;
            Vector3 shoulder = side.Upper.position, elbow = side.Fore.position, wrist = side.Hand.position;

            // LA MUÑECA SE MIDE CON VECTORES DEL CUERPO, no con los ejes del hueso. La descomposición
            // torsión-columpio de la rotación local daba 95° de extensión al agarre de la linterna que Joel dio
            // por bueno (ADR-133 enm. 1): en este rig la rotación local de Hand no es el ángulo anatómico. Aquí:
            // antebrazo (codo→muñeca) contra metacarpo (muñeca→nudillo del corazón), partido por la normal de la
            // palma (flexión +, hacia la palma) y la línea de nudillos (cubital +, hacia el meñique).
            float flex, ulnar;
            {
                Vector3 fore = (wrist - elbow).normalized;
                Vector3 meta = (side.Fingers[2][0].position - wrist).normalized;
                Vector3 palmDir = Vector3.ProjectOnPlane(side.Hand.TransformDirection(side.PalmNormalLocal), fore).normalized;
                Vector3 pinkyDir = Vector3.ProjectOnPlane(side.Hand.TransformDirection(side.KnuckleLineLocal), fore);
                pinkyDir = Vector3.ProjectOnPlane(pinkyDir, palmDir).normalized;
                // DOBLEZ TOTAL y su DIRECCIÓN, no dos atan2 contra el antebrazo: con más de 90° de doblez el eje del
                // antebrazo sale negativo en los dos y cada componente se dispara (una flexión pura de 100° leía
                // además −97° de desviación). Se reparte el doblez real según hacia dónde apunta el metacarpo.
                float bend = Vector3.Angle(fore, meta);
                Vector3 across = Vector3.ProjectOnPlane(meta, fore);
                float direction = across.sqrMagnitude > 1e-8f
                    ? Mathf.Atan2(Vector3.Dot(across, pinkyDir), Vector3.Dot(across, palmDir))
                    : 0f;
                flex = bend * Mathf.Cos(direction);
                ulnar = bend * Mathf.Sin(direction);
            }
            m.wristFlexionDeg = flex;
            m.wristDeviationDeg = ulnar;
            float dev = ulnar;

            // LA PRONO-SUPINACIÓN SE MIDE EN EL CUERPO, no contra el clip base. La primera pasada comparaba la
            // torsión con la de la izquierda del vendor, que cuelga fuera de cuadro: una mano palma arriba (81°)
            // salía casi gratis y la captura la enseñaba retorcida. Aquí: la normal de la palma alrededor del
            // antebrazo, con 0 = mano de apretón (palma hacia la línea media), +90 = palma arriba.
            {
                Vector3 fore = (wrist - elbow).normalized;
                Vector3 palm = Vector3.ProjectOnPlane(side.Hand.TransformDirection(side.PalmNormalLocal), fore).normalized;
                Vector3 medial = Vector3.ProjectOnPlane(side.Right ? -rig.InstanceRoot.right : rig.InstanceRoot.right, fore).normalized;
                Vector3 upRef = Vector3.ProjectOnPlane(rig.InstanceRoot.up, fore);
                upRef = Vector3.ProjectOnPlane(upRef, medial).normalized;
                m.forearmRotationDeg = Mathf.Atan2(Vector3.Dot(palm, upRef), Vector3.Dot(palm, medial)) * Mathf.Rad2Deg;
            }
            // Cómodo: de 30° de supinación a 70° de pronación. Palma arriba del todo o girada hacia fuera, forzado.
            Add("supinación", Mathf.Pow(Mathf.Max(0f, m.forearmRotationDeg - 30f) / 15f, 2f));
            Add("pronación", Mathf.Pow(Mathf.Max(0f, -m.forearmRotationDeg - 70f) / 15f, 2f));
            if (Mathf.Abs(m.forearmRotationDeg) > 110f) Add("antebrazo-tope", 25f);
            // flex > 0 = hacia la palma (flexión); < 0 = extensión.
            Add("extensión", Mathf.Pow(Mathf.Max(0f, -flex - ExtensionComfort) / 12f, 2f));
            Add("flexión", Mathf.Pow(Mathf.Max(0f, flex - FlexionComfort) / 12f, 2f));
            Add("cubital", Mathf.Pow(Mathf.Max(0f, ulnar - UlnarComfort) / 10f, 2f));
            Add("radial", Mathf.Pow(Mathf.Max(0f, -ulnar - RadialComfort) / 10f, 2f));
            if (Mathf.Abs(flex) > WristHardLimit || Mathf.Abs(dev) > DeviationHardLimit) Add("muñeca-tope", 25f);
            _ = baseTwist; // la torsión del clip base ya no puntúa: ver arriba
            m.elbowAngleDeg = Vector3.Angle(shoulder - elbow, wrist - elbow);
            Add("codo-cerrado", Mathf.Pow(Mathf.Max(0f, 70f - m.elbowAngleDeg) / 20f, 2f));
            Add("codo-bloqueado", Mathf.Pow(Mathf.Max(0f, m.elbowAngleDeg - 160f) / 10f, 2f));
            Vector3 up = rig.InstanceRoot.up;
            float elbowOverWrist = Vector3.Dot(elbow - wrist, up);
            Add("codo-alto", Mathf.Pow(Mathf.Max(0f, elbowOverWrist - 0.03f) / 0.05f, 2f));
            float elbowOverShoulder = Vector3.Dot(elbow - shoulder, up);
            Add("codo-sobre-hombro", Mathf.Pow(Mathf.Max(0f, elbowOverShoulder) / 0.05f, 2f));

            // Palma: la muñeca y los nudillos fuera del objeto, pero CERCA. Un objeto sostenido con las yemas y
            // la palma a 3 cm (la primera captura) toca en los números y no sujeta nada.
            float palmPen = 0f, knuckleGap = float.MaxValue;
            for (int f = 1; f < 5; f++)
            {
                Vector3 k = side.Fingers[f][0].position;
                float kg = rig.SurfaceGap(k) - HandInteractionRig.FingerSkin;
                knuckleGap = Mathf.Min(knuckleGap, kg);
                palmPen = Mathf.Max(palmPen, -kg);
                palmPen = Mathf.Max(palmPen, -(rig.SurfaceGap(Vector3.Lerp(wrist, k, 0.5f)) - PalmSkin));
            }
            m.palmPenetrationMm = palmPen * 1000f;
            m.palmGapMm = knuckleGap * 1000f;
            Add("palma-dentro", Mathf.Max(0f, palmPen - 0.002f) * 1000f * 1.0f);
            // Una mano que agarra OTRA pieza (el pomo) está lejos del cuerpo a propósito.
            if (checkBodyContact) Add("palma-lejos", Mathf.Pow(Mathf.Max(0f, knuckleGap - 0.012f) / 0.008f, 2f));

            // Dónde encierra la mano respecto de su punto (el eje debería pasar por el hueco del puño).
            Vector3 enclosed = Vector3.zero;
            for (int f = 1; f < 5; f++) enclosed += (side.Fingers[f][1].position + side.Fingers[f][2].position) * 0.5f;
            enclosed /= 4f;
            m.targetErrorMm = Vector3.ProjectOnPlane(enclosed - gripPoint, rig.Axis).magnitude * 1000f;
            if (checkFingers) Add("fuera-del-puño", Mathf.Pow(Mathf.Max(0f, m.targetErrorMm - 12f) / 10f, 2f));

            if (checkFingers)
            {
                float worst = 0f;
                string worstJoint = "";
                m.fingersTouching = 0;
                for (int f = 0; f < 5; f++)
                {
                    bool thumb = f == 0;
                    float skin = thumb ? HandInteractionRig.ThumbSkin : HandInteractionRig.FingerSkin;
                    Vector3 tip = HandInteractionRig.Tip(side, f);
                    float gap = rig.SurfaceGap(tip) - skin;
                    m.tipGapMm[f] = gap * 1000f;
                    if (gap <= TipOutside && gap >= -TipInside) m.fingersTouching++;
                    float pen = ChainPenetration(rig, side, f, 0, radius);
                    if (thumb) m.thumbPenetrationMm = pen * 1000f;
                    if (pen > worst) { worst = pen; worstJoint = side.Fingers[f][0].name; }
                    if (f == 1 && target.fingers == HandFingerStyle.IndexExtended) continue;
                    if (gap > TipOutside) Add($"yema-lejos-{f}", (gap - TipOutside) * 1000f * 0.15f);
                    if (gap < -TipInside) Add($"yema-dentro-{f}", (-TipInside - gap) * 1000f * 0.6f);
                }
                m.worstFingerPenetrationMm = worst * 1000f;
                m.worstFingerJoint = worstJoint;
                Add("falange-dentro", worst * 1000f * 0.8f);

                // LOS EXTREMOS NO SE TAPAN. Una yema más allá de la punta o de la culata y a la altura del eje no
                // penetra nada —cae fuera de la malla— y la primera captura buena la enseñó cruzando la LENTE de la
                // linterna. La punta de un objeto suele ser su parte funcional (lente, boquilla, filo).
                m.fingersOverEnd = 0;
                for (int f = 0; f < 5; f++)
                {
                    var surface = rig.Surface;
                    surface.Local(HandInteractionRig.Tip(side, f), out float tl, out float rl);
                    float beyond = Mathf.Max(tl - surface.MaxT, surface.MinT - tl) * surface.Scale;
                    float radial = rl * surface.Scale;
                    float endRadius = surface.RadiusAtT(tl > surface.MaxT ? surface.MaxT : surface.MinT) * surface.Scale;
                    if (beyond > 0.002f && radial < endRadius * 0.85f)
                    {
                        m.fingersOverEnd++;
                        Add($"tapa-extremo-{f}", 2f + beyond * 1000f * 0.1f);
                    }
                }

                // RODEAR, no sólo tocar (la deuda de ADR-077 enm. 5): el ángulo que recorren las yemas
                // alrededor del eje desde el lado de la palma.
                Vector3 palmRadial = rig.Radial(clock);
                float sum = 0f; int n = 0;
                for (int f = 1; f < 5; f++)
                {
                    if (f == 1 && target.fingers == HandFingerStyle.IndexExtended) continue;
                    Vector3 tip = HandInteractionRig.Tip(side, f);
                    Vector3 dir = Vector3.ProjectOnPlane(tip - gripPoint, rig.Axis);
                    if (dir.sqrMagnitude < 1e-8f) continue;
                    sum += Vector3.Angle(palmRadial, dir); n++;
                }
                m.wrapCoverageDeg = n > 0 ? sum / n : 0f;
                float fingerLength = (side.Fingers[2][0].position - side.Fingers[2][2].position).magnitude + side.TipLength[2];
                float reachableDeg = fingerLength / Mathf.Max(0.005f, radius + HandInteractionRig.FingerSkin) * Mathf.Rad2Deg;
                m.wrapCoverageWantedDeg = Mathf.Clamp(reachableDeg * 0.75f, 70f, 150f);
                Add("no-rodea", Mathf.Max(0f, m.wrapCoverageWantedDeg - m.wrapCoverageDeg) / 25f);
            }

            if (other != null)
            {
                float closest = float.MaxValue;
                foreach (var p in JointPoints(side))
                    foreach (var q in JointPoints(other))
                        closest = Mathf.Min(closest, (p - q).magnitude);
                m.handOverlapMm = closest * 1000f;
                Add("manos-juntas", Mathf.Pow(Mathf.Max(0f, 0.022f - closest) / 0.004f, 2f));
            }

            if (checkView)
            {
                Vector3 eye = rig.Camera.position;
                m.viewAngleDeg = Vector3.Angle(rig.InstanceRoot.forward, wrist - eye);
                Add("fuera-de-vista", Mathf.Pow(Mathf.Max(0f, m.viewAngleDeg - 40f) / 12f, 2f) * 0.5f);
            }

            m.cost = cost;
            return m;
        }

        public static IEnumerable<Vector3> JointPoints(HandSide side)
        {
            yield return side.Hand.position;
            for (int f = 0; f < 5; f++)
            {
                for (int j = 0; j < 3; j++) yield return side.Fingers[f][j].position;
                yield return HandInteractionRig.Tip(side, f);
            }
        }

        // ─── Buscar ──────────────────────────────────────────────────────────────────────────────

        private struct Candidate
        {
            public float Along, Clock, Tilt, Palm, Cost;
            public int Pole;
            public bool IndexTip;
        }

        private static HandGripTarget With(HandGripTarget t, float along)
        {
            var copy = JsonUtility.FromJson<HandGripTarget>(JsonUtility.ToJson(t));
            copy.alongAxis = along;
            return copy;
        }

        private static HandGripTarget With(HandGripTarget t, float along, bool indexTowardTip)
        {
            var copy = With(t, along);
            copy.indexTowardTip = indexTowardTip;
            return copy;
        }

        /// <summary>
        /// Resuelve una mano que AGARRA y que no es la portadora: punto del eje, reloj, inclinación, codo y
        /// separación de la palma se barren alrededor de lo pedido y gana el candidato más natural. El rig tiene
        /// que estar en el fotograma de referencia (idle, t = 0) y la otra mano ya puesta.
        /// </summary>
        public static HandGripSolution SolveSecondary(HandInteractionRig rig, HandSide side, HandGripTarget target,
            float maxShoulderShift, HandSide other, StringBuilder log)
        {
            var baseFrame = rig.Capture(0f);
            Vector3 baseShoulder = side.Upper.position, baseElbow = side.Fore.position, baseHand = side.Hand.position;
            float baseTwist = HandInteractionRig.TwistDegrees(Quaternion.Inverse(side.Fore.rotation) * side.Hand.rotation);

            float alongRange = target.autoSearch ? target.searchAlongRange : 0f;
            float clockRange = target.autoSearch ? target.searchClockRange : 0f;
            float tiltRange = target.autoSearch ? target.searchTiltRange : 0f;

            float Evaluate(float along, float clock, float tilt, float palmExtra, int pole, bool indexTip, bool fingers, out HandGripMetrics metrics, out Vector3 shift)
            {
                rig.Apply(baseFrame);
                var work = With(target, along, indexTip);
                var rot = HandRotationFor(rig, side, work, clock, tilt);
                var poleDir = PoleFor(rig, side, pole, baseElbow, baseShoulder, baseHand);
                float palm = InitialPalmOffset(rig, side, along) + target.palmOffsetMeters + palmExtra;
                bool ok = PlaceArm(rig, side, GripPoint(rig, work), rot, palm, clock, poleDir, baseShoulder, maxShoulderShift, out shift);
                if (fingers) FitFingers(rig, side, work);
                else { HandInteractionRig.SetNeutralFingers(side); HandInteractionRig.CloseCoupled(side, 0.55f, includeThumb: false); }
                metrics = Measure(rig, side, work, clock, !ok, shift, baseTwist, other, fingers, checkView: true);
                float intent = Mathf.Pow(Mathf.DeltaAngle(clock, target.clockDegrees) / 90f, 2f) * 0.3f +
                               Mathf.Pow((tilt - target.tiltDegrees) / 20f, 2f) * 0.3f +
                               Mathf.Pow((along - target.alongAxis) / 0.25f, 2f) * 0.3f +
                               (indexTip != target.indexTowardTip ? 0.5f : 0f);
                return metrics.cost + intent;
            }
            // La mano girada (índice hacia la culata) sólo se prueba si se deja buscar.
            var orientations = target.autoSearch ? new[] { target.indexTowardTip, !target.indexTowardTip } : new[] { target.indexTowardTip };

            IEnumerable<float> Range(float centre, float half, float step, float min, float max, bool wrap)
            {
                if (half <= 0f) { yield return centre; yield break; }
                for (float d = -half; d <= half + 1e-3f; d += step)
                {
                    if (wrap && half >= 180f && d >= 180f - 1e-3f) yield break; // −180 y +180 son el mismo
                    float v = centre + d;
                    if (!wrap && (v < min - 1e-4f || v > max + 1e-4f)) continue;
                    yield return v;
                }
            }

            // A: barato, sin dedos. Brazo, muñeca, codo, palma, manos, vista.
            var coarse = new List<Candidate>();
            foreach (float along in Range(target.alongAxis, alongRange, 0.05f, 0.03f, 0.97f, false))
            foreach (float clock in Range(target.clockDegrees, clockRange, 15f, 0f, 0f, true))
            foreach (float tilt in Range(target.tiltDegrees, tiltRange, 10f, -90f, 90f, false))
            foreach (bool indexTip in orientations)
            for (int pole = 0; pole < 3; pole++)
            {
                float c = Evaluate(along, clock, tilt, 0f, pole, indexTip, false, out _, out _);
                coarse.Add(new Candidate { Along = along, Clock = clock, Tilt = tilt, Pole = pole, IndexTip = indexTip, Cost = c });
            }
            var top = coarse.OrderBy(c => c.Cost).Take(24).ToList();

            // B: con dedos, refinando alrededor de los mejores. Se guarda el mejor de cada semilla para el
            // informe: si los cinco primeros fallan por lo mismo, el problema es el objeto, no la búsqueda.
            Candidate best = top[0];
            best.Cost = float.MaxValue;
            var seeds = new List<(Candidate cand, HandGripMetrics metrics)>();
            foreach (var cand in top)
            {
                Candidate seedBest = cand;
                seedBest.Cost = float.MaxValue;
                foreach (float along in Range(cand.Along, alongRange > 0f ? 0.025f : 0f, 0.025f, 0.03f, 0.97f, false))
                // El reloj es circular: sin límites (con min = max = 0 y sin «wrap» se descartaba TODO y la etapa B
                // no llegaba a evaluar ni un candidato — los costes salían float.MaxValue).
                foreach (float clock in Range(cand.Clock, clockRange > 0f ? 7.5f : 0f, 7.5f, 0f, 0f, true))
                foreach (float tilt in Range(cand.Tilt, tiltRange > 0f ? 5f : 0f, 5f, -90f, 90f, false))
                for (float dp = -0.02f; dp <= 0.0201f; dp += 0.005f)
                {
                    float c = Evaluate(along, clock, tilt, dp, cand.Pole, cand.IndexTip, true, out _, out _);
                    if (c < seedBest.Cost)
                        seedBest = new Candidate { Along = along, Clock = clock, Tilt = tilt, Palm = dp, Pole = cand.Pole, IndexTip = cand.IndexTip, Cost = c };
                }
                Evaluate(seedBest.Along, seedBest.Clock, seedBest.Tilt, seedBest.Palm, seedBest.Pole, seedBest.IndexTip, true, out var seedMetrics, out _);
                seeds.Add((seedBest, seedMetrics));
                if (seedBest.Cost < best.Cost) best = seedBest;
            }
            foreach (var (c, m) in seeds.OrderBy(s => s.cand.Cost).Take(5))
                log?.AppendLine($"  candidato: eje {c.Along:0.00}, reloj {Mathf.Repeat(c.Clock + 180f, 360f) - 180f:0}°, incl. {c.Tilt:0}°, " +
                                $"índice→{(c.IndexTip ? "punta" : "culata")}, coste {c.Cost:0.00} [{string.Join(", ", m.costBreakdown)}]");
            // Remate fino de la palma.
            var coarseBest = best;
            for (float dp = -0.004f; dp <= 0.0041f; dp += 0.001f)
            {
                float c = Evaluate(coarseBest.Along, coarseBest.Clock, coarseBest.Tilt, coarseBest.Palm + dp, coarseBest.Pole, coarseBest.IndexTip, true, out _, out _);
                if (c < best.Cost) { best = coarseBest; best.Palm = coarseBest.Palm + dp; best.Cost = c; }
            }

            Evaluate(best.Along, best.Clock, best.Tilt, best.Palm, best.Pole, best.IndexTip, true, out var final, out var finalShift);
            var sol = new HandGripSolution
            {
                Right = side.Right,
                Along = best.Along,
                Clock = Mathf.Repeat(best.Clock + 180f, 360f) - 180f,
                Tilt = best.Tilt,
                PalmOffset = target.palmOffsetMeters + best.Palm,
                Pole = best.Pole,
                ShoulderShiftWorld = finalShift,
                HasArmPose = true,
                HandPosInGrip = rig.GripMesh.InverseTransformPoint(side.Hand.position),
                HandRotInGrip = Quaternion.Inverse(rig.GripMesh.rotation) * side.Hand.rotation,
                Fingers = HandInteractionRig.ReadFingers(side),
                Metrics = final,
            };
            sol.IndexTowardTip = best.IndexTip;
            log?.AppendLine($"mano {side.Suffix}: {coarse.Count} candidatos gruesos; eje {sol.Along:0.00}, reloj {sol.Clock:0}°, inclinación {sol.Tilt:0}°, " +
                            $"índice hacia la {(best.IndexTip ? "punta" : "culata")}, antebrazo {final.forearmRotationDeg:+0;-0}° (+ = palma arriba), " +
                            $"palma {best.Palm * 1000f:+0.0;-0.0} mm sobre la inicial, codo {PoleName(sol.Pole)}, hombro {final.shoulderShiftMm:0} mm; " +
                            $"coste {final.cost:0.00} [{string.Join(", ", final.costBreakdown)}]");
            rig.Apply(baseFrame);
            return sol;
        }

        /// <summary>La mano portadora (de la que cuelga el objeto): el brazo no se mueve; sólo se rehacen los
        /// dedos con el cierre acoplado. El reloj y el punto del eje se MIDEN de dónde está la mano.</summary>
        public static HandGripSolution SolveCarrierFingers(HandInteractionRig rig, HandSide side, HandGripTarget target, StringBuilder log)
        {
            float baseTwist = HandInteractionRig.TwistDegrees(Quaternion.Inverse(side.Fore.rotation) * side.Hand.rotation);
            var measured = MeasuredCarrierTarget(rig, side, target, out float clock);
            // LO QUE HABÍA ES LA REFERENCIA: si los dedos nuevos no mejoran el coste, se conservan. Rehacer la
            // portadora de la linterna metió el pulgar 12 mm en el cuerpo y el horneado lo escribió igual.
            var original = HandInteractionRig.ReadFingers(side);
            var before = Measure(rig, side, measured, clock, false, Vector3.zero, baseTwist, null, true, checkView: false);
            FitFingers(rig, side, measured);
            var metrics = Measure(rig, side, measured, clock, false, Vector3.zero, baseTwist, null, true, checkView: false);
            if (metrics.cost >= before.cost)
            {
                HandInteractionRig.WriteFingers(side, original);
                log?.AppendLine($"mano {side.Suffix} (portadora): los dedos rehechos cuestan {metrics.cost:0.00} y los que había {before.cost:0.00}: " +
                                "SE CONSERVAN los que había.");
                metrics = before;
            }
            else
                log?.AppendLine($"mano {side.Suffix} (portadora): dedos rehechos a {measured.alongAxis:0.00} del eje, palma a las {clock:0}°; " +
                                $"coste {before.cost:0.00} → {metrics.cost:0.00} [{string.Join(", ", metrics.costBreakdown)}]");
            return new HandGripSolution
            {
                Right = side.Right, Along = measured.alongAxis, Clock = clock, Fingers = HandInteractionRig.ReadFingers(side), Metrics = metrics,
            };
        }

        /// <summary>El objetivo de la portadora leído de su pose: altura de los nudillos en el eje y lado de la palma.</summary>
        public static HandGripTarget MeasuredCarrierTarget(HandInteractionRig rig, HandSide side, HandGripTarget target, out float clock)
        {
            Vector3 knuckles = Vector3.zero;
            for (int f = 1; f < 5; f++) knuckles += side.Fingers[f][0].position;
            knuckles /= 4f;
            Vector3 local = rig.GripMesh.InverseTransformPoint(knuckles);
            clock = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            // La portadora agarra siempre la malla principal, aunque su objetivo nombre otra pieza.
            var measured = With(target, rig.MainSurface.Along01(knuckles));
            measured.gripPartNodeName = null;
            measured.role = HandRole.Grip;
            measured.offsetMeters = Vector3.zero;
            return measured;
        }

        /// <summary>
        /// Una mano con rol <see cref="HandRole.Reference"/>: su pose se lee de otro clip del wieldable (la izquierda
        /// sobre el pomo de la cuerda, en su fotograma de reposo) y se guarda en el espacio del objeto, con sus dedos,
        /// el adelanto del hombro y la dirección del codo. Nada se busca: ese agarre ya lo resolvió y validó su
        /// horneador. Se MIDE cómo queda reproducido por IK desde el clip base, que es lo que se va a hornear.
        /// El rig tiene que estar en el fotograma de referencia del idle y AnimationMode abierto.
        /// </summary>
        public static HandGripSolution SolveFromReference(HandInteractionRig rig, HandSide side, HandGripTarget target,
            HandSide other, StringBuilder log)
        {
            var clip = string.IsNullOrEmpty(target.referenceClipPath) ? null
                : UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(target.referenceClipPath);
            if (clip == null)
                throw new HandInteractionException("REFERENCE_CLIP_NOT_FOUND",
                    $"mano {side.Suffix}: no hay clip de referencia en '{target.referenceClipPath}'");

            var baseFrame = rig.Capture(0f);
            Vector3 baseShoulder = side.Upper.position;
            float baseTwist = HandInteractionRig.TwistDegrees(Quaternion.Inverse(side.Fore.rotation) * side.Hand.rotation);

            float t = Mathf.Clamp(target.referenceTime, 0f, clip.length);
            rig.Sample(clip, t);
            var sol = new HandGripSolution
            {
                Right = side.Right,
                HasArmPose = true,
                HandPosInGrip = rig.GripMesh.InverseTransformPoint(side.Hand.position),
                HandRotInGrip = Quaternion.Inverse(rig.GripMesh.rotation) * side.Hand.rotation,
                Fingers = HandInteractionRig.ReadFingers(side),
                ShoulderShiftWorld = side.Upper.position - baseShoulder,
                Pole = 3,
                PoleWorld = Vector3.ProjectOnPlane(side.Fore.position - side.Upper.position, side.Hand.position - side.Upper.position).normalized,
            };
            Vector3 referenceHandWorld = side.Hand.position;
            rig.Apply(baseFrame);

            // Reproducirla como la reproducirá el horneado: hombro adelantado, IK a la pose, dedos copiados.
            side.Upper.position = baseShoulder + sol.ShoulderShiftWorld;
            bool ok = HandInteractionRig.SolveTwoBone(side, rig.GripMesh.TransformPoint(sol.HandPosInGrip),
                rig.GripMesh.rotation * sol.HandRotInGrip, sol.PoleWorld);
            HandInteractionRig.DistributeForearmTwist(side);
            HandInteractionRig.WriteFingers(side, sol.Fingers);
            var metrics = Measure(rig, side, target, 0f, !ok, sol.ShoulderShiftWorld, baseTwist, other,
                checkFingers: false, checkView: true, checkBodyContact: false);
            metrics.targetErrorMm = (side.Hand.position - rig.GripMesh.TransformPoint(sol.HandPosInGrip)).magnitude * 1000f;
            sol.Metrics = metrics;
            log?.AppendLine($"mano {side.Suffix}: pose copiada de '{clip.name}' en t={t:0.00} s (mano de referencia en {referenceHandWorld}); " +
                            $"hombro adelantado {metrics.shoulderShiftMm:0} mm, IK {(ok ? "exacto" : "RECORTADO")}, antebrazo {metrics.forearmRotationDeg:+0;-0}°, " +
                            $"muñeca {metrics.wristFlexionDeg:+0;-0}°/{metrics.wristDeviationDeg:+0;-0}°, manos a {metrics.handOverlapMm:0} mm; " +
                            $"coste informativo {metrics.cost:0.00} [{string.Join(", ", metrics.costBreakdown)}]");
            rig.Apply(baseFrame);
            return sol;
        }

        public static string PoleName(int pole) => pole switch { 0 => "del clip base", 1 => "abajo-fuera", 2 => "abajo", _ => "de la referencia" };
    }
}
#endif
