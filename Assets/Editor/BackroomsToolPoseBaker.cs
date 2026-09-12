#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-077 enm. 4 — HORNEA LOS DEDOS de una herramienta de UNA MANO (destornillador, bote de
    /// spray) sobre su propia malla, y manda la izquierda fuera del encuadre.
    ///
    /// El problema que resuelve, señalado por Joel: colocar bien la malla NO basta. El wieldable
    /// hereda los clips del donante del que se clonó (el hacha, la antorcha) y esos clips traen los
    /// dedos cerrados sobre OTRO objeto — y, en el caso del hacha, las DOS manos sobre el mango,
    /// que en un destornillador no lo coge nadie. Los dedos son huesos animados: para cambiarlos
    /// hay que hornear clips propios, que es lo que ADR-133 hizo para la linterna.
    ///
    /// Éste es el mismo método, generalizado y sin lo que era propio de la linterna (capa de
    /// manivela, IK de la izquierda sobre el pomo). Por objeto sólo cambian los números de diseño
    /// del <see cref="Spec"/>; el algoritmo —agarre del donante, barrido de alabeo, cierre de dedos
    /// por contacto contra el perfil de radio de la MALLA REAL— es común.
    ///
    /// Lo que NO hace, declarado: no inventa un vaivén nuevo. Los tres clips (idle, equipar,
    /// enfundar) son los del vendor con el brazo derecho recolocado y la mano resuelta por IK, así
    /// que el recorrido y el ritmo siguen siendo los del donante.
    /// </summary>
    internal static class BackroomsToolPoseBaker
    {
        /// <summary>Los números de diseño de UN objeto. Todo lo demás es común.</summary>
        internal sealed class Spec
        {
            public string Tag;
            public string PrefabPath;
            public string NodeName;
            public string AnimFolder;
            public string ClipPrefix;

            /// <summary>Donante skinned del que se lee el agarre del vendor (ver <see cref="BackroomsDonorGrip"/>).</summary>
            public string DonorSkinNode;
            public string TipChildHint;

            /// <summary>Largo real del objeto, en metros: el mismo con el que se horneó la malla.</summary>
            public float LengthMeters;

            /// <summary>A qué fracción del largo, desde la culata, cierra el CENTRO de la pila de
            /// dedos. 0,5 es el centro del objeto.</summary>
            public float FistFromTail;

            /// <summary>
            /// CONSERVA EL ENCUADRE DEL DONANTE: el puño y el eje del objeto se quedan donde el
            /// vendor los animó, y este horneado sólo cambia los DEDOS y la izquierda. Es el cambio
            /// mínimo cuando la posición ya se daba por buena y lo que falla es el agarre.
            /// Con false manda el encuadre de diseño (<see cref="FistFromEye"/> y el cabeceo).
            /// </summary>
            public bool KeepVendorFraming = true;

            /// <summary>
            /// SÓLO LA ORIENTACIÓN ES DE DISEÑO; el puño se queda donde el vendor lo puso. Es el
            /// punto medio entre los dos modos y casi siempre el que se quiere: dónde cae la mano en
            /// pantalla ya estaba bien (es el encuadre que el vendor animó y que aquí nadie ha
            /// discutido), y lo que estaba mal era hacia dónde APUNTA el objeto. Dando también el
            /// puño por diseño hay que recalibrar el encuadre entero a ciegas, y la mano se me fue
            /// fuera del cuadro dos veces.
            /// </summary>
            public bool DesignAxisOnly;

            /// <summary>Dónde cae el puño respecto del ojo, en espacio del root. Sólo con
            /// <see cref="KeepVendorFraming"/> en false y <see cref="DesignAxisOnly"/> en false.</summary>
            public Vector3 FistFromEye;

            /// <summary>Hacia dónde apunta el objeto (su +Y canónico) respecto del frente del
            /// jugador: cabeceo positivo = hacia abajo, guiñada positiva = hacia la derecha. Sólo
            /// con <see cref="KeepVendorFraming"/> en false.</summary>
            public float PitchDownDegrees;
            public float YawDegrees;

            /// <summary>La izquierda se manda abajo, fuera del encuadre. El hacha pone las DOS manos
            /// en el mango; un destornillador se coge con una.</summary>
            public bool TuckLeftHand;

            /// <summary>
            /// EL ÍNDICE VA SOBRE LA BOQUILLA, no rodeando el cuerpo: así se sujeta un bote de
            /// spray, con el índice apretando el pulsador. Cambia la superficie contra la que cierra
            /// ESE dedo (una bola en el pulsador) y obliga a agarrar alto — con la mano en el tercio
            /// de abajo el índice no llega arriba ni estirándolo.
            /// </summary>
            public bool IndexOnNozzle;

            /// <summary>
            /// EL PULGAR SE TIENDE A LO LARGO DEL MANGO, apuntando a la punta, en vez de rodearlo.
            /// Es como se coge un destornillador para empujar (foto de referencia de Joel): el
            /// pulgar da la fuerza axial. Rodeándolo queda un puño de martillo, que no es lo mismo.
            /// </summary>
            public bool ThumbAlongTool;

            /// <summary>Cuánto por debajo de la punta está el centro del pulsador, en metros.</summary>
            public float NozzleDropMeters = 0.012f;

            /// <summary>Radio del PULSADOR, no el de la tapa: es un botón de medio centímetro. Con
            /// el radio del perfil (3 cm, toda la tapa) el índice se metía dentro de la lata.</summary>
            public float NozzleRadiusMeters = 0.007f;

            /// <summary>
            /// LA MUÑECA, en ángulos anatómicos con nombre y sobre la NEUTRA. Con Hand.R a identidad
            /// sus ejes son los del antebrazo, así que estos dos números son de verdad extensión y
            /// desviación cubital. Antes la muñeca venía heredada del clip del hacha —un agarre a
            /// DOS manos para talar— y eso solo ya hacía antinatural todo lo que colgaba de ella.
            /// 20° de extensión y 10° cubital son la postura de fuerza de una mano que empuja.
            /// </summary>
            public float WristExtensionDegrees = 20f;
            public float WristUlnarDegrees = 10f;

            /// <summary>Dónde queda la mano izquierda recogida, respecto del ojo y en espacio del
            /// root. Sólo se usa con <see cref="TuckLeftHand"/>.</summary>
            public Vector3 LeftHandFromEye = new(-0.22f, -0.62f, 0.12f);
        }

        private const string AnimatorNodeName = "ViewModel";
        private const string RootBoneName = "Root";
        private const string CameraBoneName = "Camera";
        private const float FrameRate = 30f;

        /// <summary>Barrido del alabeo del puño alrededor del eje del objeto.</summary>
        private const float RollScanStepDegrees = 5f;

        /// <summary>Radio de una falange: del hueso a la piel. Es lo que separa el eje del dedo de
        /// la superficie cuando el dedo la toca.</summary>
        private const float FingerSkin = 0.0085f;
        private const float ThumbSkin = 0.0105f;

        /// <summary>Aire mínimo entre los nudillos y la piel del objeto: la palma no lo atraviesa.</summary>
        private const float PalmClearance = FingerSkin;

        /// <summary>
        /// Cuánto se tolera que un dedo se hunda en el objeto, y cuánto se puede apartar el objeto
        /// de la palma para evitarlo. UNOS MILÍMETROS DE INTERSECCIÓN NO SE VEN; un puño cerrado en
        /// el aire con el objeto al lado, sí. Buscando penetración CERO el bote acabó 44 mm fuera de
        /// la mano: los dedos rodeaban la nada. La carne de un dedo cede sobre un objeto duro, así
        /// que hundirse 4 mm es lo que hace una mano de verdad.
        /// </summary>
        private const float AllowedPenetration = 0.003f;
        // 22 mm de margen: con el ancla en la línea de nudillos de la mano FINAL el bucle converge
        // en pocos milímetros, pero el meñique —cuyo nudillo queda más afuera que la media— pedía
        // más de los 12 mm de antes y se hundía 33 mm en la lata.
        private const float MaxPalmPush = 0.022f;

        /// <summary>Cuanto puede ACERCARSE el objeto al hueco que encierran los dedos antes de que
        /// deje de ser el sitio donde esta mano agarra. Un mango fino lo necesita entero.</summary>
        private const float MaxPalmPull = 0.030f;

        /// <summary>Hueco al que se considera que una yema TOCA, en metros. Es el centro del margen
        /// que exige <c>ToolGripTests</c> (-4 a +12 mm), no un numero suelto.</summary>
        private const float TouchGap = 0.008f;

        /// <summary>Margen de la yema, el mismo que exige <c>ToolGripTests</c>: nunca mas de 4 mm
        /// dentro de la piel ni mas de 12 mm fuera.</summary>
        private const float TipInside = 0.004f;
        private const float TipOutside = 0.012f;

        private static readonly Vector3 ElbowPoleRight = new(0.7f, -1f, -0.4f);
        private static readonly Vector3 ElbowPoleLeft = new(-0.7f, -1f, -0.4f);

        // ─── Entrada ─────────────────────────────────────────────────────────────────────────────

        internal static bool Bake(Spec spec) => Bake(spec, null);

        /// <summary>
        /// Con <paramref name="preview"/> NO se escribe nada: se resuelve la pose, se aplica el idle
        /// recién construido a la instancia y se entrega para retratarla. Es el modo de iterar en el
        /// editor abierto sin tocar ni un asset — hornear de verdad son minutos y ensucia el árbol.
        /// </summary>
        internal static bool Bake(Spec spec, Action<GameObject> preview)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"{spec.Tag} No hay prefab en '{spec.PrefabPath}'.");
                return false;
            }
            BackroomsEditorFolders.EnsureFolder(spec.AnimFolder);

            // Se MIDE sobre una instancia en escena (muestrear clips es cosa de escena) y se escribe
            // en el prefab después. Nada de lo que se toca aquí sobrevive al método.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (t.name == AnimatorNodeName || t.name == RootBoneName) t.gameObject.SetActive(true);

            Result result;
            try
            {
                result = Solve(instance, spec);
            }
            catch (Exception e)
            {
                Debug.LogError($"{spec.Tag} Horneado abortado: " + e);
                UnityEngine.Object.DestroyImmediate(instance);
                return false;
            }
            finally
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            }
            GameObject instanceForPreview = null;
            if (preview != null) instanceForPreview = instance;
            else UnityEngine.Object.DestroyImmediate(instance);
            if (result == null)
            {
                if (instanceForPreview != null) UnityEngine.Object.DestroyImmediate(instanceForPreview);
                return false;
            }

            if (preview != null)
            {
                // El clip recién construido, aplicado SOBRE EL NODO DEL ANIMATOR. Sus rutas son
                // relativas al ViewModel (así se construyen en BuildClip), así que muestrearlo
                // contra la raíz del prefab no resuelve ni una ruta: no se movía un solo hueso y
                // cada previsualización salía idéntica a la anterior — tres pasadas creyendo que
                // el cambio no hacía nada cuando lo que fallaba era el muestreo.
                Transform animatorNode = null;
                foreach (var t in instanceForPreview.GetComponentsInChildren<Transform>(true))
                    if (t.name == AnimatorNodeName) { animatorNode = t; break; }
                result.Idle.SampleAnimation((animatorNode != null ? animatorNode : instanceForPreview.transform).gameObject, 0f);

                // Y EL OBJETO EN SU SITIO NUEVO. El clip sólo lleva el esqueleto; la posición del
                // modelo bajo la mano vive en el prefab, que en previsualización no se escribe. Sin
                // esto la captura enseña el agarre nuevo con el objeto en el sitio VIEJO — y parece
                // que el cambio no ha hecho nada.
                foreach (var t in instanceForPreview.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != spec.NodeName) continue;
                    Transform handBone = null;
                    foreach (var h in instanceForPreview.GetComponentsInChildren<Transform>(true))
                        if (h.name == "Hand.R") { handBone = h; break; }
                    if (handBone != null) t.SetParent(handBone, false);
                    t.localPosition = result.NodeLocalPosition;
                    t.localRotation = result.NodeLocalRotation;
                    t.localScale = result.NodeLocalScale;
                    break;
                }
                preview(instanceForPreview);
                UnityEngine.Object.DestroyImmediate(instanceForPreview);
                Debug.Log($"{spec.Tag} (previsualización, sin escribir) {result.Report}");
                return true;
            }

            var idle = SaveClip(result.Idle, $"{spec.AnimFolder}/{spec.ClipPrefix}_Idle.anim", $"{spec.ClipPrefix}_Idle");
            var equip = SaveClip(result.Equip, $"{spec.AnimFolder}/{spec.ClipPrefix}_Equip.anim", $"{spec.ClipPrefix}_Equip");
            var holster = SaveClip(result.Holster, $"{spec.AnimFolder}/{spec.ClipPrefix}_Holster.anim", $"{spec.ClipPrefix}_Holster");
            AssetDatabase.SaveAssets();

            WritePrefab(spec, result, idle, equip, holster);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"{spec.Tag} {result.Report}");
            return true;
        }

        private sealed class Result
        {
            public AnimationClip Idle, Equip, Holster;
            public Vector3 NodeLocalPosition;
            public Quaternion NodeLocalRotation;
            public Vector3 NodeLocalScale;
            public string Report;
        }

        // ─── El solver ───────────────────────────────────────────────────────────────────────────

        private static Result Solve(GameObject instance, Spec spec)
        {
            var rig = ReadRig(instance, spec);
            // El nodo pasa a colgar de Hand.R: el aplicador lo deja bajo el hueso del donante, y
            // todo lo que sigue —pose, dedos— lo mide como hijo de la mano.
            rig.Node.SetParent(rig.HandR, false);
            rig.Node.localPosition = Vector3.zero;
            rig.Node.localRotation = Quaternion.identity;
            rig.Node.localScale = Vector3.one;

            var vendor = FindVendorClips(rig);
            if (vendor.Idle == null || vendor.Equip == null || vendor.Holster == null)
            {
                Debug.LogError($"{spec.Tag} Faltan clips del vendor (idle/equip/holster) en el FBX del avatar.");
                return null;
            }

            var report = new StringBuilder();
            AnimationMode.StartAnimationMode();

            // ── 1. La pose de referencia: idle del vendor, fotograma 0 ──
            Sample(rig, vendor.Idle, 0f);
            if (!BackroomsDonorGrip.TryReadHandle(instance, spec.DonorSkinNode, spec.TipChildHint,
                    out var donorBone, out var axisLocal, out var fistLocal, out float donorRadius, spec.Tag))
            {
                Debug.LogError($"{spec.Tag} No se puede leer el mango del donante '{spec.DonorSkinNode}'.");
                return null;
            }
            Vector3 handleAxis = donorBone.TransformDirection(axisLocal).normalized;
            Vector3 vendorFist = donorBone.TransformPoint(fistLocal);

            Vector3 eye = rig.Camera.position;
            var poleR = rig.InstanceRoot.TransformDirection(ElbowPoleRight);
            var poleL = rig.InstanceRoot.TransformDirection(ElbowPoleLeft);

            Vector3 fist, axis;
            Quaternion rigid;
            if (spec.KeepVendorFraming)
            {
                // El puño y el eje se quedan como el vendor los animó: desplazamiento rígido nulo.
                // No se barre el alabeo (no hay nada que reorientar) y equipar/enfundar conservan su
                // recorrido exacto. Sólo cambian los dedos y la izquierda.
                fist = vendorFist;
                axis = handleAxis;
                rigid = Quaternion.identity;
                report.AppendLine("encuadre del donante conservado: sólo se hornean los dedos" +
                                  (spec.TuckLeftHand ? " y la izquierda recogida." : "."));
            }
            else
            {
                fist = spec.DesignAxisOnly
                    ? vendorFist
                    : eye + rig.InstanceRoot.TransformDirection(spec.FistFromEye);
                axis = rig.InstanceRoot.TransformDirection(
                    Quaternion.Euler(spec.PitchDownDegrees, spec.YawDegrees, 0f) * Vector3.forward);
                float vendorTwist = TwistDegrees(Quaternion.Inverse(rig.ForearmR.rotation) * rig.HandR.rotation);
                rigid = ChooseRoll(rig, vendor.Idle, handleAxis, axis, vendorFist, fist, poleR, vendorTwist, report);
            }

            // ── 2. El objeto en el puño ──
            // LA MANO SE DEJA EN SU ESTADO FINAL ANTES DE MEDIR NADA: brazo colocado, neutra y
            // muñeca escritas. Todo lo que viene después (línea de nudillos, punto de agarre, cierre
            // de dedos) se mide contra ESTA mano. Medirlo antes de escribir la muñeca dejaba
            // posición y orientación calculadas sobre dos manos distintas: la orientación salía
            // clavada y los dedos quedaban a 6 cm del objeto, cerrados al tope sin tocar nada.
            if (!spec.KeepVendorFraming)
            {
                var placedHand = MoveRigid(rig.HandR, rigid, vendorFist, fist);
                SolveTwoBone(rig.UpperArmR, rig.ForearmR, rig.HandR, placedHand.pos, placedHand.rot, poleR, rightSide: true);
                DistributeForearmTwist(rig.ForearmR, rig.HandR, rig.TwistR);
            }
            ApplyNeutralHand(rig);
            SetWrist(rig, spec);

            // EL ANCLA ES LO QUE LA MANO ENCIERRA, no el origen del hueso del donante. Ese origen
            // puede caer a palmos del puño (el del hacha está en la base del mango, no donde agarra
            // la mano), y colocar ahí el objeto lo dejaba fuera del puño: los dedos, buscando una
            // superficie que no estaba, se cerraban al máximo y salían en garra. Con el centro que
            // encierra la mano —media de las segundas falanges y la yema del pulgar— el objeto nace
            // DENTRO del puño sea cual sea el donante, y a los dedos sólo les queda ajustar el radio.
            // EL OBJETO VA DONDE ESTABA EL DEL DONANTE, corrido por la diferencia de RADIO. La
            // mano del vendor ya rodea un cilindro —el mango del hacha, el palo de la antorcha— con
            // una pose anatómicamente perfecta; cambiar ese cilindro por otro más gordo o más fino
            // sólo pide apartarlo o acercarlo esa diferencia. Anclarlo a la línea de nudillos (lo
            // que probé antes) ignora dónde agarra ESTA mano: salían dedos atravesando el objeto
            // 2 cm, o el objeto empujado 4 cm fuera de la mano para que cupiera.
            float gripLocalY = (spec.FistFromTail - 0.5f) * spec.LengthMeters;
            float gripRadius = RadiusAt(rig, Mathf.Clamp(gripLocalY, rig.ProfileMinY, rig.ProfileMaxY));
            // −Z de la mano es hacia donde cierran los dedos en este rig (ADR-133 enm. 1).
            // EL ANCLA SALE DE LOS DEDOS, no de una dirección supuesta. Se cierra la mano a un puño
            // genérico y se mide el HUECO que encierran las falanges medias: ahí es donde cae el eje
            // de cualquier cosa agarrada, por definición. Antes lo anclaba a la línea de nudillos y
            // lo separaba «hacia la palma» usando el −Z del hueso de la mano; con la muñeca
            // reescrita ese eje ya no apunta a la palma, así que la lata se iba a la muñeca y la
            // mano salía vacía en la vista del jugador.
            PreCurlFingers(rig, 0.55f);
            Vector3 knuckleLine = Vector3.zero, enclosed = Vector3.zero;
            for (int f = 1; f < rig.FingersR.Length; f++)
            {
                knuckleLine += rig.FingersR[f][0].position;
                enclosed += (rig.FingersR[f][1].position + rig.FingersR[f][2].position) * 0.5f;
            }
            knuckleLine /= rig.FingersR.Length - 1;
            enclosed /= rig.FingersR.Length - 1;

            // Y la dirección para apartarlo de la palma también se MIDE: va de los nudillos al hueco
            // que encierran los dedos. Es la única que no depende de cómo esté orientado el hueso.
            Vector3 palmNormal = Vector3.ProjectOnPlane(enclosed - knuckleLine, axis).normalized;
            if (palmNormal.sqrMagnitude < 1e-6f) palmNormal = -rig.HandR.forward;

            Vector3 gripPoint = enclosed;
            report.AppendLine($"radio de agarre {gripRadius * 1000f:0.0} mm; el eje se ancla al hueco que " +
                              $"encierran los dedos, a {(enclosed - knuckleLine).magnitude * 1000f:0.0} mm de los nudillos.");

            Vector3 up = rig.InstanceRoot.up;
            Vector3 lateral = Vector3.ProjectOnPlane(up, axis).normalized;
            if (lateral.sqrMagnitude < 1e-6f) lateral = Vector3.ProjectOnPlane(rig.InstanceRoot.forward, axis).normalized;
            // El punto de agarre del OBJETO (a FistFromTail de su culata) se lleva a lo que la mano
            // encierra; de ahí sale dónde cae su centro.
            Vector3 nodeCentre = gripPoint + axis * (spec.LengthMeters * (0.5f - spec.FistFromTail));
            {
                // DÓNDE CAE EL EJE SE BARRE, no se deduce. Es el tercer intento y los dos anteriores
                // fallaron por SUPONER algo distinto:
                //  1) «apartarlo de la palma hasta que nada penetre» usaba la peor penetración de
                //     las quince articulaciones, y la falange del medio se hunde A PROPÓSITO por la
                //     flecha de su cuerda (enm. 4). El buscador leía como choque lo que el ajustador
                //     acababa de aceptar: saturaba el tope de 22 mm en todas las pasadas, y por eso
                //     tres algoritmos distintos dieron el mismo número byte a byte.
                //  2) medir sólo la yema arregló el destornillador y no el bote, porque el sentido
                //     seguía escrito a mano: la dirección «apartar de la palma» está tomada de los
                //     nudillos AL hueco, o sea que apunta a la palma, y con ella el bote se metía
                //     22 mm más adentro de la mano en cada pasada.
                // Ahora se prueban las dos direcciones, milímetro a milímetro, y gana la posición
                // que deja las cuatro yemas dentro del margen que exige el test. El sentido es un
                // resultado de la medida, no una premisa.
                Vector3 away = Vector3.ProjectOnPlane(palmNormal, axis).normalized;
                Vector3 origin = nodeCentre;
                float bestOffset = 0f, bestCost = float.MaxValue;
                for (int mm = -(int)(MaxPalmPull * 1000f); mm <= (int)(MaxPalmPush * 1000f); mm += 2)
                {
                    float offset = mm * 0.001f;
                    PlaceNode(rig, origin + away * offset, axis, lateral);
                    ApplyNeutralHand(rig);
                    SetWrist(rig, spec);
                    var probe = new FitStats();
                    FitRightHand(rig, spec, probe);
                    if (!probe.HasWrapTip) break;

                    // El coste es lo que le sobra al PEOR dedo para caer en el margen. Meterse
                    // dentro del objeto pesa el triple que quedarse corto: un dedo que atraviesa la
                    // malla se ve, y uno que no llega parece que sujeta flojo.
                    float cost = 0f;
                    if (probe.WorstWrapTipDepth < -TipInside) cost += (-TipInside - probe.WorstWrapTipDepth) * 3f;
                    if (probe.WorstWrapTipGap > TipOutside) cost += probe.WorstWrapTipGap - TipOutside;
                    // Y EL INDICE SOBRE EL PULSADOR CUENTA IGUAL QUE LOS QUE RODEAN. Sin esta linea
                    // el barrido corria la lata 16 mm para que los tres dedos agarrasen redondo y
                    // dejaba el indice 6,9 cm por debajo del boton: el test del pulsador, que estaba
                    // en verde, se puso en rojo por arreglar el de al lado.
                    if (probe.HasNozzleTip && Mathf.Abs(probe.NozzleTipGap) > TipOutside)
                        cost += (Mathf.Abs(probe.NozzleTipGap) - TipOutside) * 2f;
                    // A igualdad, el sitio que menos mueve el objeto respecto del hueco del puño.
                    cost += Mathf.Abs(offset) * 0.01f;
                    if (cost < bestCost) { bestCost = cost; bestOffset = offset; }
                }
                nodeCentre = origin + away * bestOffset;
                PlaceNode(rig, nodeCentre, axis, lateral);
                report.AppendLine($"el eje se corre {bestOffset * 1000f:+0.0;-0.0} mm sobre la normal de la palma " +
                                  $"(+ hacia la palma, barrido {-MaxPalmPull * 1000f:0} a {MaxPalmPush * 1000f:0} mm); " +
                                  $"le sobran {bestCost * 1000f:0.0} mm al peor dedo.");
            }

            Vector3 nodeForward = Vector3.Cross(lateral, axis).normalized;
            var nodeRotation = Quaternion.LookRotation(nodeForward, axis);

            // EL OFFSET SE CALCULA CONTRA LA MANO FINAL, la que va a quedar horneada. Antes se
            // calculaba contra la mano recién desplazada, ANTES de escribir la muñeca; como el objeto
            // cuelga de Hand.R, se llevaba después mi giro de muñeca y apuntaba a otro sitio: pedía
            // 72° hacia arriba y salía 50°. Aquí se reproduce exactamente el estado del horneado
            // —muestreo, brazo, neutra, muñeca— y se lee la mano de ahí.
            // La mano ya está en su estado final desde el bloque 2; se lee tal cual.
            var handPose0 = new Pose { pos = rig.HandR.position, rot = rig.HandR.rotation };
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
            report.AppendLine($"nodo bajo Hand.R: pos {Fmt(result.NodeLocalPosition)} rot {Fmt(result.NodeLocalRotation.eulerAngles)}.");

            // ── 3. Los tres clips del vendor, recolocados ──
            var stats = new FitStats();
            var grip = new RightGrip();
            result.Idle = BakeVendorClip(rig, spec, vendor.Idle, rigid, vendorFist, fist, poleR, poleL, stats, grip, loop: true);
            result.Equip = BakeVendorClip(rig, spec, vendor.Equip, rigid, vendorFist, fist, poleR, poleL, stats, grip, loop: false);
            result.Holster = BakeVendorClip(rig, spec, vendor.Holster, rigid, vendorFist, fist, poleR, poleL, stats, grip, loop: false);
            report.AppendLine($"idle {result.Idle.length:F2} s, equipar {result.Equip.length:F2} s, enfundar " +
                              $"{result.Holster.length:F2} s; IK recortado en {stats.ReachClamps} fotogramas; " +
                              $"dedos: hueco medio {stats.MeanGap * 1000f:F1} mm, peor penetración " +
                              $"{stats.WorstPenetration * 1000f:F1} mm, peor hueco {stats.WorstGap * 1000f:F1} mm.");
            report.Append(stats.Description);

            AnimationMode.StopAnimationMode();
            result.Report = report.ToString();
            return result;
        }

        private static AnimationClip BakeVendorClip(Rig rig, Spec spec, AnimationClip source, Quaternion rigid,
            Vector3 vendorFist, Vector3 fist, Vector3 poleR, Vector3 poleL, FitStats stats, RightGrip grip, bool loop)
        {
            int frames = Mathf.Max(1, Mathf.RoundToInt(source.length * FrameRate));
            var list = new List<Frame>(frames + 1);
            for (int k = 0; k <= frames; k++)
            {
                float t = Mathf.Min(source.length, k / FrameRate);
                Sample(rig, source, loop && k == frames ? 0f : t);

                // Con el encuadre del vendor conservado el brazo derecho NO se toca: resolverlo por
                // IK contra su propia pose podría mover el codo sin necesidad. Sólo se recoloca
                // cuando hay un desplazamiento rígido que aplicar.
                if (!spec.KeepVendorFraming)
                {
                    var hand = MoveRigid(rig.HandR, rigid, vendorFist, fist);
                    if (!SolveTwoBone(rig.UpperArmR, rig.ForearmR, rig.HandR, hand.pos, hand.rot, poleR, rightSide: true))
                        stats.ReachClamps++;
                    DistributeForearmTwist(rig.ForearmR, rig.HandR, rig.TwistR);
                }

                // EL AGARRE SE RESUELVE UNA VEZ y se repite: puño y objeto son una pieza rígida, así
                // que la relación dedo-objeto no cambia con el balanceo. Resolverlo por fotograma da
                // lo mismo con paso de 1°, y ese grado entre fotogramas vecinos es tembleque.
                if (grip.Joints == null)
                {
                    // NEUTRA Y MUÑECA ANTES DE CERRAR, siempre: `Sample` acaba de reescribir la mano
                    // entera con el puño del donante. Sin esto el agarre se resolvía encima de un
                    // puño de hacha y dos corridas seguidas daban dos manos distintas.
                    ApplyNeutralHand(rig);
                    SetWrist(rig, spec);
                    FitRightHand(rig, spec, stats);
                    grip.Joints = rig.FingersR.Select(chain => chain.Select(j => j.localRotation).ToArray()).ToArray();
                    stats.Description = DescribeGrip(rig);
                }
                else
                {
                    SetWrist(rig, spec);
                    for (int f = 0; f < rig.FingersR.Length; f++)
                        for (int j = 0; j < rig.FingersR[f].Length; j++)
                            rig.FingersR[f][j].localRotation = grip.Joints[f][j];
                }

                // LA IZQUIERDA, FUERA. El clip del hacha pone las dos manos en el mango; un
                // destornillador se coge con una. Se recoge el brazo entero abajo, fuera del
                // encuadre, y la mano se deja relajada (los dedos del clip donante ya no agarran
                // nada, así que se abren un poco para que no queden en garra).
                if (spec.TuckLeftHand) TuckLeft(rig, spec, poleL);

                list.Add(Capture(rig, t));
            }
            return BuildClip(rig, list, source.length, loop);
        }

        /// <summary>
        /// Manda la izquierda abajo y fuera del encuadre, y le abre los dedos: una mano que no
        /// sujeta nada no se queda cerrada en el aire. El objetivo es una posición de diseño
        /// respecto del ojo, y el codo cae hacia fuera como en el brazo derecho.
        /// </summary>
        private static void TuckLeft(Rig rig, Spec spec, Vector3 poleL)
        {
            Vector3 eye = rig.Camera.position;
            Vector3 target = eye + rig.InstanceRoot.TransformDirection(spec.LeftHandFromEye);
            // La muñeca mira hacia dentro, con la palma al cuerpo: es como cae un brazo suelto.
            Quaternion rot = Quaternion.LookRotation(rig.InstanceRoot.forward,
                rig.InstanceRoot.TransformDirection(new Vector3(-0.4f, -1f, 0f)).normalized);
            SolveTwoBone(rig.UpperArmL, rig.ForearmL, rig.HandL, target, rot, poleL, rightSide: false);
            DistributeForearmTwist(rig.ForearmL, rig.HandL, rig.TwistL);

            // Dedos a medio cerrar, que es como cuelga una mano vacía: se ABRE lo que el clip del
            // hacha traía cerrado sobre el mango. El pulgar cierra sobre −Z en la izquierda.
            for (int f = 0; f < rig.FingersL.Length; f++)
            {
                bool thumb = f == 0;
                var chain = rig.FingersL[f];
                for (int j = thumb ? 1 : 0; j < chain.Length; j++)
                    chain[j].localRotation = chain[j].localRotation *
                        Quaternion.AngleAxis(thumb ? 10f : 18f, thumb ? Vector3.forward : Vector3.right);
            }
        }

        // ─── Dedos ───────────────────────────────────────────────────────────────────────────────

        private delegate float Surface(Vector3 worldPoint);

        /// <summary>Distancia con signo de un punto del mundo a la piel del objeto (positivo fuera),
        /// contra el PERFIL DE RADIO real de su malla: un destornillador no es un cilindro, y los
        /// dedos tienen que tocar la piel que hay en SU tramo (el mango, no el vástago).</summary>
        private static float SurfaceGap(Rig rig, Vector3 worldPoint)
        {
            Vector3 l = rig.Node.InverseTransformPoint(worldPoint);
            float r = Mathf.Sqrt(l.x * l.x + l.z * l.z);
            float y = Mathf.Clamp(l.y, rig.ProfileMinY, rig.ProfileMaxY);
            float radius = RadiusAt(rig, y);
            if (l.y < rig.ProfileMinY || l.y > rig.ProfileMaxY)
            {
                float beyond = l.y < rig.ProfileMinY ? rig.ProfileMinY - l.y : l.y - rig.ProfileMaxY;
                float outward = Mathf.Max(0f, r - radius);
                return Mathf.Sqrt(outward * outward + beyond * beyond);
            }
            return r - radius;
        }

        /// <summary>El pulsador de la boquilla: una bola en la punta del objeto, sobre su eje. Es
        /// contra esto —y no contra el cuerpo— contra lo que cierra el índice de un bote de spray.</summary>
        private static float NozzleGap(Rig rig, Spec spec, Vector3 worldPoint)
        {
            Vector3 l = rig.Node.InverseTransformPoint(worldPoint);
            float y = rig.ProfileMaxY - spec.NozzleDropMeters;
            return (l - new Vector3(0f, y, 0f)).magnitude - spec.NozzleRadiusMeters;
        }

        /// <summary>
        /// LA POSE NEUTRA: identidad en los quince huesos de dedo y en la muñeca. En este rig eso es
        /// el cero goniométrico de verdad — cada falange apunta por +Y a su hijo, así que los
        /// segmentos quedan colineales y PIP y DIP salen a 0,00° exactos. El abanico de la mano NO
        /// se pierde: vive en la POSICIÓN de los metacarpianos, no en su rotación.
        ///
        /// Hay que llamarla DESPUÉS de cada muestreo, porque el clip reescribe la mano entera con el
        /// puño del donante. Lo que yo llamaba «reposo» era ese puño: medido, la pose guardada en el
        /// prefab del destornillador es la del hacha byte a byte, y la del bote la de la antorcha.
        /// Partir de ahí es lo que dejaba las manos deformadas y el horneado no reproducible.
        /// </summary>
        private static void ApplyNeutralHand(Rig rig)
        {
            rig.HandR.localRotation = Quaternion.identity;
            foreach (var chain in rig.FingersR)
                foreach (var joint in chain)
                    joint.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// Cierra los cuatro dedos que rodean a una fracción de su tope, sin mirar ningún objeto:
        /// un puño genérico. Sirve para PREGUNTARLE A LA MANO dónde agarra, que es lo que hay que
        /// saber antes de colocar nada. Con la mano abierta esa pregunta no tiene respuesta.
        /// </summary>
        private static void PreCurlFingers(Rig rig, float fraction)
        {
            float[] caps = { 90f, 105f, 75f };
            for (int f = 1; f < rig.FingersR.Length; f++)
                for (int j = 0; j < rig.FingersR[f].Length; j++)
                    rig.FingersR[f][j].localRotation = rig.FingersR[f][j].localRotation *
                        Quaternion.AngleAxis(-caps[Mathf.Min(j, caps.Length - 1)] * fraction, Vector3.right);
        }

        /// <summary>
        /// La muñeca por ángulos con nombre, sobre la neutra: con Hand.R a identidad sus ejes son los
        /// del antebrazo, así que extensión va sobre X y la desviación cubital sobre Z.
        /// </summary>
        private static void SetWrist(Rig rig, Spec spec)
        {
            rig.HandR.localRotation =
                Quaternion.AngleAxis(spec.WristExtensionDegrees, Vector3.right) *
                Quaternion.AngleAxis(spec.WristUlnarDegrees, Vector3.forward);
        }

        /// <summary>Ángulo entre dos falanges consecutivas: lo que mide un goniómetro y lo que miden
        /// los tests. No es lo que se escribe en el hueso.</summary>
        private static float JointAngle(Transform[] chain, int j, float tipLength)
        {
            Vector3 previous = chain[j].position -
                (j > 0 ? chain[j - 1].position : chain[0].parent.position);
            Vector3 current = EndOf(chain, j, tipLength) - chain[j].position;
            if (previous.sqrMagnitude < 1e-10f || current.sqrMagnitude < 1e-10f) return 0f;
            return Vector3.Angle(previous, current);
        }

        /// <summary>
        /// EL AGARRE, CONSTRUIDO DESDE LA NEUTRA. Cada articulación parte de cero y sólo puede
        /// FLEXIONAR, hasta su tope: la hiperextensión —causa número uno del «bulto»— no está en el
        /// dominio, así que no hace falta ningún filtro que pueda fallar. Gana el primer ángulo que
        /// TOCA viniendo del cierre máximo, que es el criterio del horneador de la linterna: elegir
        /// por menor hueco saca el dedo tendido a lo largo del objeto, porque el contacto tangente
        /// también «toca».
        /// </summary>
        private static void FitRightHand(Rig rig, Spec spec, FitStats stats)
        {
            Surface surface = p => SurfaceGap(rig, p);
            Surface nozzle = p => NozzleGap(rig, spec, p);
            float gripY = (spec.FistFromTail - 0.5f) * spec.LengthMeters;
            float gripRadius = RadiusAt(rig, Mathf.Clamp(gripY, rig.ProfileMinY, rig.ProfileMaxY));

            for (int f = 0; f < rig.FingersR.Length; f++)
            {
                bool thumb = f == 0;
                bool index = f == 1;
                var chain = rig.FingersR[f];
                float skin = thumb ? ThumbSkin : FingerSkin;
                bool onNozzle = index && spec.IndexOnNozzle;
                Surface target = onNozzle ? nozzle : surface;

                // El eje de cierre está ESCRITO, no medido: en este rig los dedos cierran sobre −X y
                // el pulgar derecho sobre +Z (ADR-133 enm. 1). Medirlo por «qué acerca la yema a la
                // palma» falla: con el dedo muy cerrado la yema vuelve a subir por encima de los
                // nudillos y el signo sale al revés en cuatro de cada cinco dedos.
                Vector3 curlAxis = thumb ? Vector3.forward : Vector3.right;
                float sign = thumb ? +1f : -1f;

                // Topes por articulación, de la base a la punta. Son los de una mano real.
                float[] caps = thumb ? new[] { 50f, 55f, 60f } : new[] { 90f, 105f, 75f };

                if (thumb && spec.ThumbAlongTool)
                {
                    // EL PULGAR TENDIDO SE TIENDE ENTERO. El medio giro de antes era un parche contra
                    // un bulto que se medía partiendo del puño del hacha; desde la neutra no aparece.
                    var b = chain[0];
                    b.rotation = Quaternion.FromToRotation(b.up, rig.Node.up) * b.rotation;
                }

                int firstJoint = thumb && spec.ThumbAlongTool ? 1 : 0;
                for (int j = firstJoint; j < chain.Length; j++)
                {
                    var start = chain[j].localRotation;
                    float cap = caps[Mathf.Min(j, caps.Length - 1)];
                    Quaternion chosen = start;
                    bool touched = false;
                    float bestReach = float.MaxValue;
                    // El cierre MAS CERRADO que no se mete en el objeto, tocando o no. Es el sitio
                    // donde descansa un dedo al que no le da para agarrar.
                    Quaternion clear = start;
                    bool haveClear = false;
                    // Y si NINGÚN ángulo queda limpio —pasa: el meñique es corto y una lata de
                    // 64 mm de diámetro se lo traga entero—, el que menos la atraviesa.
                    Quaternion shallowest = start;
                    float shallowestDepth = float.MaxValue;

                    // DOS CRITERIOS, y la diferencia importa. Un dedo que RODEA gana con el primer
                    // ángulo que no penetra viniendo del cierre máximo (si no, el contacto tangente
                    // lo deja tendido a lo largo del objeto). Un dedo que va a BUSCAR un botón
                    // pequeño necesita el contrario: el ángulo que más se ACERCA. Con el criterio de
                    // rodear, el índice del bote se cerraba del todo —el puño cerrado tampoco
                    // penetra el pulsador, así que el primer candidato ganaba— y quien acababa
                    // encima del pulsador era el corazón.
                    for (float deg = cap; deg >= 0f; deg -= 1f)
                    {
                        chain[j].localRotation = start * Quaternion.AngleAxis(deg * sign, curlAxis);
                        Vector3 a = chain[j].position, b = EndOf(chain, j, rig.TipLengthR[f]);
                        float endGap = target(b) - skin;
                        float midGap = target(Vector3.Lerp(a, b, 0.5f)) - skin;

                        // La falange es RECTA y el objeto redondo: al rodearlo, su punto medio queda
                        // por dentro de la piel por la flecha de la cuerda, ≈ L²/8R. En un mango de
                        // radio 12 mm son 10 mm; con un tope fijo no pasaba ningún cierre.
                        float length = (b - a).magnitude;
                        float sagitta = length * length / (8f * Mathf.Max(0.004f, gripRadius + skin));
                        float midTolerance = Mathf.Max(skin / 3f, sagitta * 0.75f);

                        bool crossesBody = onNozzle &&
                            (surface(b) - skin < -midTolerance ||
                             surface(Vector3.Lerp(a, b, 0.5f)) - skin < -midTolerance);

                        if (onNozzle)
                        {
                            // El que más se acerca al pulsador sin cruzar la lata.
                            if (!crossesBody && Mathf.Abs(endGap) < bestReach)
                            {
                                bestReach = Mathf.Abs(endGap);
                                chosen = chain[j].localRotation;
                                touched = true;
                            }
                            continue;
                        }

                        // TOCAR ES TOCAR, no «no chocar». Bastaba con no penetrar, y un dedo al que
                        // el objeto le queda LEJOS no penetra en ningun angulo: ganaba el primero
                        // del barrido, que es el cierre MAXIMO, y el dedo se cerraba del todo sobre
                        // el aire — el meñique del bote, que Joel vio antes que yo. Ahora, si en
                        // todo el recorrido no hay contacto, este dedo NO ha tocado y lo dice: es
                        // asi como el buscador de sitio se entera de que el objeto no esta al
                        // alcance de esta mano y lo acerca.
                        if (!crossesBody && -endGap < shallowestDepth)
                        {
                            shallowestDepth = -endGap;
                            shallowest = chain[j].localRotation;
                        }

                        if (endGap >= -0.0015f && midGap >= -midTolerance && !crossesBody)
                        {
                            if (!haveClear) { clear = chain[j].localRotation; haveClear = true; }
                            if (endGap <= TipOutside)
                            {
                                chosen = chain[j].localRotation;
                                touched = true;
                                break;
                            }
                        }
                    }

                    // Sin contacto en todo el recorrido: el objeto no está al alcance de ESE dedo.
                    // Se deja en el cierre MÁS CERRADO que no lo atraviesa — un dedo que no llega
                    // descansa contra el objeto, no dentro. El 45 % del tope que había antes era un
                    // número inventado y caía DENTRO de la lata: el meñique acababa 7 mm por dentro
                    // de la chapa. Si ni siquiera eso existe, se queda relajado.
                    chain[j].localRotation = touched ? chosen
                        : haveClear ? clear
                        : shallowestDepth < float.MaxValue ? shallowest
                        : start * Quaternion.AngleAxis(cap * 0.45f * sign, curlAxis);

                    if (!onNozzle)
                    {
                        float jointGap = surface(EndOf(chain, j, rig.TipLengthR[f])) - skin;
                        stats?.Add(jointGap);
                        if (!thumb && j == chain.Length - 1) stats?.AddWrapTip(jointGap);
                    }
                    else if (j == chain.Length - 1 && stats != null)
                    {
                        stats.NozzleTipGap = nozzle(EndOf(chain, j, rig.TipLengthR[f])) - skin;
                        stats.HasNozzleTip = true;
                    }
                }
            }
        }

        /// <summary>
        /// Cierra un dedo falange a falange hasta TOCAR la superficie: para cada articulación, de la
        /// base a la punta, se barre del cierre MÁXIMO al abierto y gana el PRIMER ángulo que no
        /// penetra. Dos pasadas, para que la base cierre lo que la primera no pudo. Copiado del
        /// horneador de la linterna (ADR-133 enm. 1), que es donde se midió cada una de estas
        /// decisiones; mirar sólo el extremo de ESA falange es lo que evita el dedo «en tienda».
        /// </summary>
        /// <param name="avoid">Superficie que el dedo NO puede atravesar aunque no sea la que busca.
        /// Es lo que impide que el índice del bote cruce por dentro de la lata camino del pulsador.</param>
        /// <param name="flexSign">Hacia qué lado FLEXIONA este dedo, medido contra la palma. El
        /// barrido sólo va en ese sentido: una articulación no se dobla hacia atrás.</param>
        /// <param name="maxFlex">Tope ABSOLUTO de doblez entre falanges, en grados, de la base a la punta.</param>
        private static void FitFinger(Transform[] chain, float tipLength, Vector3 curlAxis, float skin,
            Surface surface, Surface avoid, int firstJoint, float gripRadius, float flexSign,
            float[] maxFlex, FitStats stats)
        {
            for (int pass = 0; pass < 2; pass++)
            for (int j = firstJoint; j < chain.Length; j++)
            {
                var start = chain[j].localRotation;
                // SIN SUPONER EL SIGNO. El barrido va de −80° a +80° y gana el ángulo que deja el
                // extremo de la falange TOCANDO (|hueco| mínimo) sin penetrar. Antes se recorría
                // desde el cierre máximo dando por hecho que cerrar era negativo, y se aceptaba el
                // primero que no penetraba: con estos dos objetos el cierre resultó ser POSITIVO
                // (medido: la yema del índice se acerca a 9 mm en +40° y se aleja a 24 mm en −80°),
                // así que el primer ángulo del barrido ya valía y los dedos se quedaban abiertos
                // — tres pasadas con las yemas a 4-7 cm del eje.
                float bestContact = float.MaxValue, bestScore = float.MaxValue;
                Quaternion best = start, bestFallback = start;
                bool found = false;
                // SÓLO EN EL SENTIDO EN QUE EL DEDO FLEXIONA. Barrer los dos sentidos dejaba
                // articulaciones dobladas HACIA ATRÁS: la malla se pliega sobre sí misma y el dedo
                // sale como un bulto — «totalmente deformados», y con el hueco a la superficie
                // perfecto, porque medir la distancia al objeto no dice nada de si la pose es
                // posible. El barrido es amplio a propósito; quien manda es el tope ABSOLUTO de
                // doblez que se comprueba dentro. Los 5° de contra son la hiperextensión real.
                float limit = maxFlex[Mathf.Min(j, maxFlex.Length - 1)];
                for (int step = -5; step <= 120; step++)
                {
                    float deg = step * flexSign;
                    chain[j].localRotation = start * Quaternion.AngleAxis(deg, curlAxis);
                    Vector3 a = chain[j].position, b = EndOf(chain, j, tipLength);
                    float endGap = surface(b) - skin;
                    float midGap = surface(Vector3.Lerp(a, b, 0.5f)) - skin;

                    // LA TOLERANCIA DEL MEDIO SALE DE LA GEOMETRÍA, no de un número fijo. Una falange
                    // es RECTA y la superficie es redonda: al rodearla, el medio de la falange queda
                    // por dentro de la piel por la flecha de su cuerda, ≈ L²/8R. En el mango de un
                    // destornillador (radio 12 mm) son 10 mm, muy por encima del tope fijo anterior.
                    float length = (b - a).magnitude;
                    float sagitta = length * length / (8f * Mathf.Max(0.004f, gripRadius + skin));
                    float midTolerance = Mathf.Max(skin / 3f, sagitta * 0.75f);

                    bool clearsAvoid = avoid == null ||
                        (avoid(b) - skin >= -0.002f && avoid(Vector3.Lerp(a, b, 0.5f)) - skin >= -midTolerance);

                    // EL DOBLEZ ES ABSOLUTO, no lo que yo añado. La pose de partida ya viene cerrada
                    // sobre el objeto del donante, así que limitar sólo el giro AÑADIDO sumaba encima
                    // y salían falanges a 175° — el dedo doblado en dos. Se mide el ángulo real entre
                    // esta falange y la anterior, y que doble HACIA LA PALMA y no hacia el dorso.
                    Vector3 previousBone = a - (j > 0 ? chain[j - 1].position : chain[0].parent.position);
                    Vector3 thisBone = b - a;
                    bool anatomical = true;
                    if (previousBone.sqrMagnitude > 1e-10f && thisBone.sqrMagnitude > 1e-10f)
                        anatomical = Vector3.Angle(previousBone, thisBone) <= limit;
                    // El SENTIDO ya lo garantiza el barrido (sólo flexión desde el reposo). Exigir
                    // además que cada falange apunte más hacia la palma que la anterior parecía
                    // razonable y NO lo es: pasada la mitad del cierre, la falange distal de un puño
                    // vuelve a alejarse de la palma. Con esa condición no había pose que rodease el
                    // objeto y el buscador lo apartaba 7 cm de la mano hasta que «cabía».
                    if (!anatomical) continue;

                    if (endGap >= -0.0015f && midGap >= -midTolerance && clearsAvoid)
                    {
                        if (Mathf.Abs(endGap) < bestContact)
                        {
                            bestContact = Mathf.Abs(endGap);
                            best = chain[j].localRotation;
                            found = true;
                        }
                        continue;
                    }
                    if (!clearsAvoid) continue;
                    float score = Mathf.Abs(endGap) + (midGap < -midTolerance ? 1f - midGap : 0f) +
                                  (endGap < -0.0015f ? 1f - endGap : 0f);
                    if (score < bestScore) { bestScore = score; bestFallback = chain[j].localRotation; }
                }
                chain[j].localRotation = found ? best : bestFallback;
                if (pass == 1) stats?.Add(surface(EndOf(chain, j, tipLength)) - skin);
            }
        }

        private static Vector3 EndOf(Transform[] chain, int j, float tipLength)
            => j + 1 < chain.Length ? chain[j + 1].position : chain[j].TransformPoint(0f, tipLength, 0f);

        /// <summary>
        /// Hacia qué lado FLEXIONA el dedo: el giro que lleva la yema hacia la palma. Se prueban los
        /// dos y gana el que la acerca. Es anatomía de la mano, así que se mide contra la palma y no
        /// contra el objeto — con el objeto como referencia, un dedo que no lo alcanza «flexiona»
        /// hacia donde le acerque, aunque sea doblando la articulación hacia atrás.
        /// </summary>
        private static float FlexSign(Transform[] chain, float tipLength, Vector3 curlAxis, Vector3 palmNormal)
        {
            var saved = chain.Select(j => j.localRotation).ToArray();
            Vector3 palmPoint = chain[0].position + palmNormal * 0.03f;

            float Reach(float sign)
            {
                for (int j = 0; j < chain.Length; j++)
                    chain[j].localRotation = saved[j] * Quaternion.AngleAxis(30f * sign, curlAxis);
                float d = Vector3.Distance(EndOf(chain, chain.Length - 1, tipLength), palmPoint);
                for (int j = 0; j < chain.Length; j++) chain[j].localRotation = saved[j];
                return d;
            }

            return Reach(1f) <= Reach(-1f) ? 1f : -1f;
        }

        /// <summary>El centro de lo que encierra una mano cerrada: la media de las segundas falanges
        /// de los cuatro dedos y de la yema del pulgar. Es donde está el objeto cuando la mano lo
        /// tiene agarrado de verdad (misma medida que usa el horneador de la linterna).</summary>
        private static Vector3 EnclosedCentre(Transform[][] fingers, float[] tipLengths)
        {
            Vector3 sum = Vector3.zero;
            for (int f = 1; f < fingers.Length; f++) sum += fingers[f][1].position;
            sum += fingers[0][2].TransformPoint(0f, tipLengths[0], 0f);
            return sum / fingers.Length;
        }

        /// <summary>Una línea por dedo con la distancia de cada falange a la piel: es lo que
        /// convierte «se ve bien» en un número, y lo que miden los tests.</summary>
        private static string DescribeGrip(Rig rig)
        {
            var sb = new StringBuilder();
            string[] names = { "pulgar", "índice", "corazón", "anular", "meñique" };
            // El objeto, en su propio espacio: alto del perfil y radio a media altura. Sin esto,
            // «el dedo está a 4 cm» no dice si sobra dedo o falta objeto.
            sb.AppendLine($"  objeto: y de {rig.ProfileMinY * 100f:0.0} a {rig.ProfileMaxY * 100f:0.0} cm, " +
                          $"radio medio {RadiusAt(rig, (rig.ProfileMinY + rig.ProfileMaxY) * 0.5f) * 1000f:0.0} mm.");
            // DÓNDE ESTÁ EL OBJETO CUANDO SE HORNEA, no cuando se sondeó: si estos dos no coinciden,
            // el agarre se midió en un sitio y se guardó en otro.
            float minKnuckle = float.MaxValue;
            for (int f = 1; f < rig.FingersR.Length; f++)
                minKnuckle = Mathf.Min(minKnuckle, SurfaceGap(rig, rig.FingersR[f][0].position));
            Vector3 nodeInHand = rig.HandR.InverseTransformPoint(rig.Node.position);
            sb.AppendLine($"  al hornear: nudillo más cercano {minKnuckle * 1000f:+0.0;-0.0} mm, nodo en local de " +
                          $"Hand.R {Fmt(nodeInHand)}, escala del nodo {Fmt(rig.Node.lossyScale)}.");
            for (int f = 0; f < rig.FingersR.Length; f++)
            {
                sb.Append($"  {names[f]}:");
                for (int j = 0; j < rig.FingersR[f].Length; j++)
                {
                    Vector3 end = EndOf(rig.FingersR[f], j, rig.TipLengthR[f]);
                    float gap = SurfaceGap(rig, end) - (f == 0 ? ThumbSkin : FingerSkin);
                    sb.Append($" {gap * 1000f:+0.0;-0.0} mm");
                }
                // Y DÓNDE cae la yema en el espacio del objeto: altura sobre su eje y distancia al
                // eje. Un dedo «lejos» puede estarlo por encima de la punta (le sobra objeto) o por
                // fuera del radio (no cierra), y son dos arreglos distintos.
                Vector3 tip = rig.Node.InverseTransformPoint(EndOf(rig.FingersR[f], 2, rig.TipLengthR[f]));
                sb.AppendLine($"   yema en y {tip.y * 100f:+0.0;-0.0} cm, a {Mathf.Sqrt(tip.x * tip.x + tip.z * tip.z) * 1000f:0} mm del eje.");
            }
            return sb.ToString();
        }

        // ─── Rig ─────────────────────────────────────────────────────────────────────────────────

        private sealed class Rig
        {
            /// <summary>La RAÍZ DEL PREFAB, que es el marco de referencia de «adelante» y «arriba».
            /// NO el hueso llamado «Root»: sus ejes son los del esqueleto y no coinciden — pedir 6°
            /// de cabeceo contra él dejaba el destornillador apuntando 77° hacia ARRIBA (medido).</summary>
            public Transform InstanceRoot;
            public Transform Animator, Root, Camera;
            public Transform HandR, ForearmR, UpperArmR, HandL, ForearmL, UpperArmL;
            public Transform[] TwistR, TwistL;
            public Transform Node;
            public Mesh NodeMesh;
            public Transform[] Skeleton;
            public string[] Paths;
            public Transform[][] FingersR, FingersL;
            public float[] TipLengthR, TipLengthL;
            /// <summary>Rotaciones de las falanges derechas en la pose del PREFAB (bind), leídas
            /// antes de muestrear ningún clip: es el punto de partida NEUTRO desde el que se
            /// cierran los dedos. Partir de la pose del donante los dejaba en garra — un puño
            /// cerrado sobre el mango de un hacha ya está más cerrado que el barrido puede abrir.</summary>
            public Quaternion[][] FingerRestR;
            public float[] RadiusProfile;
            public float ProfileMinY, ProfileMaxY;
        }

        private static Rig ReadRig(GameObject instance, Spec spec)
        {
            var byName = new Dictionary<string, Transform>();
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;

            Transform Need(string n) => byName.TryGetValue(n, out var t) ? t
                : throw new InvalidOperationException($"falta el hueso o nodo '{n}' en el prefab");

            var rig = new Rig
            {
                InstanceRoot = instance.transform,
                Animator = Need(AnimatorNodeName),
                Root = Need(RootBoneName),
                Camera = Need(CameraBoneName),
                HandR = Need("Hand.R"), ForearmR = Need("Forearm.R"), UpperArmR = Need("UpperArm.R"),
                HandL = Need("Hand.L"), ForearmL = Need("Forearm.L"), UpperArmL = Need("UpperArm.L"),
                TwistR = new[] { Need("ForearmTwist.2.R"), Need("ForearmTwist.3.R"), Need("ForearmTwist.4.R") },
                TwistL = new[] { Need("ForearmTwist.2.L"), Need("ForearmTwist.3.L"), Need("ForearmTwist.4.L") },
                Node = Need(spec.NodeName),
            };
            rig.NodeMesh = rig.Node.GetComponentInChildren<MeshFilter>(true)?.sharedMesh
                ?? throw new InvalidOperationException($"el nodo '{spec.NodeName}' no trae malla");

            string[] fingerNames = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
            rig.FingersR = fingerNames.Select(f => new[] { Need($"{f}.1.R"), Need($"{f}.2.R"), Need($"{f}.3.R") }).ToArray();
            rig.FingersL = fingerNames.Select(f => new[] { Need($"{f}.1.L"), Need($"{f}.2.L"), Need($"{f}.3.L") }).ToArray();
            rig.TipLengthR = rig.FingersR.Select(c => c[2].localPosition.magnitude * 0.9f).ToArray();
            rig.TipLengthL = rig.FingersL.Select(c => c[2].localPosition.magnitude * 0.9f).ToArray();
            // Antes de muestrear ningún clip: la pose del prefab es la de bind, y es el neutro desde
            // el que se cierra por contacto.
            rig.FingerRestR = rig.FingersR.Select(c => c.Select(j => j.localRotation).ToArray()).ToArray();

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

            ReadRadiusProfile(rig);
            return rig;
        }

        /// <summary>Radio del objeto por tramo de su largo (percentil 90 por bin): ni el
        /// destornillador ni el bote son cilindros perfectos, y el mango es más gordo que el
        /// vástago.</summary>
        private static void ReadRadiusProfile(Rig rig)
        {
            const int bins = 36;
            var vertices = rig.NodeMesh.vertices;
            var lists = new List<float>[bins];
            for (int i = 0; i < bins; i++) lists[i] = new List<float>();
            var b = rig.NodeMesh.bounds;
            rig.ProfileMinY = b.min.y;
            rig.ProfileMaxY = b.max.y;
            float span = Mathf.Max(1e-5f, b.size.y);
            foreach (var v in vertices)
            {
                int bin = Mathf.Clamp(Mathf.FloorToInt((v.y - b.min.y) / span * bins), 0, bins - 1);
                lists[bin].Add(Mathf.Sqrt(v.x * v.x + v.z * v.z));
            }
            rig.RadiusProfile = new float[bins];
            for (int i = 0; i < bins; i++)
            {
                if (lists[i].Count == 0) { rig.RadiusProfile[i] = i > 0 ? rig.RadiusProfile[i - 1] : 0f; continue; }
                lists[i].Sort();
                rig.RadiusProfile[i] = lists[i][Mathf.Clamp((int)(lists[i].Count * 0.9f), 0, lists[i].Count - 1)];
            }
        }

        private static float RadiusAt(Rig rig, float y)
        {
            float span = Mathf.Max(1e-5f, rig.ProfileMaxY - rig.ProfileMinY);
            float f = Mathf.Clamp01((y - rig.ProfileMinY) / span) * (rig.RadiusProfile.Length - 1);
            int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, rig.RadiusProfile.Length - 2);
            return Mathf.Lerp(rig.RadiusProfile[i], rig.RadiusProfile[i + 1], f - i);
        }

        /// <summary>Coloca el nodo del modelo en una pose de MUNDO: centro, eje y costado.</summary>
        private static void PlaceNode(Rig rig, Vector3 centre, Vector3 axis, Vector3 lateral)
        {
            Vector3 forward = Vector3.Cross(lateral, axis).normalized;
            rig.Node.SetPositionAndRotation(centre, Quaternion.LookRotation(forward, axis));
        }

        // ─── Clips del vendor ────────────────────────────────────────────────────────────────────

        private struct VendorClips { public AnimationClip Idle, Equip, Holster; }

        /// <summary>
        /// Los clips ORIGINALES del FBX del avatar, no los overrides del prefab: tras el primer
        /// horneado los overrides son los NUESTROS, y muestrearlos apilaría desplazamientos
        /// (ADR-133 enm. 1).
        /// </summary>
        private static VendorClips FindVendorClips(Rig rig)
        {
            var clips = new VendorClips();
            var animator = rig.Animator.GetComponent<Animator>();
            if (animator == null || animator.avatar == null) return clips;
            string path = AssetDatabase.GetAssetPath(animator.avatar);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is not AnimationClip clip || clip.name.StartsWith("__preview__")) continue;
                string n = clip.name.ToLowerInvariant();
                if (clips.Idle == null && n.Contains("idle")) clips.Idle = clip;
                if (clips.Equip == null && n.Contains("equip")) clips.Equip = clip;
                if (clips.Holster == null && n.Contains("holster")) clips.Holster = clip;
            }
            return clips;
        }

        // ─── Muestreo, IK y torsión ──────────────────────────────────────────────────────────────

        private static void Sample(Rig rig, AnimationClip clip, float t)
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(rig.Animator.gameObject, clip, t);
            AnimationMode.EndSampling();
            rig.Animator.localPosition = Vector3.zero;
            rig.Animator.localRotation = Quaternion.identity;
        }

        private struct Pose { public Vector3 pos; public Quaternion rot; }

        private static Pose MoveRigid(Transform t, Quaternion rigid, Vector3 pivotFrom, Vector3 pivotTo)
            => new() { pos = pivotTo + rigid * (t.position - pivotFrom), rot = rigid * t.rotation };

        private sealed class Frame { public float Time; public Vector3[] Pos; public Quaternion[] Rot; }

        private sealed class FitStats
        {
            public string Description = "";
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

            // LA YEMA DE LOS CUATRO QUE RODEAN, aparte. Es lo unico que mide el test de agarre, y
            // mezclarla con las falanges de la base falseaba la busqueda: la base de un dedo que
            // rodea SIEMPRE queda lejos de la superficie, asi que el peor hueco del monton nunca
            // bajaba y el buscador no tenia forma de saber si las yemas tocaban.
            public bool HasWrapTip;
            public float WorstWrapTipGap;      // el que mas LEJOS se queda
            public float WorstWrapTipDepth;    // el que mas DENTRO se mete

            /// <summary>La yema del indice contra el PULSADOR, cuando ese es su trabajo. Va aparte
            /// porque no rodea nada: si el buscador de sitio solo mira a los que rodean, mueve la
            /// lata hasta que los tres agarran de maravilla y deja el indice a 7 cm del boton.</summary>
            public bool HasNozzleTip;
            public float NozzleTipGap;
            public void AddWrapTip(float gap)
            {
                if (!HasWrapTip || gap > WorstWrapTipGap) WorstWrapTipGap = gap;
                if (!HasWrapTip || gap < WorstWrapTipDepth) WorstWrapTipDepth = gap;
                HasWrapTip = true;
            }
        }

        private sealed class RightGrip { public Quaternion[][] Joints; }

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

        /// <summary>IK analítico de dos huesos, con el codo como bisagra sobre la Z local (−Z en el
        /// derecho, +Z en el izquierdo). Copiado de ADR-133 enm. 1.</summary>
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
        /// Barre el alabeo del puño alrededor del eje del objeto y se queda con el que deja la
        /// torsión de muñeca más parecida a la del vendor, con el pulgar en la mitad de arriba.
        /// A ojo salió la mano boca abajo (ADR-133 enm. 1): esto se barre, no se escribe.
        /// </summary>
        private static Quaternion ChooseRoll(Rig rig, AnimationClip idle, Vector3 handleAxis, Vector3 axis,
            Vector3 vendorFist, Vector3 fist, Vector3 poleR, float vendorTwist, StringBuilder report)
        {
            var baseRotation = Quaternion.FromToRotation(handleAxis, axis);
            Vector3 upRef = Vector3.ProjectOnPlane(rig.InstanceRoot.up, axis).normalized;
            Vector3 rightRef = Vector3.Cross(upRef, axis).normalized;
            float bestScore = float.MaxValue, bestRoll = 0f, bestTwist = 0f, bestThumb = 0f;
            for (float roll = -180f; roll < 180f; roll += RollScanStepDegrees)
            {
                Sample(rig, idle, 0f);
                var rigid = Quaternion.AngleAxis(roll, axis) * baseRotation;
                var hand = MoveRigid(rig.HandR, rigid, vendorFist, fist);
                SolveTwoBone(rig.UpperArmR, rig.ForearmR, rig.HandR, hand.pos, hand.rot, poleR, rightSide: true);
                float twist = TwistDegrees(Quaternion.Inverse(rig.ForearmR.rotation) * rig.HandR.rotation);
                Vector3 d = rig.FingersR[0][0].position - fist;
                Vector3 perp = d - axis * Vector3.Dot(d, axis);
                float thumbClock = Mathf.Atan2(Vector3.Dot(perp, rightRef), Vector3.Dot(perp, upRef)) * Mathf.Rad2Deg;
                float score = Mathf.Abs(Mathf.DeltaAngle(twist, vendorTwist)) + (Mathf.Abs(thumbClock) > 90f ? 1000f : 0f);
                if (score < bestScore) { bestScore = score; bestRoll = roll; bestTwist = twist; bestThumb = thumbClock; }
            }
            Sample(rig, idle, 0f);
            report.AppendLine($"alabeo {bestRoll:+0}°: torsión de muñeca {bestTwist:0}° (vendor {vendorTwist:0}°), " +
                              $"pulgar a las {bestThumb:0}°.");
            return Quaternion.AngleAxis(bestRoll, axis) * baseRotation;
        }

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

        // ─── Escritura ───────────────────────────────────────────────────────────────────────────

        private static AnimationClip BuildClip(Rig rig, List<Frame> frames, float length, bool loop)
        {
            var clip = new AnimationClip { frameRate = FrameRate };
            for (int i = 0; i < rig.Skeleton.Length; i++)
            {
                var px = new AnimationCurve(); var py = new AnimationCurve(); var pz = new AnimationCurve();
                var rx = new AnimationCurve(); var ry = new AnimationCurve(); var rz = new AnimationCurve(); var rw = new AnimationCurve();
                Quaternion previous = frames[0].Rot[i];
                foreach (var f in frames)
                {
                    var q = f.Rot[i];
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

        /// <summary>Una curva que no cambia se guarda con DOS claves: el idle pasó de 26 MB a 4,7
        /// en la linterna (ADR-133 enm. 1).</summary>
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

        private static AnimationClip SaveClip(AnimationClip clip, string path, string name)
        {
            clip.name = name;
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(clip, existing);
                UnityEngine.Object.DestroyImmediate(clip);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        /// <summary>
        /// Escribe en el prefab la pose del nodo y los overrides. El controller NO se toca: estos
        /// objetos usan el del vendor y sólo cambian los clips, así que la lista de pares tiene que
        /// seguir teniendo EXACTAMENTE <c>controller.animationClips.Length</c> entradas sin
        /// deduplicar, o el inspector del vendor la regenera vacía (ADR-133 enm. 1).
        /// </summary>
        private static void WritePrefab(Spec spec, Result result, AnimationClip idle, AnimationClip equip,
            AnimationClip holster)
        {
            var root = PrefabUtility.LoadPrefabContents(spec.PrefabPath);
            try
            {
                Transform hand = null, node = null;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Hand.R") hand ??= t;
                    else if (t.name == spec.NodeName) node ??= t;
                }
                if (hand == null || node == null)
                    throw new InvalidOperationException("el prefab ha perdido Hand.R o el nodo del modelo");

                node.SetParent(hand, false);
                node.localPosition = result.NodeLocalPosition;
                node.localRotation = result.NodeLocalRotation;
                node.localScale = result.NodeLocalScale;

                var wieldableAnimator = root.GetComponentInChildren<PolymindGames.WieldableSystem.WieldableAnimator>(true);
                if (wieldableAnimator == null)
                    throw new InvalidOperationException("el prefab no tiene WieldableAnimator");
                var so = new SerializedObject(wieldableAnimator);
                var controller = so.FindProperty("_clips._controller").objectReferenceValue as AnimatorController;
                if (controller == null)
                    throw new InvalidOperationException("el WieldableAnimator no tiene controller");

                var originals = controller.animationClips;
                var pairs = so.FindProperty("_clips._clips");
                pairs.arraySize = originals.Length;
                int overridden = 0;
                for (int i = 0; i < originals.Length; i++)
                {
                    var e = pairs.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("Original").objectReferenceValue = originals[i];
                    var over = OverrideFor(originals[i], idle, equip, holster);
                    e.FindPropertyRelative("Override").objectReferenceValue = over;
                    if (over != null) overridden++;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"{spec.Tag} {overridden}/{originals.Length} clips del controller '{controller.name}' con override propio.");

                PrefabUtility.SaveAsPrefabAsset(root, spec.PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static AnimationClip OverrideFor(AnimationClip original, AnimationClip idle, AnimationClip equip,
            AnimationClip holster)
        {
            string n = original.name.ToLowerInvariant();
            if (n.Contains("equip")) return equip;
            if (n.Contains("holster")) return holster;
            // «Use» también es el idle: ninguna de las dos herramientas tiene animación de uso propia.
            if (n.Contains("idle") || n.Contains("use")) return idle;
            return null;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }
}
#endif
