#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-133 enm. 1 — hornea las ANIMACIONES PROPIAS de la linterna de manivela: idle, equipar y
    /// enfundar con la mano derecha agarrando el tubo, y la cuerda con la izquierda sobre el pomo.
    /// Se ejecuta desde "Backrooms ▸ Linterna ▸ Hornear animaciones" y lo llama también el
    /// aplicador del modelo al terminar, para que rehacer el modelo rehaga la mano.
    ///
    /// POR QUÉ CLIPS HORNEADOS Y NO UN FBX ANIMADO A MANO. Los clips del vendor son de una
    /// antorcha: el puño a la altura del ojo, los dedos cerrados sobre un palo de 3 cm, la
    /// izquierda colgando. Joel pidió tres cosas que ningún offset da: la mano más baja y natural,
    /// los dedos «al milímetro» sobre ESTE cuerpo de 6 cm, y la otra mano dando vueltas a la
    /// manivela. Las tres son geometría medible sobre las mallas que ya hay —el tubo tiene un radio
    /// por tramo, el pomo describe un círculo conocido—, así que se RESUELVEN en vez de dibujarse:
    /// IK de dos huesos para llevar cada mano donde toca y un ajuste de contacto por falange que
    /// cierra cada dedo hasta tocar la superficie y ni un milímetro más. Si el modelo cambia
    /// (remesh pendiente), se rehornea y los dedos vuelven a caer sobre la piel nueva.
    ///
    /// LA MANO DERECHA SE LLEVA DEL VENDOR, no se inventa. Su idle ya sostiene la antorcha
    /// apuntando al frente en agarre de martillo; lo que se le hace es un desplazamiento RÍGIDO
    /// (puño + tubo como una pieza) hasta la posición de diseño y el reapuntado exacto de la
    /// lente, fotograma a fotograma, conservando su balanceo. Después el brazo se recalcula por IK
    /// desde el hombro, que no se mueve. Así equipar y enfundar siguen siendo los movimientos del
    /// vendor, sólo que terminan donde ahora vive el puño.
    ///
    /// EL MODELO PASA A COLGAR DE <c>Hand.R</c>: puño y tubo son una pieza rígida, y en cualquier
    /// clip (los del vendor incluidos) la linterna va donde va la mano. Colgarla del hueso
    /// `Torch` valía mientras la animación era la del vendor; con clips propios sería animar dos
    /// cosas para que coincidan.
    ///
    /// EL RELOJ DE LA MANIVELA. El pomo barre un círculo de 6 cm en un plano a 4,4 cm del eje del
    /// tubo, y ese plano corta el puño si el costado de la manivela cae donde están el pulgar
    /// (arriba) o los dedos (abajo-izquierda). Se ROTA EL TUBO sobre su eje —el tubo es simétrico,
    /// la mano no— hasta poner la manivela hacia arriba-izquierda, entre las dos, y se MIDE la
    /// holgura mínima de la órbita contra cada falange en el log y en el test.
    /// </summary>
    public static class BackroomsCrankFlashlightPoseBaker
    {
        public const string AnimFolder = BackroomsCrankFlashlightModelApplier.BakedFolder + "/Anim";
        public const string IdleClipPath = AnimFolder + "/BR_CrankFlashlight_Idle.anim";
        public const string EquipClipPath = AnimFolder + "/BR_CrankFlashlight_Equip.anim";
        public const string HolsterClipPath = AnimFolder + "/BR_CrankFlashlight_Holster.anim";
        public const string CrankClipPath = AnimFolder + "/BR_CrankFlashlight_Crank.anim";
        public const string ControllerPath = AnimFolder + "/BR_CrankFlashlight.controller";

        /// <summary>Capa y estado de la cuerda en el controller. Los lee <c>CrankFlashlightWieldable</c>.</summary>
        public const string CrankLayerName = "Crank";
        public const string CrankStateName = "Crank";

        private const string TemplateControllerPath =
            "Assets/PolymindGames/FPSCore/Art/Animations/Wieldables/Controllers/Template_Tool.controller";

        private const string PrefabPath = BackroomsCrankFlashlightCreator.PrefabPath;
        private const string AnimatorNodeName = "ViewModel";
        private const string RootBoneName = "Root";
        private const string CameraBoneName = "Camera";
        private const float FrameRate = 30f;

        // ─── DISEÑO: los números que se afinan con la captura ───────────────────────────────────

        /// <summary>
        /// Dónde queda el PUÑO derecho respecto del ojo, en espacio del root (x derecha, y arriba,
        /// z delante). El vendor lo tiene en (0,33, −0,01, 0,49): a la altura del ojo y en el
        /// borde, que es lo que Joel llamó «muy arriba, no orgánico». Referencia: la foto de una
        /// mano con linterna — el puño abajo a la derecha, el antebrazo entrando desde abajo.
        /// </summary>
        private static readonly Vector3 FistFromEye = new(0.10f, -0.11f, 0.36f);

        /// <summary>Cabeceo de la lente bajo el horizonte del jugador. Una linterna se lleva
        /// apuntando al suelo unos metros por delante, no al horizonte.</summary>
        private const float LensPitchDownDegrees = 8f;

        /// <summary>Guiñada de la lente: positivo, hacia la derecha. Hacia dentro: el haz cruza la
        /// mirada a pocos metros en vez de correr paralelo a ella, y el cuerpo se ve de tres
        /// cuartos en vez de por la culata, como en la foto de referencia. El cono de 55° del haz
        /// sigue cubriendo el centro de la pantalla.</summary>
        private const float LensYawDegrees = -8f;

        /// <summary>
        /// El ALABEO del puño sobre el eje del tubo no se elige: se BARRE. La rotación mínima que
        /// lleva el mango de la antorcha a la lente deja la muñeca con la torsión que le salga, y
        /// probando +60° a ojo salió la mano boca abajo con 155° de giro mano/antebrazo. Se prueba
        /// cada alabeo de −180 a 180 y se toma el que deja la torsión de la muñeca (giro sobre el
        /// eje del antebrazo) más parecida a la que el vendor animó para la antorcha, que es la
        /// única muñeca «cómoda» de la que hay prueba, con el pulgar en la mitad de arriba: una
        /// linterna se lleva con el pulgar encima. Este paso es el límite del barrido.
        /// </summary>
        private const float RollScanStepDegrees = 5f;

        /// <summary>
        /// Hacia dónde mira el COSTADO de la manivela: al CENTRO DEL ARCO LIBRE alrededor del tubo,
        /// medido sobre la mano ya cerrada (el mayor hueco angular sin ninguna falange). Se pedía
        /// a 50° a la izquierda de arriba, pero con el pulgar tendido por ese lado el brazo de la
        /// manivela en reposo lo atravesaba: el pulgar va donde lo deja la muñeca del vendor y la
        /// manivela va donde no hay mano. Este valor sólo es la reserva si no se puede medir.
        /// </summary>
        private const float CrankSideDegreesLeftOfUpFallback = 50f;

        /// <summary>
        /// HOLGURA DE LA PALMA: la distancia mínima que se exige entre el hueso de un nudillo (la
        /// base de cada dedo) y la piel del tubo. La mano del vendor cierra sobre un palo de 3 cm y
        /// con el tubo de 6 el nudillo del índice caía a 1 mm de la superficie, es decir, con
        /// 7 mm de carne dentro del tubo. Si algún nudillo queda más cerca que esto, el tubo se
        /// aparta de la palma lo que falte, en la normal de la palma.
        /// </summary>
        private const float PalmClearance = FingerSkin;

        /// <summary>A qué fracción del largo, desde la culata, cierra el CENTRO DE LA PILA DE DEDOS.
        /// 0,48: por el centro; el pulgar, que se tiende 8 cm hacia la lente desde la pila, acaba
        /// justo en el bisel (con 0,55 asomaba 1,4 cm por delante de la lente). La culata asoma
        /// por detrás de la muñeca, como en la foto de referencia.</summary>
        private const float FistFromTail = 0.48f;

        /// <summary>Radio de una falange: la distancia del hueso a la piel. Es lo que separa el eje
        /// del dedo de la superficie del tubo cuando el dedo la TOCA.</summary>
        private const float FingerSkin = 0.0085f;
        private const float ThumbSkin = 0.0105f;

        /// <summary>Dirección del codo (polo del IK), en espacio del root. Abajo y hacia fuera: los
        /// codos cuelgan, no apuntan al cielo.</summary>
        private static readonly Vector3 ElbowPoleRight = new(0.7f, -1f, -0.4f);
        private static readonly Vector3 ElbowPoleLeft = new(-0.7f, -1f, -0.4f);

        /// <summary>
        /// El HOMBRO izquierdo mientras da cuerda, respecto del ojo. El del vendor cuelga 42 cm por
        /// detrás del ojo (la izquierda está fuera de plano) y desde ahí el pomo no se alcanza: 58
        /// cm contra 46 de brazo. Un rig de brazos en primera persona no tiene torso: el hombro se
        /// adelanta y nadie lo ve. Se anima (es una curva de posición), así que la transición lo
        /// lleva y lo trae.
        /// </summary>
        private static readonly Vector3 LeftShoulderFromEyeWhileCranking = new(-0.24f, -0.22f, 0.22f);

        /// <summary>Dónde entra el pomo en la mano izquierda, en local de <c>Hand.L</c>: a 7,2 cm de
        /// la muñeca por los metacarpos —justo detrás de los nudillos, que están a 8— y 2 cm hacia
        /// la palma (la palma es −Z). Con 5,5 la primera pasada cerraba el puño DEBAJO del pomo.</summary>
        internal static readonly Vector3 LeftGripPointLocal = new(0f, 0.072f, -0.020f);

        /// <summary>Hacia dónde apuntan los metacarpos de la izquierda al agarrar el pomo, en
        /// espacio del root: adelante y un poco abajo, con la palma vuelta hacia el tubo.</summary>
        private static readonly Vector3 LeftFingersDirection = new(0.25f, -0.35f, 0.90f);

        /// <summary>Bamboleo del tubo por vuelta mientras se da cuerda: el esfuerzo de la izquierda
        /// se nota en la derecha. Grados de cabeceo y de alabeo, sobre el puño.</summary>
        private const float CrankWobblePitchDegrees = 1.2f;
        private const float CrankWobbleRollDegrees = 0.8f;

        /// <summary>Fotogramas por vuelta del clip de cuerda. Su duración es 1/vueltas·s⁻¹ del
        /// componente, para que la fase del Animator y el ángulo de la manivela sean lo mismo.</summary>
        private const int CrankFrames = 30;

        // ─────────────────────────────────────────────────────────────────────────────────────────

        [MenuItem("Backrooms/Linterna/Hornear animaciones", false, 93)]
        public static void BakeFromMenu()
        {
            Bake();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>Hornea los cuatro clips, el controller y deja el prefab apuntando a ellos.</summary>
        public static bool Bake()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[CrankFlashlightPose] No hay prefab en '{PrefabPath}'.");
                return false;
            }

            BackroomsEditorFolders.EnsureFolder(AnimFolder);

            // SE MIDE SOBRE UNA INSTANCIA EN ESCENA y se escribe en el prefab después: muestrear
            // clips es cosa de escena (AnimationMode), y las cifras que salen —un offset, cuatro
            // clips— son las que se aplican al asset. Nada de lo que se toca en la instancia
            // sobrevive a este método.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (t.name == AnimatorNodeName || t.name == RootBoneName) t.gameObject.SetActive(true);

            Result result;
            try
            {
                result = Solve(instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[CrankFlashlightPose] Horneado abortado: " + e);
                Object.DestroyImmediate(instance);
                return false;
            }
            finally
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            }
            Object.DestroyImmediate(instance);

            if (result == null) return false;

            var idle = SaveClip(result.Idle, IdleClipPath, "BR_CrankFlashlight_Idle", loop: true);
            var equip = SaveClip(result.Equip, EquipClipPath, "BR_CrankFlashlight_Equip", loop: false);
            var holster = SaveClip(result.Holster, HolsterClipPath, "BR_CrankFlashlight_Holster", loop: false);
            var crank = SaveClip(result.Crank, CrankClipPath, "BR_CrankFlashlight_Crank", loop: true);
            var controller = EnsureController(crank);

            WritePrefab(result, controller, idle, equip, holster, crank);
            Debug.Log("[CrankFlashlightPose] " + result.Report);
            return true;
        }

        // ─── El solver ───────────────────────────────────────────────────────────────────────────

        private sealed class Result
        {
            public AnimationClip Idle, Equip, Holster, Crank;
            public Vector3 NodeLocalPosition;
            public Quaternion NodeLocalRotation;
            public Vector3 NodeLocalScale;
            public string Report;
        }

        private sealed class Rig
        {
            public Transform Animator, Root, Camera;
            public Transform HandR, ForearmR, UpperArmR, HandL, ForearmL, UpperArmL;
            public Transform[] TwistR, TwistL;
            public Transform Node, Body, Crank;
            public Mesh BodyMesh, CrankMesh;
            public Transform[] Skeleton;
            public string[] Paths;
            public Dictionary<string, Transform> ByName;
            public Transform[][] FingersR, FingersL; // [dedo][falange]
            public float[] TipLengthR, TipLengthL;
            public float[] RadiusProfile; public float ProfileMinY, ProfileMaxY;
            public Vector3 KnobLocal; public float KnobRadius;
        }

        private static Result Solve(GameObject instance)
        {
            var rig = ReadRig(instance);
            // El nodo del modelo cuelga de Hand.R DESDE AQUÍ: el aplicador lo deja bajo el hueso de
            // la antorcha, y todo lo que sigue —pose, dedos, pomo— lo mide como hijo de la mano.
            rig.Node.SetParent(rig.HandR, false);
            rig.Node.localPosition = Vector3.zero;
            rig.Node.localRotation = Quaternion.identity;
            rig.Node.localScale = Vector3.one;
            var vendor = FindVendorClips(rig);
            if (vendor.Idle == null || vendor.Equip == null || vendor.Holster == null)
            {
                Debug.LogError("[CrankFlashlightPose] Faltan clips de la antorcha del vendor (idle/equip/holster) " +
                               "en el FBX del avatar. Nada horneado.");
                return null;
            }

            var report = new StringBuilder();
            AnimationMode.StartAnimationMode();

            // ── 1. La pose de referencia: idle del vendor, fotograma 0 ──
            Sample(rig, vendor.Idle, 0f);
            if (!BackroomsCrankFlashlightModelApplier.TryReadTorchHandle(instance, rig.HandR,
                    out var torchBone, out var axisLocal, out var fistLocal))
            {
                Debug.LogError("[CrankFlashlightPose] No se puede leer el mango de la antorcha: sin él no hay " +
                               "agarre del que partir.");
                return null;
            }
            Vector3 handleAxis = torchBone.TransformDirection(axisLocal).normalized;
            Vector3 vendorFist = torchBone.TransformPoint(fistLocal);

            Vector3 eye = rig.Camera.position;
            Vector3 fist = eye + rig.Root.TransformDirection(FistFromEye);
            Vector3 lens = rig.Root.TransformDirection(
                Quaternion.Euler(LensPitchDownDegrees, LensYawDegrees, 0f) * Vector3.forward);

            // El desplazamiento RÍGIDO puño+tubo: gira el mango del vendor hasta la lente de diseño
            // y lleva su puño al puño de diseño. Se aplica igual a todos los fotogramas de todos
            // los clips, así que equipar y enfundar conservan su recorrido.
            var poleR = rig.Root.TransformDirection(ElbowPoleRight);
            var poleL = rig.Root.TransformDirection(ElbowPoleLeft);
            float vendorTwist = TwistDegrees(Quaternion.Inverse(rig.ForearmR.rotation) * rig.HandR.rotation);
            var rigid = ChooseRoll(rig, vendor.Idle, handleAxis, lens, vendorFist, fist, poleR, vendorTwist, report);
            report.AppendLine($"mango del vendor {Fmt(rig.Root.InverseTransformDirection(handleAxis))} → lente " +
                              $"{Fmt(rig.Root.InverseTransformDirection(lens))}; puño del vendor " +
                              $"{Fmt(rig.Root.InverseTransformPoint(vendorFist) - rig.Root.InverseTransformPoint(eye))} → " +
                              $"{Fmt(FistFromEye)} respecto del ojo.");

            // EL CENTRO DEL PUÑO ES EL DE LOS DEDOS, no el origen del hueso de la antorcha: medido,
            // los cuatro dedos se apilan de +2 a +10 cm por delante de ese origen a lo largo del
            // eje (índice hacia la lente, meñique hacia la culata), así que el tubo se corre para
            // que la PILA quede a FistFromTail y no el origen. Con el origen, el índice cerraba
            // más allá del bisel.
            float fingerStackOffset;
            {
                var hand0 = MoveRigid(rig.HandR, rigid, vendorFist, fist);
                float sum = 0f;
                for (int f = 1; f < rig.FingersR.Length; f++)
                {
                    Vector3 p = hand0.pos + hand0.rot * rig.HandR.InverseTransformPoint(rig.FingersR[f][1].position);
                    sum += Vector3.Dot(p - fist, lens);
                }
                fingerStackOffset = sum / (rig.FingersR.Length - 1);
            }
            report.AppendLine($"la pila de dedos queda {fingerStackOffset * 100f:+0.0} cm por delante del origen del hueso; el tubo se corre lo mismo.");

            // ── 2. El tubo en el puño: pose de diseño del nodo del modelo, y su offset bajo Hand.R ──
            // Dos pasadas: la primera coloca el tubo con la manivela en la reserva, cierra los
            // dedos y MIDE (holgura de la palma, arco libre); la segunda corrige el centro del tubo
            // y gira el tubo sobre su eje para poner la manivela en el arco libre.
            Vector3 up = rig.Root.up;
            Vector3 lateral = Vector3.ProjectOnPlane(Quaternion.AngleAxis(CrankSideDegreesLeftOfUpFallback, lens) *
                                Vector3.ProjectOnPlane(up, lens).normalized, lens).normalized;
            Vector3 nodeCentre = fist + lens * (fingerStackOffset +
                BackroomsCrankFlashlightModelApplier.BodyLengthMeters * (0.5f - FistFromTail));
            {
                var probeHand = MoveRigid(rig.HandR, rigid, vendorFist, fist);
                SolveTwoBone(rig.UpperArmR, rig.ForearmR, rig.HandR, probeHand.pos, probeHand.rot, poleR, rightSide: true);
                PlaceNode(rig, nodeCentre, lens, lateral);
                FitRightHandToTube(rig, new FitStats());

                // Holgura de la palma: el nudillo más cercano manda.
                float minKnuckleGap = float.MaxValue;
                for (int f = 1; f < rig.FingersR.Length; f++)
                    minKnuckleGap = Mathf.Min(minKnuckleGap, TubeGap(rig, rig.FingersR[f][0].position));
                Vector3 palmNormal = -rig.HandR.forward; // −Z de la mano: hacia donde cierran los dedos
                Vector3 away = Vector3.ProjectOnPlane(palmNormal, lens).normalized;
                float push = Mathf.Max(0f, PalmClearance - minKnuckleGap);
                nodeCentre += away * push;
                report.AppendLine($"nudillo más cercano a {minKnuckleGap * 1000f:0.0} mm de la piel: el tubo se aparta {push * 1000f:0.0} mm de la palma.");

                // Y el arco libre, con el tubo ya apartado y los dedos cerrados otra vez.
                PlaceNode(rig, nodeCentre, lens, lateral);
                FitRightHandToTube(rig, new FitStats());
                float afterGap = float.MaxValue;
                for (int f = 1; f < rig.FingersR.Length; f++)
                    afterGap = Mathf.Min(afterGap, TubeGap(rig, rig.FingersR[f][0].position));
                report.AppendLine($"tras apartarlo, el nudillo más cercano queda a {afterGap * 1000f:0.0} mm de la piel.");
                if (TryFreeArc(rig, out float arcDegrees, out float arcCentreClock))
                {
                    Vector3 upRef = Vector3.ProjectOnPlane(up, lens).normalized;
                    // El reloj crece hacia la derecha; el giro positivo sobre +Z lleva arriba → izquierda.
                    lateral = Vector3.ProjectOnPlane(Quaternion.AngleAxis(-arcCentreClock, lens) * upRef, lens).normalized;
                    report.AppendLine($"arco libre de {arcDegrees:0}° centrado a las {arcCentreClock:0}° del tubo: la manivela va ahí.");
                }
                Sample(rig, vendor.Idle, 0f);
            }
            Vector3 nodeForward = Vector3.Cross(lateral, lens).normalized;
            var nodeRotation = Quaternion.LookRotation(nodeForward, lens);

            // Hand.R de diseño en el fotograma 0: la del vendor, desplazada. El offset del nodo bajo
            // la mano se CALCULA, no se lee de la jerarquía: la primera pasada lo leyó tras mover
            // transforms bajo AnimationMode y salió a 26 cm de la mano (la segunda, 7). Con la
            // pose de la mano y la del tubo en números, el offset es una resta y no depende de
            // qué haya escrito el muestreo entre medias.
            var handPose0 = MoveRigid(rig.HandR, rigid, vendorFist, fist);
            var boneScale = rig.HandR.lossyScale;
            float boneFactor = Mathf.Max(1e-5f, Mathf.Max(boneScale.x, Mathf.Max(boneScale.y, boneScale.z)));
            var invHand = Quaternion.Inverse(handPose0.rot);
            var result = new Result
            {
                NodeLocalPosition = invHand * (nodeCentre - handPose0.pos) / boneFactor,
                NodeLocalRotation = invHand * nodeRotation,
                NodeLocalScale = Vector3.one / boneFactor,
            };
            rig.Node.localPosition = result.NodeLocalPosition;
            rig.Node.localRotation = result.NodeLocalRotation;
            rig.Node.localScale = result.NodeLocalScale;
            rig.HandR.SetPositionAndRotation(handPose0.pos, handPose0.rot);
            report.AppendLine($"nodo bajo Hand.R: pos {Fmt(result.NodeLocalPosition)} rot {Fmt(result.NodeLocalRotation.eulerAngles)}; " +
                              $"costado de la manivela hacia {Fmt(rig.Root.InverseTransformDirection(lateral))}.");
            report.AppendLine($"puesta: ojo {Fmt(eye)}, puño {Fmt(fist)}, Hand.R {Fmt(handPose0.pos)}, centro del tubo " +
                              $"{Fmt(nodeCentre)}; tubo a {(nodeCentre - handPose0.pos).magnitude:F3} m de la muñeca.");

            // ── 3. Los tres clips del vendor, reposicionados ──
            var idleFrames = new List<Frame>();
            var stats = new FitStats();
            // EL AGARRE SE RESUELVE UNA VEZ y se repite en cada fotograma de los tres clips: puño y
            // tubo son una pieza rígida, así que la relación dedo-tubo no cambia con el balanceo ni
            // al equipar. Ajustarlo por fotograma daba lo mismo… con paso de 1°, y ese grado de
            // diferencia entre fotogramas vecinos es tembleque en las falanges. Además deja las
            // curvas de los dedos CONSTANTES, que es lo que las hace comprimibles (ver BuildClip).
            var grip = new RightGrip();

            result.Idle = BakeVendorClip(rig, vendor.Idle, rigid, vendorFist, fist, poleR, stats, grip, idleFrames, loop: true);
            result.Equip = BakeVendorClip(rig, vendor.Equip, rigid, vendorFist, fist, poleR, stats, grip, null, loop: false);
            result.Holster = BakeVendorClip(rig, vendor.Holster, rigid, vendorFist, fist, poleR, stats, grip, null, loop: false);
            report.AppendLine($"idle {result.Idle.length:F2} s, equipar {result.Equip.length:F2} s, enfundar " +
                              $"{result.Holster.length:F2} s; alcance del IK derecho recortado en {stats.ReachClamps} " +
                              $"fotogramas; dedos: hueco medio {stats.MeanGap * 1000f:F1} mm, peor penetración " +
                              $"{stats.WorstPenetration * 1000f:F1} mm, peor hueco {stats.WorstGap * 1000f:F1} mm.");
            report.Append(stats.IdleDescription);

            // ── 4. La cuerda: la derecha del idle-0 con bamboleo, la izquierda sobre el pomo ──
            float rps = ReadRevolutionsPerSecond(instance);
            result.Crank = BakeCrankClip(rig, idleFrames[0], fist, lens, lateral, poleR, poleL, rps, report);

            AnimationMode.StopAnimationMode();
            result.Report = report.ToString();
            return result;
        }

        private struct Pose { public Vector3 pos; public Quaternion rot; }

        private static Pose MoveRigid(Transform t, Quaternion rigid, Vector3 pivotFrom, Vector3 pivotTo)
            => new() { pos = pivotTo + rigid * (t.position - pivotFrom), rot = rigid * t.rotation };

        /// <summary>Un fotograma resuelto: la pose local de cada hueso del esqueleto.</summary>
        private sealed class Frame
        {
            public float Time;
            public Vector3[] Pos;
            public Quaternion[] Rot;
        }

        private sealed class FitStats
        {
            public string IdleDescription = "";
            public int ReachClamps;
            public float SumGap; public int GapCount;
            public float WorstPenetration; public float WorstGap;
            public float MeanGap => GapCount > 0 ? SumGap / GapCount : 0f;
            public void Add(float gap)
            {
                SumGap += gap; GapCount++;
                if (-gap > WorstPenetration) WorstPenetration = -gap;
                if (gap > WorstGap) WorstGap = gap;
            }
        }

        /// <summary>Las rotaciones locales de las quince falanges derechas, resueltas una vez.</summary>
        private sealed class RightGrip
        {
            public Quaternion[][] Joints;
        }

        private static AnimationClip BakeVendorClip(Rig rig, AnimationClip source, Quaternion rigid, Vector3 vendorFist,
            Vector3 fist, Vector3 poleR, FitStats stats, RightGrip grip, List<Frame> keep, bool loop)
        {
            int frames = Mathf.Max(1, Mathf.RoundToInt(source.length * FrameRate));
            var list = new List<Frame>(frames + 1);
            for (int k = 0; k <= frames; k++)
            {
                float t = Mathf.Min(source.length, k / FrameRate);
                Sample(rig, source, loop && k == frames ? 0f : t);

                // La derecha: puño desplazado en bloque y brazo por IK desde el hombro del vendor.
                var hand = MoveRigid(rig.HandR, rigid, vendorFist, fist);
                if (!SolveTwoBone(rig.UpperArmR, rig.ForearmR, rig.HandR, hand.pos, hand.rot, poleR, rightSide: true))
                    stats.ReachClamps++;
                DistributeForearmTwist(rig.ForearmR, rig.HandR, rig.TwistR);

                // Los dedos derechos: del puño del vendor, cerrados hasta tocar el tubo — la primera
                // vez; después, el mismo agarre en todos los fotogramas.
                if (grip.Joints == null)
                {
                    FitRightHandToTube(rig, stats);
                    grip.Joints = rig.FingersR.Select(chain => chain.Select(j => j.localRotation).ToArray()).ToArray();
                }
                else
                {
                    for (int f = 0; f < rig.FingersR.Length; f++)
                        for (int j = 0; j < rig.FingersR[f].Length; j++)
                            rig.FingersR[f][j].localRotation = grip.Joints[f][j];
                }

                if (k == 0 && keep != null)
                    stats.IdleDescription = DescribeRightHandOnTube(rig);

                var frame = Capture(rig, t);
                list.Add(frame);
                keep?.Add(frame);
            }
            return BuildClip(rig, list, source.length, loop);
        }

        private static AnimationClip BakeCrankClip(Rig rig, Frame idle0, Vector3 fist, Vector3 lens, Vector3 lateral,
            Vector3 poleR, Vector3 poleL, float rps, StringBuilder report)
        {
            float length = 1f / Mathf.Max(0.05f, rps);
            var list = new List<Frame>(CrankFrames + 1);
            var eye = rig.Camera.position;
            var leftShoulder = eye + rig.Root.TransformDirection(LeftShoulderFromEyeWhileCranking);
            Vector3 upRef = Vector3.ProjectOnPlane(rig.Root.up, lens).normalized;
            Vector3 rightRef = Vector3.Cross(upRef, lens).normalized;

            float worstGripError = 0f, worstOrbitClearance = float.MaxValue, minLeftReach = float.MaxValue, maxLeftReach = 0f;
            int leftClamps = 0;
            float armLength = rig.ForearmL.localPosition.magnitude + rig.HandL.localPosition.magnitude;

            // EL PUNTO DE AGARRE SE MIDE, NO SE DECLARA. `LeftGripPointLocal` es sólo la semilla: se
            // pone la mano con ella, se cierran los dedos sobre el pomo y se lee dónde queda el
            // CENTRO de los dedos cerrados en local de la mano; con ese punto se vuelve a poner la
            // mano, y a la tercera vuelta el pomo está donde los dedos cierran. Con la constante a
            // secas el puño cerraba 2 cm por debajo del pomo.
            Vector3 gripLocal = LeftGripPointLocal;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Apply(rig, idle0);
                rig.Crank.localRotation = BackroomsCrankFlashlightModelApplier.CrankLocalRotation;
                Vector3 knob0 = rig.Crank.TransformPoint(rig.KnobLocal);
                Vector3 peg0 = rig.Crank.TransformDirection(BackroomsCrankFlashlightModelApplier.CrankSpinAxis).normalized;
                if (Vector3.Dot(peg0, lateral) < 0f) peg0 = -peg0;
                Vector3 fingers0 = rig.Root.TransformDirection(LeftFingersDirection);
                var rot0 = Quaternion.LookRotation(peg0, Vector3.ProjectOnPlane(fingers0, peg0).normalized);
                rig.UpperArmL.position = leftShoulder;
                SolveTwoBone(rig.UpperArmL, rig.ForearmL, rig.HandL, knob0 - rot0 * gripLocal, rot0, poleL, rightSide: false);
                FitLeftHandToPeg(rig, knob0, peg0);
                gripLocal = rig.HandL.InverseTransformPoint(EnclosedCentre(rig.FingersL, rig.TipLengthL));
            }
            report.AppendLine($"agarre izquierdo medido en local de Hand.L: {Fmt(gripLocal)} (semilla {Fmt(LeftGripPointLocal)}).");

            for (int k = 0; k <= CrankFrames; k++)
            {
                float phase = (k % CrankFrames) / (float)CrankFrames;
                float theta = phase * 360f;
                Apply(rig, idle0);

                // La derecha: el idle con un bamboleo por vuelta, girado sobre el puño.
                var wobble = Quaternion.AngleAxis(CrankWobblePitchDegrees * Mathf.Sin(theta * Mathf.Deg2Rad), rightRef) *
                             Quaternion.AngleAxis(CrankWobbleRollDegrees * Mathf.Cos(theta * Mathf.Deg2Rad), lens);
                var handR = new Pose
                {
                    pos = fist + wobble * (rig.HandR.position - fist),
                    rot = wobble * rig.HandR.rotation,
                };
                SolveTwoBone(rig.UpperArmR, rig.ForearmR, rig.HandR, handR.pos, handR.rot, poleR, rightSide: true);
                DistributeForearmTwist(rig.ForearmR, rig.HandR, rig.TwistR);

                // La manivela al ángulo de esta fase, EXACTAMENTE como la pone el componente.
                rig.Crank.localRotation = BackroomsCrankFlashlightModelApplier.CrankLocalRotation *
                                          Quaternion.AngleAxis(theta, BackroomsCrankFlashlightModelApplier.CrankSpinAxis);
                Vector3 knob = rig.Crank.TransformPoint(rig.KnobLocal);
                Vector3 pegAxis = rig.Crank.TransformDirection(BackroomsCrankFlashlightModelApplier.CrankSpinAxis).normalized;
                if (Vector3.Dot(pegAxis, lateral) < 0f) pegAxis = -pegAxis;

                // La izquierda: palma contra el disco, el pomo en el punto de agarre.
                Vector3 fingers = rig.Root.TransformDirection(LeftFingersDirection);
                var handRot = Quaternion.LookRotation(pegAxis, Vector3.ProjectOnPlane(fingers, pegAxis).normalized);
                Vector3 handPos = knob - handRot * gripLocal;
                rig.UpperArmL.position = leftShoulder;
                float reach = (handPos - leftShoulder).magnitude;
                minLeftReach = Mathf.Min(minLeftReach, reach); maxLeftReach = Mathf.Max(maxLeftReach, reach);
                if (!SolveTwoBone(rig.UpperArmL, rig.ForearmL, rig.HandL, handPos, handRot, poleL, rightSide: false))
                    leftClamps++;
                DistributeForearmTwist(rig.ForearmL, rig.HandL, rig.TwistL);
                FitLeftHandToPeg(rig, knob, pegAxis);

                float gripError = (EnclosedCentre(rig.FingersL, rig.TipLengthL) - knob).magnitude;
                worstGripError = Mathf.Max(worstGripError, gripError);
                worstOrbitClearance = Mathf.Min(worstOrbitClearance, OrbitClearance(rig, knob));

                var frame = Capture(rig, k / (float)CrankFrames * length);
                list.Add(frame);
            }

            report.AppendLine($"cuerda: {length:F2} s por vuelta; el pomo entra en la izquierda con {worstGripError * 1000f:F1} mm " +
                              $"de error máximo; alcance izquierdo {minLeftReach:F2}–{maxLeftReach:F2} m de {armLength:F2} " +
                              $"({leftClamps} recortes); holgura mínima del pomo a la mano derecha " +
                              $"{worstOrbitClearance * 1000f:F0} mm.");
            return BuildClip(rig, list, length, loop: true);
        }

        /// <summary>Coloca el nodo del modelo (hijo de Hand.R) en una pose de MUNDO: centro, eje de
        /// la lente y costado de la manivela.</summary>
        private static void PlaceNode(Rig rig, Vector3 centre, Vector3 lens, Vector3 lateral)
        {
            Vector3 forward = Vector3.Cross(lateral, lens).normalized;
            rig.Node.SetPositionAndRotation(centre, Quaternion.LookRotation(forward, lens));
        }

        /// <summary>Distancia con signo de un punto del mundo a la piel del tubo (positivo = fuera).</summary>
        private static float TubeGap(Rig rig, Vector3 worldPoint)
        {
            Vector3 l = rig.Node.InverseTransformPoint(worldPoint);
            float r = Mathf.Sqrt(l.x * l.x + l.z * l.z);
            return r - BodyRadiusAt(rig, Mathf.Clamp(l.y, rig.ProfileMinY, rig.ProfileMaxY));
        }

        /// <summary>El mayor hueco angular alrededor del tubo sin ninguna falange derecha, en el
        /// reloj mirando desde la culata (0 = arriba, + = derecha del jugador).</summary>
        private static bool TryFreeArc(Rig rig, out float arcDegrees, out float centreClock)
        {
            Vector3 axis = rig.Node.up;
            Vector3 upRef = Vector3.ProjectOnPlane(rig.Root.up, axis).normalized;
            Vector3 rightRef = Vector3.Cross(upRef, axis).normalized;
            var angles = new List<float>();
            for (int f = 0; f < rig.FingersR.Length; f++)
                for (int j = 0; j <= 3; j++)
                {
                    Vector3 p = j < 3 ? rig.FingersR[f][j].position : rig.FingersR[f][2].TransformPoint(0f, rig.TipLengthR[f], 0f);
                    Vector3 d = p - rig.Node.position;
                    Vector3 perp = d - axis * Vector3.Dot(d, axis);
                    angles.Add(Mathf.Atan2(Vector3.Dot(perp, rightRef), Vector3.Dot(perp, upRef)) * Mathf.Rad2Deg);
                }
            // La muñeca también ocupa su sitio.
            {
                Vector3 d = rig.HandR.position - rig.Node.position;
                Vector3 perp = d - axis * Vector3.Dot(d, axis);
                angles.Add(Mathf.Atan2(Vector3.Dot(perp, rightRef), Vector3.Dot(perp, upRef)) * Mathf.Rad2Deg);
            }
            angles.Sort();
            arcDegrees = 0f; centreClock = 0f;
            if (angles.Count < 2) return false;
            // Con el pulgar arriba-izquierda y los nudillos a la derecha quedan DOS arcos casi
            // iguales (arriba-derecha y abajo-izquierda), y el mayor cambiaba de una pasada a otra
            // por un grado. Gana el arco de la mitad IZQUIERDA si es suficiente: es de donde viene
            // la mano que da cuerda, y así no tiene que cruzar por encima de la linterna.
            float leftArc = 0f, leftCentre = 0f;
            for (int i = 0; i < angles.Count; i++)
            {
                float a = angles[i], b = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 360f;
                float span = b - a, centre = (a + b) * 0.5f;
                if (centre > 180f) centre -= 360f;
                if (span > arcDegrees) { arcDegrees = span; centreClock = centre; }
                if (centre < 0f && span > leftArc) { leftArc = span; leftCentre = centre; }
            }
            if (leftArc > 60f) { arcDegrees = leftArc; centreClock = leftCentre; }
            return arcDegrees > 60f;
        }

        /// <summary>
        /// Cada falange derecha en coordenadas del TUBO: cuánto a lo largo (desde el centro, + hacia
        /// la lente), el hueco hasta la piel y a qué hora del reloj queda mirando desde la culata
        /// (0 = arriba, + = derecha del jugador). Es lo que contesta «¿dónde cae el índice?» sin
        /// una captura, y lo que dice si el pulgar va encima o debajo.
        /// </summary>
        private static string DescribeRightHandOnTube(Rig rig)
        {
            var sb = new StringBuilder();
            Vector3 axis = rig.Node.up;
            Vector3 upRef = Vector3.ProjectOnPlane(rig.Root.up, axis).normalized;
            Vector3 rightRef = Vector3.Cross(upRef, axis).normalized;
            string[] names = { "Pulgar", "Índice", "Corazón", "Anular", "Meñique" };
            for (int f = 0; f < rig.FingersR.Length; f++)
            {
                sb.Append("  ").Append(names[f]).Append(':');
                var chain = rig.FingersR[f];
                for (int j = 0; j <= chain.Length; j++)
                {
                    Vector3 p = j < chain.Length ? chain[j].position : chain[2].TransformPoint(0f, rig.TipLengthR[f], 0f);
                    Vector3 l = rig.Node.InverseTransformPoint(p);
                    float r = Mathf.Sqrt(l.x * l.x + l.z * l.z);
                    float gap = r - BodyRadiusAt(rig, Mathf.Clamp(l.y, rig.ProfileMinY, rig.ProfileMaxY));
                    Vector3 d = p - rig.Node.position;
                    Vector3 perp = d - axis * Vector3.Dot(d, axis);
                    float clock = Mathf.Atan2(Vector3.Dot(perp, rightRef), Vector3.Dot(perp, upRef)) * Mathf.Rad2Deg;
                    string label = j < chain.Length ? $"j{j + 1}" : "yema";
                    sb.Append($" {label}(y{l.y * 100f:+0.0}cm hueco{gap * 1000f:+0}mm {clock:0}°)");
                }
                sb.AppendLine();
            }
            {
                Vector3 d = rig.HandR.position - rig.Node.position;
                Vector3 perp = d - axis * Vector3.Dot(d, axis);
                float clock = Mathf.Atan2(Vector3.Dot(perp, rightRef), Vector3.Dot(perp, upRef)) * Mathf.Rad2Deg;
                var local = Quaternion.Inverse(rig.ForearmR.rotation) * rig.HandR.rotation;
                sb.AppendLine($"  muñeca a las {clock:0}° del tubo, a {perp.magnitude * 100f:0.0} cm del eje; torsión " +
                              $"mano/antebrazo {TwistDegrees(local):0}° (euler {Fmt(local.eulerAngles)}).");
            }
            // El arco LIBRE alrededor del tubo: el mayor hueco angular sin ninguna falange. Es donde
            // cabe el costado de la manivela sin que su órbita cruce la mano.
            {
                var angles = new List<float>();
                for (int f = 0; f < rig.FingersR.Length; f++)
                    for (int j = 0; j <= 3; j++)
                    {
                        Vector3 p = j < 3 ? rig.FingersR[f][j].position : rig.FingersR[f][2].TransformPoint(0f, rig.TipLengthR[f], 0f);
                        Vector3 d = p - rig.Node.position;
                        Vector3 perp = d - axis * Vector3.Dot(d, axis);
                        angles.Add(Mathf.Atan2(Vector3.Dot(perp, rightRef), Vector3.Dot(perp, upRef)) * Mathf.Rad2Deg);
                    }
                angles.Sort();
                float bestGap = 0f, bestMid = 0f;
                for (int i = 0; i < angles.Count; i++)
                {
                    float a = angles[i], b = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 360f;
                    if (b - a > bestGap) { bestGap = b - a; bestMid = (a + b) * 0.5f; }
                }
                if (bestMid > 180f) bestMid -= 360f;
                sb.AppendLine($"  arco libre de {bestGap:0}° centrado en las {bestMid:0}°.");
            }
            return sb.ToString();
        }

        /// <summary>Distancia mínima del CENTRO del pomo a cualquier falange derecha, menos el radio
        /// del pomo y el de la falange: positivo = no se tocan.</summary>
        private static float OrbitClearance(Rig rig, Vector3 knob)
        {
            float best = float.MaxValue;
            for (int f = 0; f < rig.FingersR.Length; f++)
            {
                var chain = rig.FingersR[f];
                float skin = f == 0 ? ThumbSkin : FingerSkin;
                for (int j = 0; j < chain.Length; j++)
                {
                    Vector3 a = chain[j].position;
                    Vector3 b = j + 1 < chain.Length ? chain[j + 1].position : chain[j].TransformPoint(0f, rig.TipLengthR[f], 0f);
                    float d = DistancePointSegment(knob, a, b) - rig.KnobRadius - skin;
                    if (d < best) best = d;
                }
            }
            return best;
        }

        // ─── El rig ──────────────────────────────────────────────────────────────────────────────

        private static Rig ReadRig(GameObject instance)
        {
            var rig = new Rig { ByName = new Dictionary<string, Transform>() };
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (!rig.ByName.ContainsKey(t.name)) rig.ByName[t.name] = t;

            Transform Need(string n) => rig.ByName.TryGetValue(n, out var t) ? t
                : throw new InvalidOperationException($"falta el hueso o nodo '{n}' en el prefab");

            rig.Animator = Need(AnimatorNodeName);
            rig.Root = Need(RootBoneName);
            rig.Camera = Need(CameraBoneName);
            rig.HandR = Need("Hand.R"); rig.ForearmR = Need("Forearm.R"); rig.UpperArmR = Need("UpperArm.R");
            rig.HandL = Need("Hand.L"); rig.ForearmL = Need("Forearm.L"); rig.UpperArmL = Need("UpperArm.L");
            rig.TwistR = new[] { Need("ForearmTwist.2.R"), Need("ForearmTwist.3.R"), Need("ForearmTwist.4.R") };
            rig.TwistL = new[] { Need("ForearmTwist.2.L"), Need("ForearmTwist.3.L"), Need("ForearmTwist.4.L") };
            rig.Node = Need(BackroomsCrankFlashlightModelApplier.NodeName);
            rig.Body = Need(BackroomsCrankFlashlightModelApplier.BodyNodeName);
            rig.Crank = Need(BackroomsCrankFlashlightModelApplier.CrankNodeName);
            rig.BodyMesh = rig.Body.GetComponent<MeshFilter>()?.sharedMesh
                ?? throw new InvalidOperationException("el nodo Body no trae malla");
            rig.CrankMesh = rig.Crank.GetComponent<MeshFilter>()?.sharedMesh
                ?? throw new InvalidOperationException("el nodo Crank no trae malla");

            string[] fingerNames = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
            rig.FingersR = fingerNames.Select(f => new[] { Need($"{f}.1.R"), Need($"{f}.2.R"), Need($"{f}.3.R") }).ToArray();
            rig.FingersL = fingerNames.Select(f => new[] { Need($"{f}.1.L"), Need($"{f}.2.L"), Need($"{f}.3.L") }).ToArray();
            // La última falange no tiene hijo que diga su largo: se toma el de la anterior, un poco
            // más corta, que es la proporción de una mano.
            rig.TipLengthR = rig.FingersR.Select(c => c[2].localPosition.magnitude * 0.9f).ToArray();
            rig.TipLengthL = rig.FingersL.Select(c => c[2].localPosition.magnitude * 0.9f).ToArray();

            // El esqueleto que se hornea: todo lo que cuelga de Root, menos el modelo.
            var skeleton = new List<Transform>();
            foreach (var t in rig.Root.GetComponentsInChildren<Transform>(true))
            {
                bool underModel = false;
                for (var p = t; p != null; p = p.parent)
                    if (p == rig.Node) { underModel = true; break; }
                if (!underModel) skeleton.Add(t);
            }
            rig.Skeleton = skeleton.ToArray();
            rig.Paths = rig.Skeleton.Select(t => AnimationUtility.CalculateTransformPath(t, rig.Animator)).ToArray();

            ReadBodyProfile(rig);
            ReadKnob(rig);
            return rig;
        }

        /// <summary>Radio del tubo por tramo de su largo (percentil 90 por bin): el cuerpo no es un
        /// cilindro perfecto y los dedos deben tocar la piel que hay en SU tramo.</summary>
        private static void ReadBodyProfile(Rig rig)
        {
            const int bins = 36;
            var verts = rig.BodyMesh.vertices;
            var b = rig.BodyMesh.bounds;
            rig.ProfileMinY = b.min.y; rig.ProfileMaxY = b.max.y;
            var radii = new List<float>[bins];
            for (int i = 0; i < bins; i++) radii[i] = new List<float>();
            foreach (var v in verts)
            {
                int bin = Mathf.Clamp((int)((v.y - b.min.y) / (b.size.y) * bins), 0, bins - 1);
                radii[bin].Add(Mathf.Sqrt(v.x * v.x + v.z * v.z));
            }
            rig.RadiusProfile = new float[bins];
            float last = b.extents.x;
            for (int i = 0; i < bins; i++)
            {
                if (radii[i].Count == 0) { rig.RadiusProfile[i] = last; continue; }
                radii[i].Sort();
                last = radii[i][Mathf.Min(radii[i].Count - 1, (int)(radii[i].Count * 0.9f))];
                rig.RadiusProfile[i] = last;
            }
        }

        private static float BodyRadiusAt(Rig rig, float y)
        {
            int bins = rig.RadiusProfile.Length;
            float u = (y - rig.ProfileMinY) / (rig.ProfileMaxY - rig.ProfileMinY);
            int i = Mathf.Clamp((int)(u * bins), 0, bins - 1);
            return rig.RadiusProfile[i];
        }

        /// <summary>El POMO de la manivela, en local de la propia manivela: el centroide del cuarto
        /// más lejano del eje, y su radio. La malla canónica pone el eje en y=0 y el brazo hacia +Y.</summary>
        private static void ReadKnob(Rig rig)
        {
            var verts = rig.CrankMesh.vertices;
            float maxY = rig.CrankMesh.bounds.max.y;
            float cut = maxY - rig.CrankMesh.bounds.size.y * 0.25f;
            Vector3 sum = Vector3.zero; int n = 0;
            foreach (var v in verts) { if (v.y < cut) continue; sum += v; n++; }
            rig.KnobLocal = n > 0 ? sum / n : new Vector3(0f, maxY, 0f);
            float r = 0f; n = 0;
            foreach (var v in verts)
            {
                if (v.y < cut) continue;
                r += new Vector2(v.x - rig.KnobLocal.x, v.z - rig.KnobLocal.z).magnitude; n++;
            }
            rig.KnobRadius = n > 0 ? Mathf.Max(0.004f, r / n * 1.2f) : 0.008f;
        }

        /// <summary>El pomo en local de la manivela, para quien lo quiera medir fuera (tests).</summary>
        internal static Vector3 KnobLocalOf(Mesh crankMesh)
        {
            var rig = new Rig { CrankMesh = crankMesh };
            ReadKnob(rig);
            return rig.KnobLocal;
        }

        private struct VendorClips { public AnimationClip Idle, Equip, Holster; }

        /// <summary>
        /// Los clips ORIGINALES de la antorcha, del FBX del avatar del Animator, y NO los overrides del
        /// prefab: tras el primer horneado los overrides son los nuestros, y muestrearlos para
        /// hornear otra vez apilaría desplazamiento sobre desplazamiento.
        /// </summary>
        private static VendorClips FindVendorClips(Rig rig)
        {
            var animator = rig.Animator.GetComponent<Animator>();
            var result = new VendorClips();
            if (animator == null || animator.avatar == null) return result;
            string fbx = AssetDatabase.GetAssetPath(animator.avatar);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbx))
            {
                if (asset is not AnimationClip clip || clip.name.StartsWith("__")) continue;
                string n = clip.name.ToLowerInvariant();
                if (n.Contains("idle")) result.Idle ??= clip;
                else if (n.Contains("equip")) result.Equip ??= clip;
                else if (n.Contains("holster")) result.Holster ??= clip;
            }
            return result;
        }

        private static float ReadRevolutionsPerSecond(GameObject instance)
        {
            var flashlight = instance.GetComponent<BackroomsSurvival.Gameplay.CrankFlashlightWieldable>();
            if (flashlight == null) return 1f;
            var so = new SerializedObject(flashlight);
            var p = so.FindProperty("revolutionsPerSecond");
            return p != null && p.floatValue > 0.05f ? p.floatValue : 1f;
        }

        private static void Sample(Rig rig, AnimationClip clip, float t)
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(rig.Animator.gameObject, clip, t);
            AnimationMode.EndSampling();
            // El root del viewmodel no se anima: si el muestreo lo tocó (RootT/RootQ), vuelve a su sitio.
            rig.Animator.localPosition = Vector3.zero;
            rig.Animator.localRotation = Quaternion.identity;
        }

        private static Frame Capture(Rig rig, float time)
        {
            var f = new Frame { Time = time, Pos = new Vector3[rig.Skeleton.Length], Rot = new Quaternion[rig.Skeleton.Length] };
            for (int i = 0; i < rig.Skeleton.Length; i++)
            {
                f.Pos[i] = rig.Skeleton[i].localPosition;
                f.Rot[i] = rig.Skeleton[i].localRotation;
            }
            return f;
        }

        private static void Apply(Rig rig, Frame f)
        {
            for (int i = 0; i < rig.Skeleton.Length; i++)
            {
                rig.Skeleton[i].localPosition = f.Pos[i];
                rig.Skeleton[i].localRotation = f.Rot[i];
            }
        }

        // ─── IK y torsión ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// IK analítico de dos huesos. Los huesos de este rig apuntan por +Y a su hijo, y el codo es
        /// una bisagra sobre la Z local del brazo: −Z en el derecho, +Z en el izquierdo (medido en
        /// la pose de bind: el antebrazo derecho gira 93° sobre −Z, el izquierdo 13° sobre +Z). Con
        /// la bisagra clavada a la Z de los dos huesos, la TORSIÓN de la mano va entera a la muñeca
        /// y a los huesos de torsión, que es como el vendor reparte la piel del antebrazo.
        /// Devuelve false si el objetivo no se alcanza y hubo que recortarlo.
        /// </summary>
        private static bool SolveTwoBone(Transform upper, Transform fore, Transform hand, Vector3 target,
            Quaternion handRotation, Vector3 pole, bool rightSide)
        {
            Vector3 s = upper.position;
            float a = (fore.position - upper.position).magnitude;
            float b = (hand.position - fore.position).magnitude;
            Vector3 toTarget = target - s;
            float d = toTarget.magnitude;
            bool reachable = true;
            float dMax = a + b - 0.002f;
            if (d > dMax) { d = dMax; reachable = false; }
            d = Mathf.Max(d, Mathf.Abs(a - b) + 0.002f);
            Vector3 u = toTarget.normalized;

            Vector3 poleDir = Vector3.ProjectOnPlane(pole, u).normalized;
            if (poleDir.sqrMagnitude < 1e-6f) poleDir = Vector3.ProjectOnPlane(Vector3.down, u).normalized;

            float cosAlpha = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float alpha = Mathf.Acos(cosAlpha);
            Vector3 upperDir = (u * Mathf.Cos(alpha) + poleDir * Mathf.Sin(alpha)).normalized;
            Vector3 elbow = s + upperDir * a;
            Vector3 foreDir = (s + u * d - elbow).normalized;

            Vector3 bendNormal = Vector3.Cross(upperDir, foreDir);
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.Cross(upperDir, poleDir);
            bendNormal.Normalize();
            Vector3 zAxis = rightSide ? -bendNormal : bendNormal;

            upper.rotation = Quaternion.LookRotation(zAxis, upperDir);
            fore.rotation = Quaternion.LookRotation(zAxis, foreDir);
            hand.rotation = handRotation;
            return reachable;
        }

        /// <summary>
        /// Barre el alabeo del puño sobre el eje del tubo y devuelve la rotación rígida completa
        /// (alabeo · mango→lente). Criterio: torsión de muñeca lo más parecida a la del vendor con
        /// el pulgar en la mitad de arriba. Deja la instancia en la pose del vendor al salir.
        /// </summary>
        private static Quaternion ChooseRoll(Rig rig, AnimationClip idle, Vector3 handleAxis, Vector3 lens,
            Vector3 vendorFist, Vector3 fist, Vector3 poleR, float vendorTwist, StringBuilder report)
        {
            var baseRotation = Quaternion.FromToRotation(handleAxis, lens);
            Vector3 upRef = Vector3.ProjectOnPlane(rig.Root.up, lens).normalized;
            Vector3 rightRef = Vector3.Cross(upRef, lens).normalized;
            float bestScore = float.MaxValue, bestRoll = 0f, bestTwist = 0f, bestThumb = 0f;
            var table = new StringBuilder();
            for (float roll = -180f; roll < 180f; roll += RollScanStepDegrees)
            {
                Sample(rig, idle, 0f);
                var rigid = Quaternion.AngleAxis(roll, lens) * baseRotation;
                var hand = MoveRigid(rig.HandR, rigid, vendorFist, fist);
                SolveTwoBone(rig.UpperArmR, rig.ForearmR, rig.HandR, hand.pos, hand.rot, poleR, rightSide: true);
                float twist = TwistDegrees(Quaternion.Inverse(rig.ForearmR.rotation) * rig.HandR.rotation);
                Vector3 d = rig.FingersR[0][0].position - fist;
                Vector3 perp = d - lens * Vector3.Dot(d, lens);
                float thumbClock = Mathf.Atan2(Vector3.Dot(perp, rightRef), Vector3.Dot(perp, upRef)) * Mathf.Rad2Deg;
                float score = Mathf.Abs(Mathf.DeltaAngle(twist, vendorTwist)) + (Mathf.Abs(thumbClock) > 90f ? 1000f : 0f);
                table.Append($" {roll:+0}°→torsión {twist:0}° pulgar {thumbClock:0}°;");
                if (score < bestScore) { bestScore = score; bestRoll = roll; bestTwist = twist; bestThumb = thumbClock; }
            }
            Sample(rig, idle, 0f);
            report.AppendLine($"alabeo elegido {bestRoll:+0}°: torsión de muñeca {bestTwist:0}° (vendor {vendorTwist:0}°), " +
                              $"pulgar a las {bestThumb:0}° del tubo. Barrido:{table}");
            return Quaternion.AngleAxis(bestRoll, lens) * baseRotation;
        }

        /// <summary>Torsión (giro sobre Y, el eje del hueso) de una rotación local: la parte
        /// «twist» de la descomposición swing-twist, en grados con signo.</summary>
        private static float TwistDegrees(Quaternion local)
        {
            var t = new Quaternion(0f, local.y, 0f, local.w);
            if (t.y == 0f && t.w == 0f) return 0f;
            t.Normalize();
            float angle = 2f * Mathf.Atan2(t.y, t.w) * Mathf.Rad2Deg;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;
            return angle;
        }

        /// <summary>
        /// Los tres huesos de torsión del antebrazo reparten la torsión de la mano sobre el eje del
        /// antebrazo, a razón de su distancia a la muñeca. MEDIDO en el idle del vendor: mano a 62°
        /// de torsión, huesos a 15/29/40° — 0,24/0,46/0,65 de la torsión, que es 0,9 veces su
        /// posición relativa (0,25/0,50/0,75). Ese 0,9 es del vendor y se conserva.
        /// </summary>
        private static void DistributeForearmTwist(Transform fore, Transform hand, Transform[] twist)
        {
            float angle = TwistDegrees(Quaternion.Inverse(fore.rotation) * hand.rotation);
            float handLength = hand.localPosition.magnitude;
            foreach (var tw in twist)
            {
                float k = 0.9f * (tw.localPosition.magnitude / Mathf.Max(1e-4f, handLength));
                tw.localRotation = Quaternion.AngleAxis(angle * k, Vector3.up);
            }
        }

        // ─── Dedos ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Firma de superficie: distancia con signo de un punto del mundo a la piel del
        /// objeto (positivo fuera). El ajuste cierra cada falange hasta que su hueso queda a
        /// «radio de dedo» de la piel.</summary>
        private delegate float Surface(Vector3 worldPoint);

        private static void FitRightHandToTube(Rig rig, FitStats stats)
        {
            Surface tube = p =>
            {
                Vector3 l = rig.Node.InverseTransformPoint(p);
                float r = Mathf.Sqrt(l.x * l.x + l.z * l.z);
                float y = Mathf.Clamp(l.y, rig.ProfileMinY, rig.ProfileMaxY);
                float radius = BodyRadiusAt(rig, y);
                // Más allá de los extremos del tubo el dedo está libre: se mide contra el borde.
                if (l.y < rig.ProfileMinY || l.y > rig.ProfileMaxY)
                {
                    float beyond = l.y < rig.ProfileMinY ? rig.ProfileMinY - l.y : l.y - rig.ProfileMaxY;
                    return Mathf.Sqrt(Mathf.Max(0f, r - radius) * Mathf.Max(0f, r - radius) + beyond * beyond);
                }
                return r - radius;
            };
            for (int f = 0; f < rig.FingersR.Length; f++)
            {
                bool thumb = f == 0;
                if (thumb)
                    LayThumbAlongTube(rig.FingersR[0][0], rig.Node.up);
                // El cierre es NEGATIVO sobre el eje que se pasa: los dedos cierran girando sobre −X
                // y el pulgar derecho sobre +Z, así que a él se le pasa −Z.
                FitFinger(rig.FingersR[f], rig.TipLengthR[f], thumb ? -Vector3.forward : Vector3.right,
                    thumb ? ThumbSkin : FingerSkin, tube, thumb ? 1 : 0, stats);
            }
        }

        /// <summary>
        /// LA BASE DEL PULGAR SE TIENDE A LO LARGO DEL TUBO, hacia la lente, como en la foto de
        /// referencia. El pulgar del vendor rodea un palo de 3 cm y su base apunta a través del
        /// puño; sobre un tubo de 6 no llega a rodear nada y se quedaba abierto, colgando. Con la
        /// primera falange paralela al tubo, las otras dos cierran por contacto sobre su lomo.
        /// </summary>
        private static void LayThumbAlongTube(Transform thumbBase, Vector3 tubeAxis)
        {
            thumbBase.rotation = Quaternion.FromToRotation(thumbBase.up, tubeAxis) * thumbBase.rotation;
        }

        private static void FitLeftHandToPeg(Rig rig, Vector3 knob, Vector3 pegAxis)
        {
            Surface peg = p => DistancePointLine(p, knob, pegAxis) - rig.KnobRadius;
            for (int f = 0; f < rig.FingersL.Length; f++)
            {
                bool thumb = f == 0;
                // De partida los dedos de la izquierda vienen abiertos (cuelga suelta): se cierran a
                // una copa genérica y el contacto los deja sobre el pomo.
                var chain = rig.FingersL[f];
                for (int j = thumb ? 1 : 0; j < 3; j++)
                    chain[j].localRotation = chain[j].localRotation *
                        Quaternion.AngleAxis(thumb ? -25f : -45f, thumb ? Vector3.forward : Vector3.right);
                // El pulgar izquierdo cierra sobre −Z (espejo del derecho): se le pasa +Z.
                FitFinger(chain, rig.TipLengthL[f], thumb ? Vector3.forward : Vector3.right,
                    thumb ? ThumbSkin : FingerSkin, peg, thumb ? 1 : 0, null);
            }
        }

        /// <summary>
        /// Cierra un dedo falange a falange hasta TOCAR la superficie. Para cada articulación, de la
        /// base a la punta, se barre el ángulo sobre su eje de flexión y se elige el que deja el
        /// extremo de esa falange a «piel» de la superficie sin que ninguna falange posterior entre
        /// en ella. Un dedo del vendor cerrado sobre un palo de 3 cm sale abriéndose sobre un tubo
        /// de 6; el mismo código cerraría uno abierto. El signo del cierre lo da el eje que se pasa.
        /// </summary>
        private static void FitFinger(Transform[] chain, float tipLength, Vector3 curlAxis, float skin, Surface surface,
            int firstJoint, FitStats stats)
        {
            // DOS PASADAS de la base a la punta. En cada articulación se barre DEL MÁS CERRADO AL MÁS
            // ABIERTO y se toma el primer ángulo en que el extremo de ESA falange queda a «piel» de
            // la superficie sin que el centro de la falange se hunda más de un tercio de piel
            // (una falange recta sobre un tubo curvo siempre se hunde un poco por el medio). Sólo esa
            // falange: exigir además que el resto del dedo (aún con el cierre del vendor) no
            // penetrase abría la base hasta despegar la punta, y el dedo salía en tienda de
            // campaña con la segunda falange a 3 cm del tubo. Las falanges siguientes se colocan
            // ellas mismas en su turno, y la segunda pasada deja que la base cierre lo que la
            // primera no pudo. Un dedo tendido a lo largo del tubo también «toca» (contacto
            // tangente); por eso gana la solución MÁS CERRADA que toca, no la de menor |hueco|.
            for (int pass = 0; pass < 2; pass++)
            for (int j = firstJoint; j < chain.Length; j++)
            {
                var start = chain[j].localRotation;
                float bestScore = float.MaxValue; Quaternion best = start;
                bool found = false;
                for (int deg = -75; deg <= 60 && !found; deg++)
                {
                    chain[j].localRotation = start * Quaternion.AngleAxis(deg, curlAxis);
                    Vector3 a = chain[j].position, b = EndOf(chain, j, tipLength);
                    float endGap = surface(b) - skin;
                    float midGap = surface(Vector3.Lerp(a, b, 0.5f)) - skin;
                    // El PRIMER ángulo que no penetra, viniendo del cierre máximo: si la superficie
                    // está ahí, es el contacto; si no la alcanza (un pomo pequeño), es el dedo lo más
                    // cerrado posible sin tocar, que es un puño flojo y no un índice tieso. Con el
                    // contacto como única salida el índice izquierdo se quedaba señalando.
                    if (endGap >= -0.0015f && midGap >= -skin / 3f)
                    {
                        best = chain[j].localRotation;
                        found = true;
                        break;
                    }
                    float score = Mathf.Abs(endGap) + (midGap < -skin / 3f ? 1f - midGap : 0f) + (endGap < -0.0015f ? 1f - endGap : 0f);
                    if (score < bestScore) { bestScore = score; best = chain[j].localRotation; }
                }
                chain[j].localRotation = best;
                if (pass == 1) stats?.Add(surface(EndOf(chain, j, tipLength)) - skin);
            }
        }

        /// <summary>El centro de lo que encierra una mano cerrada: la media de las segundas falanges
        /// de los cuatro dedos y de la yema del pulgar. Es donde está el pomo cuando la mano lo
        /// tiene agarrado, y lo que mide el test del clip de cuerda.</summary>
        internal static Vector3 EnclosedCentre(Transform[][] fingers, float[] tipLengths)
        {
            Vector3 sum = Vector3.zero;
            for (int f = 1; f < fingers.Length; f++) sum += fingers[f][1].position;
            sum += fingers[0][2].TransformPoint(0f, tipLengths[0], 0f);
            return sum / fingers.Length;
        }

        private static Vector3 EndOf(Transform[] chain, int j, float tipLength)
            => j + 1 < chain.Length ? chain[j + 1].position : chain[j].TransformPoint(0f, tipLength, 0f);

        private static float DistancePointLine(Vector3 p, Vector3 origin, Vector3 dir)
            => Vector3.Cross(p - origin, dir.normalized).magnitude;

        private static float DistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        // ─── Clips y controller ──────────────────────────────────────────────────────────────────

        private static AnimationClip BuildClip(Rig rig, List<Frame> frames, float length, bool loop)
        {
            var clip = new AnimationClip { frameRate = FrameRate };
            int n = rig.Skeleton.Length;
            for (int i = 0; i < n; i++)
            {
                var px = new AnimationCurve(); var py = new AnimationCurve(); var pz = new AnimationCurve();
                var rx = new AnimationCurve(); var ry = new AnimationCurve(); var rz = new AnimationCurve(); var rw = new AnimationCurve();
                Quaternion previous = frames[0].Rot[i];
                foreach (var f in frames)
                {
                    var q = f.Rot[i];
                    // Continuidad: el mismo giro con signo opuesto es la misma rotación pero una
                    // curva que salta. Se mantiene el hemisferio del fotograma anterior.
                    if (Quaternion.Dot(q, previous) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    previous = q;
                    px.AddKey(f.Time, f.Pos[i].x); py.AddKey(f.Time, f.Pos[i].y); pz.AddKey(f.Time, f.Pos[i].z);
                    rx.AddKey(f.Time, q.x); ry.AddKey(f.Time, q.y); rz.AddKey(f.Time, q.z); rw.AddKey(f.Time, q.w);
                }
                string path = rig.Paths[i];
                Set(clip, path, "m_LocalPosition.x", Squash(px)); Set(clip, path, "m_LocalPosition.y", Squash(py)); Set(clip, path, "m_LocalPosition.z", Squash(pz));
                Set(clip, path, "m_LocalRotation.x", Squash(rx)); Set(clip, path, "m_LocalRotation.y", Squash(ry));
                Set(clip, path, "m_LocalRotation.z", Squash(rz)); Set(clip, path, "m_LocalRotation.w", Squash(rw));
            }
            clip.EnsureQuaternionContinuity();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            settings.loopBlend = false;
            settings.stopTime = length;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        /// <summary>
        /// Una curva que no cambia se guarda con DOS claves, no con una por fotograma. De los 48
        /// huesos del rig, en el idle sólo se mueven los del brazo derecho: el resto (dedos, torsión,
        /// izquierda, cámara) es constante, y escribirlo entero dejaba un idle de 26 MB en texto.
        /// El umbral es por debajo de lo que un cuaternión o un metro de hueso pueden mostrar.
        /// </summary>
        private static AnimationCurve Squash(AnimationCurve curve)
        {
            if (curve.length < 3) return curve;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < curve.length; i++)
            {
                float v = curve[i].value;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max - min > 1e-5f) return curve;
            var first = curve[0]; var last = curve[curve.length - 1];
            return new AnimationCurve(new Keyframe(first.time, first.value), new Keyframe(last.time, first.value));
        }

        private static void Set(AnimationClip clip, string path, string property, AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), property), curve);
        }

        private static AnimationClip SaveClip(AnimationClip clip, string path, string name, bool loop)
        {
            clip.name = name;
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(clip, existing);
                Object.DestroyImmediate(clip);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        /// <summary>
        /// El controller propio: una COPIA del <c>Template_Tool</c> del vendor (misma máquina de
        /// estados, mismos parámetros, así que equipar/enfundar/usar siguen funcionando con los
        /// overrides) más una capa «Crank» en override a peso 0 con un único estado en bucle. El
        /// peso lo sube y baja el wieldable. Se conserva el asset entre horneados (su GUID vive en
        /// el prefab): sólo se rehace la capa.
        /// </summary>
        private static AnimatorController EnsureController(AnimationClip crankClip)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                if (!AssetDatabase.CopyAsset(TemplateControllerPath, ControllerPath))
                    throw new InvalidOperationException($"no se pudo copiar '{TemplateControllerPath}' a '{ControllerPath}'");
                controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            }

            for (int i = controller.layers.Length - 1; i >= 1; i--)
                if (controller.layers[i].name == CrankLayerName) controller.RemoveLayer(i);

            controller.AddLayer(CrankLayerName);
            var layers = controller.layers;
            int index = layers.Length - 1;
            layers[index].defaultWeight = 0f;
            layers[index].blendingMode = AnimatorLayerBlendingMode.Override;
            controller.layers = layers;

            var machine = controller.layers[index].stateMachine;
            var state = machine.AddState(CrankStateName);
            state.motion = crankClip;
            machine.defaultState = state;
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static void WritePrefab(Result result, AnimatorController controller, AnimationClip idle,
            AnimationClip equip, AnimationClip holster, AnimationClip crank)
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Transform hand = null, node = null, animatorNode = null;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Hand.R") hand ??= t;
                    else if (t.name == BackroomsCrankFlashlightModelApplier.NodeName) node = t;
                    else if (t.name == AnimatorNodeName) animatorNode ??= t;
                }
                if (hand == null || node == null || animatorNode == null)
                    throw new InvalidOperationException("el prefab ha perdido Hand.R, el nodo del modelo o el ViewModel");

                node.SetParent(hand, false);
                node.localPosition = result.NodeLocalPosition;
                node.localRotation = result.NodeLocalRotation;
                node.localScale = result.NodeLocalScale;

                var animator = animatorNode.GetComponent<Animator>();
                if (animator != null)
                {
                    var aso = new SerializedObject(animator);
                    aso.FindProperty("m_Controller").objectReferenceValue = controller;
                    aso.ApplyModifiedPropertiesWithoutUndo();
                }

                var wieldableAnimator = root.GetComponentInChildren<PolymindGames.WieldableSystem.WieldableAnimator>(true);
                if (wieldableAnimator == null)
                    throw new InvalidOperationException("el prefab no tiene WieldableAnimator");
                var so = new SerializedObject(wieldableAnimator);
                so.FindProperty("_clips._controller").objectReferenceValue = controller;
                var pairs = so.FindProperty("_clips._clips");
                // La MISMA lista que usa el drawer del vendor (`controller.animationClips`, sin deduplicar):
                // si el tamaño no coincide, el inspector la regenera y se pierden los overrides.
                var originals = controller.animationClips;
                pairs.arraySize = originals.Length;
                for (int i = 0; i < originals.Length; i++)
                {
                    var e = pairs.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("Original").objectReferenceValue = originals[i];
                    e.FindPropertyRelative("Override").objectReferenceValue = OverrideFor(originals[i], idle, equip, holster, crank);
                }
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static AnimationClip OverrideFor(AnimationClip original, AnimationClip idle, AnimationClip equip,
            AnimationClip holster, AnimationClip crank)
        {
            string n = original.name.ToLowerInvariant();
            if (original == crank) return crank;
            if (n.Contains("equip")) return equip;
            if (n.Contains("holster")) return holster;
            // «Use» también es el idle, como en el vendor: la linterna no tiene animación de uso.
            if (n.Contains("idle") || n.Contains("use")) return idle;
            return null;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }
}
#endif
