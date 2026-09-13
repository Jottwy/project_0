#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using BackroomsSurvival.Gameplay.HandInteraction;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    [Serializable]
    public sealed class HandValidationIssue
    {
        /// <summary>error | warning | info</summary>
        public string severity;
        public string code;
        public string hand;
        public string message;
        public float measured;
        public float threshold;
        /// <summary>Qué cambiar en el perfil (o qué comando lanzar) para arreglarlo.</summary>
        public string hint;
    }

    [Serializable]
    public sealed class HandValidationReport
    {
        public bool ok;
        public int errors, warnings;
        public List<HandValidationIssue> issues = new();
        public List<HandGripMetrics> hands = new();
        /// <summary>Lo que no se puede juzgar midiendo y queda para un humano (mirar las capturas).</summary>
        public List<string> manualChecks = new();
    }

    /// <summary>
    /// Mide el resultado HORNEADO, no el que el solver creía: instancia el prefab tal como está en disco,
    /// muestrea sus clips efectivos (idle al principio y a mitad) y aplica las mismas medidas que la búsqueda.
    /// </summary>
    internal static class HandInteractionValidator
    {
        public static HandValidationReport Validate(HandInteractionProfile profile)
        {
            var report = new HandValidationReport();
            void Issue(string sev, string code, string hand, string msg, float measured = 0f, float threshold = 0f, string hint = "")
                => report.issues.Add(new HandValidationIssue
                {
                    severity = sev, code = code, hand = hand, message = msg, measured = measured, threshold = threshold, hint = hint,
                });

            bool baked = !(string.IsNullOrEmpty(profile.lastBakeUtc) || profile.bakedClips == null || profile.bakedClips.Length == 0 ||
                           profile.bakedClips.Any(c => c == null));
            if (!baked)
                Issue("error", "NOT_BAKED", "", "el perfil no tiene un horneado completo; sólo se mide la mano portadora tal como está",
                    hint: "lanzar el comando 'bake'");

            HandInteractionBaker.Context ctx;
            try { ctx = HandInteractionBaker.Open(profile); }
            catch (HandInteractionException e)
            {
                Issue("error", e.Code, "", e.Message);
                return Finish(report);
            }

            using (ctx)
            {
                var rig = ctx.Rig;
                if (profile.bakedClips != null)
                    foreach (var bakedClip in profile.bakedClips.Where(c => c != null))
                        if (!ctx.Pairs.Any(p => p.effective == bakedClip))
                            Issue("error", "OVERRIDE_NOT_USED", "", $"el clip horneado '{bakedClip.name}' existe pero el wieldable no lo usa",
                                hint: "rehornear con 'bake'; si alguien cambió los overrides a mano, revisar el prefab");

                CheckWarp(rig, report);

                // Las manos con rol Reference se comparan contra su clip: la pose horneada tiene que ser ESA pose.
                var referencePoses = new Dictionary<bool, (Vector3 pos, Quaternion[][] fingers)>();
                foreach (bool right in new[] { true, false })
                {
                    var target = profile.Hand(right);
                    if (target.role != HandRole.Reference || !baked) continue;
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(target.referenceClipPath);
                    if (clip == null)
                    {
                        Issue("error", "REFERENCE_CLIP_NOT_FOUND", right ? "R" : "L", $"no hay clip de referencia en '{target.referenceClipPath}'");
                        continue;
                    }
                    clip.SampleAnimation(rig.Animator.gameObject, Mathf.Clamp(target.referenceTime, 0f, clip.length));
                    rig.Animator.localPosition = Vector3.zero;
                    rig.Animator.localRotation = Quaternion.identity;
                    var side = rig.Side(right);
                    referencePoses[right] = (rig.GripMesh.InverseTransformPoint(side.Hand.position), HandInteractionRig.ReadFingers(side));
                }

                foreach (float fraction in new[] { 0f, 0.5f })
                {
                    ctx.IdleEffective.SampleAnimation(rig.Animator.gameObject, ctx.IdleEffective.length * fraction);
                    rig.Animator.localPosition = Vector3.zero;
                    rig.Animator.localRotation = Quaternion.identity;
                    string when = fraction == 0f ? "idle t=0" : "idle mitad";

                    foreach (bool right in new[] { true, false })
                    {
                        var side = rig.Side(right);
                        var target = profile.Hand(right);
                        rig.UseSurfaceFor(side == ctx.Carrier ? null : target);
                        if (target.role == HandRole.Reference && referencePoses.TryGetValue(right, out var reference))
                        {
                            float driftMm = (rig.GripMesh.InverseTransformPoint(side.Hand.position) - reference.pos).magnitude *
                                            rig.GripMesh.lossyScale.y * 1000f;
                            float worstFinger = 0f;
                            for (int f = 0; f < 5; f++)
                                for (int j = 0; j < 3; j++)
                                    worstFinger = Mathf.Max(worstFinger, Quaternion.Angle(side.Fingers[f][j].localRotation, reference.fingers[f][j]));
                            if (fraction == 0f && (driftMm > 5f || worstFinger > 3f))
                                Issue("error", "REFERENCE_MISMATCH", side.Suffix,
                                    $"{when}: la mano horneada se aparta {driftMm:0.0} mm y {worstFinger:0.0}° de su pose de referencia",
                                    driftMm, 5f, "rehornear; si persiste, subir maxShoulderShiftMeters (el brazo no llega)");
                            var rm = HandGripSolver.Measure(rig, side, target, 0f, false, target.resolvedShoulderShift,
                                HandInteractionRig.TwistDegrees(Quaternion.Inverse(side.Fore.rotation) * side.Hand.rotation), rig.Side(!right),
                                checkFingers: false, checkView: fraction == 0f, checkBodyContact: false);
                            rm.hand = side.Suffix;
                            rm.targetErrorMm = driftMm;
                            if (fraction == 0f) report.hands.Add(rm);
                            if (rm.handOverlapMm < 15f)
                                Issue("error", "HANDS_OVERLAP", side.Suffix, $"{when}: las dos manos se tocan ({rm.handOverlapMm:0} mm)", rm.handOverlapMm, 15f,
                                    "la pose de referencia choca con la portadora en este clip");
                            continue;
                        }
                        if (target.role != HandRole.Grip) continue;
                        // Sin hornear, una secundaria no tiene pose: medirla daría la mano del clip base a 70 cm.
                        if (!baked && side != ctx.Carrier) continue;
                        var other = rig.Side(!right);
                        float baseTwist = HandInteractionRig.TwistDegrees(Quaternion.Inverse(side.Fore.rotation) * side.Hand.rotation);

                        HandGripMetrics m;
                        if (side == ctx.Carrier)
                        {
                            // La portadora: el objetivo es donde está (no se movió); se miden sus dedos. Su torsión
                            // «base» es la suya: no se juzga contra sí misma.
                            var measured = HandGripSolver.MeasuredCarrierTarget(rig, side, target, out float carrierClock);
                            m = HandGripSolver.Measure(rig, side, measured, carrierClock, false, Vector3.zero, baseTwist, null, true, checkView: false);
                        }
                        else
                        {
                            // Se mide en el punto que ELIGIÓ la búsqueda, no en el pedido.
                            var work = JsonUtility.FromJson<HandGripTarget>(JsonUtility.ToJson(target));
                            if (target.resolved) work.alongAxis = target.resolvedAlongAxis;
                            float clock = target.resolved ? target.resolvedClockDegrees : target.clockDegrees;
                            m = HandGripSolver.Measure(rig, side, work, clock, false, target.resolvedShoulderShift, baseTwist, other,
                                true, checkView: fraction == 0f);
                        }
                        m.hand = side.Suffix;
                        if (fraction == 0f) report.hands.Add(m);
                        Judge(report, m, target, side == ctx.Carrier, when, Issue);
                    }
                }
            }

            report.manualChecks.Add("Mirar las capturas (comando 'capture'): la medida no ve si la pose PARECE natural desde el ojo.");
            report.manualChecks.Add("Probar equipar/enfundar en Play: el peso de la secundaria se mezcla por distancia al idle.");
            return Finish(report);
        }

        private static void Judge(HandValidationReport report, HandGripMetrics m, HandGripTarget target, bool carrier, string when,
            Action<string, string, string, string, float, float, string> issue)
        {
            string h = m.hand;
            for (int f = 0; f < 5; f++)
            {
                if (f == 1 && target.fingers == HandFingerStyle.IndexExtended) continue;
                float gap = m.tipGapMm[f];
                string name = new[] { "pulgar", "índice", "corazón", "anular", "meñique" }[f];
                if (gap < -HandGripSolver.TipInside * 1000f - 2f)
                    issue("error", "TIP_INSIDE", h, $"{when}: la yema del {name} está {-gap:0.0} mm DENTRO del objeto", gap, -HandGripSolver.TipInside * 1000f,
                        "subir palmOffsetMeters de esa mano unos milímetros o rehornear con autoSearch");
                else if (gap > HandGripSolver.TipOutside * 1000f + 6f && f != 0)
                    issue("warning", "TIP_FAR", h, $"{when}: la yema del {name} queda a {gap:0.0} mm de la piel (no agarra)", gap, HandGripSolver.TipOutside * 1000f,
                        "bajar palmOffsetMeters, o el objeto es demasiado gordo para rodearlo con esa mano");
            }
            if (m.worstFingerPenetrationMm > 4f)
                issue("error", "FINGER_PENETRATION", h, $"{when}: la falange '{m.worstFingerJoint}' atraviesa el objeto {m.worstFingerPenetrationMm:0.0} mm",
                    m.worstFingerPenetrationMm, 4f, "rehornear; si persiste, mover el objetivo (alongAxis/clockDegrees) lejos de piezas obstáculo");
            if (m.thumbPenetrationMm > 4f)
                issue("error", "THUMB_PENETRATION", h, $"{when}: el pulgar entra {m.thumbPenetrationMm:0.0} mm en el objeto", m.thumbPenetrationMm, 4f,
                    "fingers=ThumbAlongAxis o subir palmOffsetMeters");
            if (m.palmPenetrationMm > 3f)
                issue("error", "PALM_PENETRATION", h, $"{when}: la palma entra {m.palmPenetrationMm:0.0} mm en el objeto", m.palmPenetrationMm, 3f,
                    "subir palmOffsetMeters");
            if (m.fingersOverEnd > 0)
                issue("warning", "FINGER_OVER_END", h, $"{when}: {m.fingersOverEnd} yema(s) cruzan la cara de un extremo del objeto (tapan la punta/lente)",
                    m.fingersOverEnd, 0f, "nudge direction=tail (o tip si es la culata) unos milímetros");
            if (m.wrapCoverageDeg + 30f < m.wrapCoverageWantedDeg)
                issue("warning", "NOT_WRAPPING", h, $"{when}: los dedos recorren {m.wrapCoverageDeg:0}° alrededor del eje (se esperaban ~{m.wrapCoverageWantedDeg:0}°): toca pero no rodea",
                    m.wrapCoverageDeg, m.wrapCoverageWantedDeg, "bajar palmOffsetMeters o cambiar tiltDegrees");
            if (carrier) return;

            if (m.reachClamped)
                issue("error", "REACH_CLAMPED", h, $"{when}: el brazo no llega a su objetivo", 1f, 0f,
                    "subir maxShoulderShiftMeters o acercar el objetivo (alongAxis hacia la culata)");
            if (Mathf.Abs(m.wristFlexionDeg) > 70f || Mathf.Abs(m.wristDeviationDeg) > 35f)
                issue("error", "WRIST_LIMIT", h, $"{when}: muñeca en el tope (flexión {m.wristFlexionDeg:0}°, desviación {m.wristDeviationDeg:0}°)",
                    Mathf.Max(Mathf.Abs(m.wristFlexionDeg), Mathf.Abs(m.wristDeviationDeg)), 70f, "cambiar clockDegrees (el lado desde el que llega la mano)");
            else if (Mathf.Abs(m.forearmRotationDeg) > 110f)
                issue("error", "FOREARM_LIMIT", h, $"{when}: antebrazo girado {m.forearmRotationDeg:0}° (0 = mano de apretón, + = palma arriba)",
                    m.forearmRotationDeg, 110f, "cambiar clockDegrees o indexTowardTip");
            else if (m.forearmRotationDeg > 45f)
                issue("warning", "PALM_UP", h, $"{when}: palma hacia arriba ({m.forearmRotationDeg:0}° de supinación): se ve retorcida", m.forearmRotationDeg, 45f,
                    "probar clockDegrees con la palma más de lado, o indexTowardTip al revés");
            else if (m.costBreakdown.Any(c => c.StartsWith("extensión") || c.StartsWith("flexión") || c.StartsWith("cubital") || c.StartsWith("radial") || c.StartsWith("pronación")))
                issue("warning", "WRIST_UNCOMFORTABLE", h, $"{when}: muñeca fuera de su rango cómodo ({string.Join(", ", m.costBreakdown.Where(c => !c.StartsWith("yema")))})",
                    m.cost, 0f, "probar otro clockDegrees o tiltDegrees con autoSearch");
            if (m.elbowAngleDeg < 55f || m.elbowAngleDeg > 170f)
                issue("warning", "ELBOW_POSE", h, $"{when}: codo a {m.elbowAngleDeg:0}°", m.elbowAngleDeg, 55f, "cambiar alongAxis o maxShoulderShiftMeters");
            if (m.handOverlapMm < 15f)
                issue("error", "HANDS_OVERLAP", h, $"{when}: las dos manos se tocan ({m.handOverlapMm:0} mm entre articulaciones)", m.handOverlapMm, 15f,
                    "separar alongAxis de las dos manos");
            if (m.palmGapMm > 22f)
                issue("warning", "PALM_FAR", h, $"{when}: el nudillo más cercano queda a {m.palmGapMm:0} mm de la piel: sostiene con las yemas", m.palmGapMm, 22f,
                    "nudge direction=in, o rehornear con autoSearch");
            if (m.targetErrorMm > 18f)
                issue("warning", "TARGET_MISSED", h, $"{when}: el hueco del puño queda a {m.targetErrorMm:0} mm del eje en su punto", m.targetErrorMm, 18f,
                    "rehornear; si no cambia, el objetivo no es alcanzable con esa orientación");
            if (m.viewAngleDeg > 45f)
                issue("warning", "HAND_OUT_OF_VIEW", h, $"{when}: la mano está a {m.viewAngleDeg:0}° del centro de la vista", m.viewAngleDeg, 45f,
                    "mover el agarre hacia la punta del objeto");
            if (m.shoulderShiftMm > 250f)
                issue("warning", "SHOULDER_SHIFT", h, $"hombro adelantado {m.shoulderShiftMm:0} mm", m.shoulderShiftMm, 250f,
                    "puede verse en equipar/enfundar: revisar las capturas");
        }

        /// <summary>Regla 14 de CLAUDE.md (ADR-077 enm. 2): lo que cuelga de los brazos 1P warpea.</summary>
        private static void CheckWarp(HandInteractionRig rig, HandValidationReport report)
        {
            foreach (var r in rig.Node.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    if (mat.shader.name.Contains("FieldOfView") || mat.shader.name.Contains("UIWarp")) continue;
                    report.issues.Add(new HandValidationIssue
                    {
                        severity = "error", code = "VIEWMODEL_WARP", hand = "",
                        message = $"'{r.name}' usa '{mat.shader.name}' en el viewmodel: sale 1,5× más grande que la mano",
                        hint = "menú Backrooms/Viewmodel/Rewarp held items (ADR-077 enm. 2)",
                    });
                }
        }

        private static HandValidationReport Finish(HandValidationReport report)
        {
            // Una sola entrada por código y mano: t=0 y la mitad del idle suelen repetir lo mismo.
            report.issues = report.issues
                .GroupBy(i => (i.code, i.hand))
                .Select(g => g.OrderBy(i => i.severity == "error" ? 0 : i.severity == "warning" ? 1 : 2).First())
                .ToList();
            report.errors = report.issues.Count(i => i.severity == "error");
            report.warnings = report.issues.Count(i => i.severity == "warning");
            report.ok = report.errors == 0;
            return report;
        }
    }
}
#endif
