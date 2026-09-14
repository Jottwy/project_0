using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-023: shows the peer's held wieldable on their 3P proxy by attaching a model to the
    /// right-hand bone (<c>Hand.R</c>) driven by the networked <c>view.heldItem</c> item ID. Unlike
    /// clothing (ADR-022, a pre-placed wardrobe), the held item is DYNAMIC, so this is new content:
    /// we resolve the item's world <see cref="ItemPickup"/> prefab by id, instantiate its mesh under
    /// the hand, and neutralize the pickup's physics/interaction components (renderers only).
    ///
    /// Slice 2 adds per-CATEGORY grip fidelity: the held item's FPSCore category (de-prefixed
    /// <c>ParentGroup.Name</c>: "Melee"/"Firearms"/"Tools", + fallback) selects a <see cref="GripPoseSet.CategoryGrip"/>
    /// that (a) places the model under Hand.R and (b) curls the 15 right-hand finger bones into a
    /// static grip (parametric curl around a shared bend axis), applied in LateUpdate AFTER the
    /// Animator (same ordering as <see cref="ProxyPitchHook"/>). Placement + finger pose are re-applied
    /// every LateUpdate so editing the <see cref="GripPoseSet"/> asset calibrates live during Play.
    ///
    /// Bones are resolved BY NAME (the rig is GENERIC). LIMITATION: grip is RIGHT-HAND ONLY — two-handed
    /// items (rifle/bow/spear) leave Hand.L free. Reads the held id from the RemotePlayerManager view
    /// whose root is this GameObject (same lookup as <see cref="ProxyClothingHook"/>). Attach to the
    /// avatar root. Removable: delete the file and the proxy shows empty hands; nothing else breaks.
    /// </summary>
    public sealed class ProxyHeldItemHook : MonoBehaviour
    {
        [Header("Anchor")]
        [Tooltip("Hand bone name on the MaleSurvivor skeleton to attach the held model to.")]
        [SerializeField] private string _handBoneName = "Hand.R";

        [Header("Grip configuration (per-category placement + finger pose)")]
        [Tooltip("Per-category grip poses. Calibrate the asset live during Play.")]
        [SerializeField] private GripPoseSet _gripPoses;

        // ─── Agarre medido (objetos con manivela: la linterna) ───────────────────────────────────────────
        // AJUSTES DE CÓDIGO, `readonly` y NO serializados a propósito: el valor por defecto de un [SerializeField] queda
        // congelado en el prefab ya importado y un número cambiado aquí no llega al juego ni avisa (medido 14-09:
        // _holdDown 0,60 -> 0,28 dio una captura idéntica al decimal). Cambiarlos es cambiar el código.

        // Aire entre el borde del meñique y la manivela, a lo largo del eje del objeto (m).
        private readonly float _crankClearance = 0.01f;

        // Muñeca por debajo del hombro, en fracción del largo del brazo. 0,72 -> 0,60 (subirla un poco) -> 0,28:
        // Joel pidió las manos al pecho con la linterna a dos manos (14-09); a 0,60 quedaban a la altura de la cadera.
        private readonly float _holdDown = 0.28f;

        // Muñeca por delante del hombro, en fracción del largo del brazo: el brazo algo levantado.
        private readonly float _holdForward = 0.56f;

        // Muñeca hacia fuera del hombro (m). Con 0 la mano quedaba delante de la entrepierna (captura 14-09).
        private readonly float _holdOutward = 0.06f;

        // Codo hacia fuera, en fracción del largo del brazo: cuanto menos, más pegado al costado.
        private readonly float _elbowOutward = 0.15f;

        // Muñeca respecto del hombro cuando el objeto es a DOS MANOS: negativa = hacia el centro, para que la izquierda
        // llegue al pomo (m). Era −0,07; Joel pidió la izquierda «más espaciada» (14-09).
        private readonly float _twoHandOutward = -0.03f;

        // Por debajo de esta distancia entre los nudillos de las dos manos se castiga que se amontonen (m). Era 0,05.
        private readonly float _handsApart = 0.08f;

        // Cierre de anular y meñique de la mano del pomo, que no caben en una pieza corta: se RECOGEN (lección de la
        // izquierda en la manivela de primera persona, ADR-150 enm. 3).
        private readonly float _tuckedFingersDegrees = 75f;

        // Giro PREFERIDO de la palma hacia abajo, en grados. Sólo desempata: el giro real lo elige la búsqueda.
        private readonly float _palmDownDegrees = 20f;

        // La linterna se lleva algo inclinada hacia el suelo de delante, sumada al cabeceo del vecino. Con 0 y
        // cabeceo 0 el haz salía horizontal a la altura de la cadera y no alumbraba nada cercano.
        private readonly float _beamDownBias = 12f;

        // Tope del cabeceo que sigue la linterna, en grados.
        private readonly float _beamPitchClamp = 60f;

        // Right-hand finger bones of the MaleSurvivor skeleton, grouped by the two curl scalars.
        private static readonly string[] ThumbBoneNames =
        {
            "ThumbFinger.1.R", "ThumbFinger.2.R", "ThumbFinger.3.R",
        };
        private static readonly string[] FingerBoneNames =
        {
            "IndexFinger.1.R", "IndexFinger.2.R", "IndexFinger.3.R",
            "MiddleFinger.1.R", "MiddleFinger.2.R", "MiddleFinger.3.R",
            "RingFinger.1.R", "RingFinger.2.R", "RingFinger.3.R",
            "PinkyFinger.1.R", "PinkyFinger.2.R", "PinkyFinger.3.R",
        };

        // Sentinel that never equals a real item ID (0 = empty hands is a valid value), so the
        // first observed value always applies — including a peer holding nothing (0).
        private const int Unset = int.MinValue;

        private RemotePlayerManager _manager;
        private Transform _hand;
        private GameObject _instance;
        private int _applied = Unset;
        private string _category = "";
        // ADR-049: the carry hook owns the peer's hands while there are planks in them.
        private ProxyCarryHook _carryHook;

        private Transform[] _thumbBones;
        private Transform[] _fingerBones;
        private Quaternion[] _thumbBind;
        private Quaternion[] _fingerBind;

        // Agarre medido (paso 1 de la linterna, 2026-09-14).
        private Transform _upperArm, _lowerArm;
        private Transform _indexKnuckle, _middleKnuckle, _pinkyKnuckle, _thumbBase;
        private Transform[][] _curlChains;   // índice, corazón, anular, meñique
        private Transform[] _thumbChain;
        private bool _measured;
        private float _radius, _bodyCentreY, _halfLength, _crankY;
        private Vector2 _axisOffsetXZ;
        private float _pitch;
        private bool _curlSolved;
        private readonly float[] _curlDegrees = new float[4];
        private float _thumbDegrees;
        private bool _thumbAlongFinger;

        // Elección del brazo por naturalidad (port de la búsqueda de primera persona, 2026-09-14).
        // A la altura del pecho la muñeca derecha medía 54° de cubital con inclinación ±40° y giro 70°, y mirando 30°
        // abajo 112° de extensión: el barrido se quedaba corto. Más giro, inclinación hasta ±60° y la linterna algo
        // cruzada hacia la línea media (un gesto natural con dos manos).
        private static readonly float[] RollCandidates = { -50f, -20f, 10f, 40f, 70f, 100f };
        private static readonly float[] TiltCandidates = { -60f, -30f, 0f, 30f, 60f };
        private static readonly float[] HeightCandidates = { -0.08f, 0f, 0.08f };
        private static readonly float[] YawCandidates = { 0f, -15f };
        private const int PoleCount = 3;
        private const float RechooseDegrees = 8f;
        private const float RechooseSeconds = 0.5f;
        private bool _hasChoice;
        private float _choiceRoll, _choiceTilt, _choiceHeight, _choiceYaw, _choicePitch;
        private float _nextChooseTime;
        private int _choicePole;
        private Quaternion _upperRest, _lowerRest, _handRest;

        /// <summary>Coste de naturalidad del brazo derecho en el último fotograma (0 = cómodo). Lo lee el arnés.</summary>
        public float LastArmCost { get; private set; }

        // Mano izquierda en el pomo de la manivela: la linterna es de DOS manos (Joel, 14-09), igual que en primera
        // persona, donde la izquierda va en el pomo también en reposo y lo sigue al dar cuerda.
        private Transform _upperArmL, _lowerArmL, _handL;
        private Transform _indexKnuckleL, _middleKnuckleL, _pinkyKnuckleL, _thumbBaseL;
        private Transform[][] _curlChainsL;
        private Transform[] _thumbChainL;
        private Transform _crank;
        private Vector3 _knobLocal;
        private float _knobRadius;
        private bool _twoHanded;
        private bool _hasLeftChoice, _leftCurlSolved;
        private float _leftClock, _leftSign = 1f, _leftTilt;
        private static readonly float[] LeftTiltCandidates = { -30f, -15f, 0f, 15f, 30f };
        private int _leftPole;
        private readonly float[] _leftCurl = new float[2];
        private float _leftThumb;
        private Quaternion _upperLRest, _lowerLRest, _handLRest;

        /// <summary>Coste de naturalidad del brazo izquierdo en el último fotograma. Lo lee el arnés.</summary>
        public float LastLeftArmCost { get; private set; }

        /// <summary>Metros entre los nudillos de la izquierda y donde tendrían que estar para agarrar el pomo (0 = lo
        /// agarra). Lo lee el arnés: en una captura no se distingue «sobre el pomo» de «cerca del pomo».</summary>
        public float LastLeftKnobMiss { get; private set; }

        private bool HasLeftRig =>
            _handL != null && _upperArmL != null && _lowerArmL != null && _indexKnuckleL != null
            && _middleKnuckleL != null && _pinkyKnuckleL != null && _thumbBaseL != null;

        // ─── Libro abierto (bit BookOpen, 2026-09-14) ────────────────────────────────────────────────────
        // El libro de supervivencia no pasa por la funda y no llega como heldItem: llega como bit. Mientras está abierto
        // el objeto de la funda se ESCONDE (no se destruye: al cerrar vuelve sin reconstruirse) y el vecino sujeta con
        // las dos manos el libro abierto que hornea ProxyOpenBookBuilder.
        private const string OpenBookResource = "ProxyOpenBook";
        private static GameObject _openBookPrefab;
        private static bool _openBookLoaded;
        private static ProxyBookHold.Cover _bookCover;
        private GameObject _book;
        private bool _bookOpen, _hasBookChoice;
        private int _buttons;
        private bool _dead;
        private readonly BookHand _bookRight = new BookHand();
        private readonly BookHand _bookLeft = new BookHand();
        // Hasta ±70° de giro de los dedos y más abajo y más adentro en la tapa: la mano puede llegar desde DEBAJO del
        // libro. Con ±45° y cerca del canto la primera captura sacó los antebrazos horizontales (14-09).
        // ±90°: los dedos a lo alto de la página, para que no crucen el lomo (vista de frente a las tapas, 14-09).
        private static readonly float[] BookSpreadCandidates = { -90f, -70f, -45f, -20f, 0f, 20f, 45f, 70f, 90f };
        private static readonly float[] BookAlongCandidates = { -0.10f, -0.06f, -0.02f };
        private static readonly float[] BookInsetCandidates = { 0.03f, 0.06f, 0.09f };

        // Los dedos van detrás de la tapa ABIERTOS lo justo para no atravesarla y el pulgar se cierra hasta apoyarse en la
        // página (ProxyBookHold.OpenBehindPlane / ReachWithTip).
        private sealed class BookHand
        {
            public float Spread, Along, Inset;
            public int Pole;
            // Giro del pulgar resuelto al elegir la mano y reaplicado cada fotograma (barrer la rejilla cada fotograma sobra).
            public bool ThumbSolved;
            public float ThumbA, ThumbB;
        }

        // Se vuelven a elegir las manos cuando los hombros suben o bajan esto respecto del cuerpo (agacharse con el libro
        // abierto): la elección de pie, reaplicada agachado, dejaba la mano derecha sobre la página (captura 14-09).
        private const float BookRechooseMetres = 0.15f;
        private float _bookChoiceShoulderHeight;

        /// <summary>Coste de naturalidad de cada brazo con el libro abierto. Lo lee el arnés.</summary>
        public float LastBookRightCost { get; private set; }
        public float LastBookLeftCost { get; private set; }

        /// <summary>Metros entre los nudillos de cada mano y su sitio detrás de la tapa. Lo lee el arnés.</summary>
        public float LastBookMissRight { get; private set; }
        public float LastBookMissLeft { get; private set; }

        /// <summary>Lo más adelantado de los dedos de cada mano respecto de la tapa (m; negativo = todos detrás). Lo lee el arnés:
        /// en una captura no se distingue «detrás de la tapa» de «sobre la página».</summary>
        public float LastBookFrontRight { get; private set; }
        public float LastBookFrontLeft { get; private set; }

        /// <summary>Lo más metido en el libro de muñeca, base del pulgar y nudillos (m; negativo = fuera). Lo lee el arnés.</summary>
        public float LastBookPierceRight { get; private set; }
        public float LastBookPierceLeft { get; private set; }

        /// <summary>Metros entre la yema de cada pulgar y su sitio sobre la página. Lo lee el arnés.</summary>
        public float LastBookThumbMissRight { get; private set; }
        public float LastBookThumbMissLeft { get; private set; }

        /// <summary>El libro abierto mientras se pinta; si no, null. Lo lee el arnés.</summary>
        public Transform OpenBook => _bookOpen ? _book.transform : null;

        private void Awake()
        {
            var bones = BuildBoneMap();
            bones.TryGetValue(_handBoneName, out _hand);
            _carryHook = GetComponent<ProxyCarryHook>();
            CacheFingerChain(bones, ThumbBoneNames, out _thumbBones, out _thumbBind);
            CacheFingerChain(bones, FingerBoneNames, out _fingerBones, out _fingerBind);

            bones.TryGetValue("UpperArm.R", out _upperArm);
            bones.TryGetValue("LowerArm.R", out _lowerArm);
            bones.TryGetValue("IndexFinger.1.R", out _indexKnuckle);
            bones.TryGetValue("MiddleFinger.1.R", out _middleKnuckle);
            bones.TryGetValue("PinkyFinger.1.R", out _pinkyKnuckle);
            bones.TryGetValue("ThumbFinger.1.R", out _thumbBase);
            _curlChains = new[]
            {
                Chain(bones, "IndexFinger"), Chain(bones, "MiddleFinger"),
                Chain(bones, "RingFinger"), Chain(bones, "PinkyFinger"),
            };
            _thumbChain = Chain(bones, "ThumbFinger");

            bones.TryGetValue("UpperArm.L", out _upperArmL);
            bones.TryGetValue("LowerArm.L", out _lowerArmL);
            bones.TryGetValue("Hand.L", out _handL);
            bones.TryGetValue("IndexFinger.1.L", out _indexKnuckleL);
            bones.TryGetValue("MiddleFinger.1.L", out _middleKnuckleL);
            bones.TryGetValue("PinkyFinger.1.L", out _pinkyKnuckleL);
            bones.TryGetValue("ThumbFinger.1.L", out _thumbBaseL);
            _curlChainsL = new[]
            {
                Chain(bones, "IndexFinger", "L"), Chain(bones, "MiddleFinger", "L"),
                Chain(bones, "RingFinger", "L"), Chain(bones, "PinkyFinger", "L"),
            };
            _thumbChainL = Chain(bones, "ThumbFinger", "L");
        }

        private static Transform[] Chain(Dictionary<string, Transform> bones, string finger, string side = "R")
        {
            var chain = new Transform[3];
            for (int i = 0; i < 3; i++)
                bones.TryGetValue($"{finger}.{i + 1}.{side}", out chain[i]);
            return chain;
        }

        private bool HasHandRig =>
            _hand != null && _upperArm != null && _lowerArm != null && _indexKnuckle != null
            && _middleKnuckle != null && _pinkyKnuckle != null && _thumbBase != null;

        // Re-arm for pool reuse: drop any held model and force a re-apply against the fresh peer.
        private void OnEnable()
        {
            ClearInstance();
            _applied = Unset;
            _category = "";
            if (_book != null)
                _book.SetActive(false);
            _bookOpen = false;
            _hasBookChoice = false;
            _buttons = 0;
            _dead = false;
            ClearBookReadouts();
        }

        private void ClearBookReadouts()
        {
            LastBookRightCost = 0f;
            LastBookLeftCost = 0f;
            LastBookMissRight = 0f;
            LastBookMissLeft = 0f;
            LastBookFrontRight = 0f;
            LastBookFrontLeft = 0f;
            LastBookPierceRight = 0f;
            LastBookPierceLeft = 0f;
            LastBookThumbMissRight = 0f;
            LastBookThumbMissLeft = 0f;
        }

        // Change-detect the held id; (re)build the model + resolve its category on change.
        private void Update()
        {
            if (_hand == null)
                return;

            // ADR-049: planks win. A peer cannot be shouldering a stack of drywall and presenting a
            // rifle at the same time, and the carryable's own controller blocks the equivalent
            // locally. `_applied` is reset rather than kept so the held model rebuilds the moment the
            // planks are put down. Update order between the two hooks is undefined, so a single frame
            // of overlap is possible on the transition — both re-evaluate every frame and it closes
            // itself; anything tighter would mean coupling their execution order for one frame.
            if (_carryHook != null && _carryHook.IsCarrying)
            {
                ClearInstance();
                SetBookOpen(false);
                _category = "";
                _applied = Unset;
                return;
            }

            if (!TryResolveHeld(out int id))
                return;
            SetBookOpen(RemoteButtons.Has(_buttons, RemoteButtons.BookOpen) && !_dead);
            if (id == _applied)
                return;

            ClearInstance();
            _category = "";
            if (id != 0)
            {
                var def = DataDefinition<ItemDefinition>.GetWithId(id);
                if (def != null && def.ParentGroup != null)
                    _category = def.ParentGroup.Name; // de-prefixed: "Melee"/"Firearms"/"Tools"
                _instance = BuildHeldModel(def);
                // Cambiar de objeto con el libro abierto: el nuevo sale ya escondido y aparece al cerrarlo.
                if (_instance != null && _bookOpen)
                    _instance.SetActive(false);
            }
            _applied = id;
        }

        // Apply per-category placement + finger grip AFTER the Animator. Re-applied every frame so
        // GripPoseSet edits calibrate live; only runs while an item is held (empty hands → the
        // locomotion clip drives the hand naturally).
        private void LateUpdate()
        {
            if (_bookOpen)
            {
                ApplyBookHold();
                return;
            }

            if (_instance == null)
                return;

            if (_measured && HasHandRig)
            {
                ApplyMeasuredHold();
                return;
            }

            var grip = _gripPoses != null ? _gripPoses.Resolve(_category) : null;

            var t = _instance.transform;
            t.localPosition = grip != null ? grip.modelLocalPosition : Vector3.zero;
            t.localRotation = Quaternion.Euler(grip != null ? grip.modelLocalEuler : Vector3.zero);
            t.localScale = grip != null ? SafeScale(grip.modelLocalScale) : Vector3.one;

            Vector3 axis = _gripPoses != null ? _gripPoses.fingerBendAxis : Vector3.forward;
            float sign = (_gripPoses != null && _gripPoses.invertBend) ? -1f : 1f;
            ApplyCurl(_fingerBones, _fingerBind, (grip != null ? grip.fingerCurl : 0f) * sign, axis);
            ApplyCurl(_thumbBones, _thumbBind, (grip != null ? grip.thumbCurl : 0f) * sign, axis);
        }

        /// <summary>Resolves the item's pickup mesh, neutralizes it, and parents it to the hand.</summary>
        private GameObject BuildHeldModel(ItemDefinition def)
        {
            var pickup = def != null ? def.Pickup : null;
            if (pickup == null)
                return null; // unknown item / no world model → empty hands (graceful)

            var go = Instantiate(pickup.gameObject, _hand, false);
            ProxyRigUtil.NeutralizeToVisualOnly(go);
            ProxyRigUtil.SetLayerRecursive(go, _hand.gameObject.layer);
            MeasureHeldModel(go);
            // ADR-133: AFTER neutralizing (which destroys every MonoBehaviour on the model), and on
            // the model rather than on this avatar so it needs no re-bake of the prefab. Does
            // nothing for a model without a "Crank" child. See ProxyCrankHook for why it lives here.
            ProxyCrankHook.AttachIfCranked(go, transform);
            return go; // placement is applied in LateUpdate (live-calibratable)
        }

        /// <summary>
        /// ¿Se puede medir el agarre de este modelo? Hoy, sólo un cuerpo alargado en su +Y local con un hijo
        /// "Crank" (la linterna): es el único objeto cuyo eje, lente y pieza lateral están fijados por su
        /// propio creador (lente en +Y, manivela a −0,0206 m del centro). El resto sigue con GripPoseSet.
        /// Item-agnóstico como ProxyCrankHook: pregunta por la forma, no por el nombre.
        /// </summary>
        private void MeasureHeldModel(GameObject model)
        {
            _measured = false;
            _curlSolved = false;
            _hasChoice = false;
            _twoHanded = false;
            _hasLeftChoice = false;
            _leftCurlSolved = false;
            _crank = null;
            if (model == null)
                return;

            var filter = model.GetComponent<MeshFilter>();
            Transform crank = null;
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != ProxyCrankHook.CrankNodeName) continue;
                crank = t;
                break;
            }
            if (filter == null || filter.sharedMesh == null || crank == null)
                return;

            Bounds b = filter.sharedMesh.bounds;
            Vector3 e = b.extents;
            if (e.y < e.x || e.y < e.z)
                return; // no es alargado en +Y: no sabemos dónde está su lente

            _radius = (e.x + e.z) * 0.5f;
            _bodyCentreY = b.center.y;
            _halfLength = e.y;
            _axisOffsetXZ = new Vector2(b.center.x, b.center.z);
            _crankY = model.transform.InverseTransformPoint(crank.position).y;
            _measured = true;

            // EL POMO: el brazo de la manivela sale por su +Y local desde el pivote (malla medida: centro y 0,03,
            // semialto 0,03) y gira sobre su Z. El pomo se toma cerca del extremo del brazo.
            var crankFilter = crank.GetComponent<MeshFilter>();
            if (crankFilter != null && crankFilter.sharedMesh != null)
            {
                Bounds cb = crankFilter.sharedMesh.bounds;
                _knobLocal = new Vector3(cb.center.x, cb.center.y + cb.extents.y * 0.75f, cb.center.z);
                _knobRadius = Mathf.Max(0.006f, Mathf.Min(cb.extents.x, cb.extents.z) * 0.8f);
                _crank = crank;
                _twoHanded = true;
            }
        }

        /// <summary>
        /// Brazo algo levantado hacia delante (IK de dos huesos), nudillos hacia donde mira el vecino con la
        /// palma hacia el cuerpo, objeto a (radio + piel) de los nudillos por delante de la manivela y dedos
        /// cerrados hasta tocar. El cierre se resuelve UNA vez por objeto y luego sólo se aplica.
        /// </summary>
        private void ApplyMeasuredHold()
        {
            Transform root = transform;
            float pitch = Mathf.Clamp(_pitch + _beamDownBias, -_beamPitchClamp, _beamPitchClamp);
            Vector3 beam = Quaternion.AngleAxis(pitch, root.right) * root.forward;

            // EL BRAZO SE ELIGE POR NATURALIDAD, no se fuerza. Antes la mano se orientaba a la fuerza después del IK
            // y la muñeca se comía lo que no cuadraba («la muñeca medio torcida», Joel 14-09). Se barren el giro de
            // la palma sobre el eje del haz, el codo y cuánto adelantar la mano, y gana el de menor coste con la
            // medida de primera persona (ProxyArmNaturalness). Se elige al coger el objeto y cuando el cabeceo cambia
            // de verdad; entre medias se reaplica la elección — elegir en cada fotograma alterna y tiembla (ADR-150).
            if (!_hasChoice || (Mathf.Abs(pitch - _choicePitch) > RechooseDegrees && Time.time >= _nextChooseTime))
            {
                ChooseRightArm(beam, pitch);
                _nextChooseTime = Time.time + RechooseSeconds;
            }
            LastArmCost = PoseRightArm(_choiceRoll, _choiceTilt, _choicePole, _choiceHeight, _choiceYaw, beam);

            var frame = Frame();
            float grip = ProxyGripSolver.GripAlongAxis(_bodyCentreY, _halfLength, frame.Width, true, _crankY, _crankClearance);
            ProxyGripSolver.PlaceCylinder(frame, _radius, grip, out Vector3 position, out Quaternion rotation, _choiceTilt);
            position -= rotation * new Vector3(_axisOffsetXZ.x, 0f, _axisOffsetXZ.y);
            var t = _instance.transform;
            t.localScale = Vector3.one;
            t.SetPositionAndRotation(position, rotation);

            Vector3 axisPoint = position + rotation * new Vector3(_axisOffsetXZ.x, 0f, _axisOffsetXZ.y);
            Vector3 axisDir = rotation * Vector3.up;

            if (_twoHanded && HasLeftRig && _crank != null)
                ApplyLeftHandOnKnob(axisPoint, axisDir, frame.KnuckleCentre);

            if (!_curlSolved)
            {
                for (int i = 0; i < _curlChains.Length; i++)
                    _curlDegrees[i] = ProxyGripSolver.CurlToTouch(_curlChains[i], frame.KnuckleAxis, axisPoint, axisDir, _radius);

                // El pulgar no flexiona sobre la línea de nudillos: se prueba también sobre el eje de los dedos y
                // se queda el que llega a tocar con menos giro.
                float alongKnuckles = ProxyGripSolver.CurlToTouch(_thumbChain, frame.KnuckleAxis, axisPoint, axisDir, _radius);
                ProxyGripSolver.Curl(_thumbChain, frame.KnuckleAxis, -alongKnuckles);
                float alongFinger = ProxyGripSolver.CurlToTouch(_thumbChain, frame.FingerAxis, axisPoint, axisDir, _radius);
                _thumbAlongFinger = Mathf.Abs(alongFinger) > 0f
                    && (Mathf.Approximately(alongKnuckles, 0f) || Mathf.Abs(alongFinger) <= Mathf.Abs(alongKnuckles));
                if (!_thumbAlongFinger)
                {
                    ProxyGripSolver.Curl(_thumbChain, frame.FingerAxis, -alongFinger);
                    ProxyGripSolver.Curl(_thumbChain, frame.KnuckleAxis, alongKnuckles);
                }
                _thumbDegrees = _thumbAlongFinger ? alongFinger : alongKnuckles;
                _curlSolved = true;
                return;
            }

            for (int i = 0; i < _curlChains.Length; i++)
                ProxyGripSolver.Curl(_curlChains[i], frame.KnuckleAxis, _curlDegrees[i]);
            ProxyGripSolver.Curl(_thumbChain, _thumbAlongFinger ? frame.FingerAxis : frame.KnuckleAxis, _thumbDegrees);
        }

        /// <summary>
        /// Paso 2 de la linterna: dónde está la lente del objeto medido ESTE fotograma (el +Y de su cuerpo) y
        /// hacia dónde apunta. Falso si lo que hay en la mano no es de los medidos, y entonces ProxyLightHook se
        /// queda con su luz de antorcha. También falso con el objeto escondido por el libro abierto.
        /// Hay que leerlo después del LateUpdate de este hook.
        /// </summary>
        public bool TryGetBeam(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (!_measured || _instance == null || !_instance.activeSelf || !HasHandRig)
                return false;

            var t = _instance.transform;
            position = t.TransformPoint(new Vector3(_axisOffsetXZ.x, _bodyCentreY + _halfLength, _axisOffsetXZ.y));
            rotation = Quaternion.LookRotation(t.up, t.forward);
            return true;
        }

        private void ChooseRightArm(Vector3 beam, float pitch)
        {
            _upperRest = _upperArm.localRotation;
            _lowerRest = _lowerArm.localRotation;
            _handRest = _hand.localRotation;

            float best = float.MaxValue;
            float bestRoll = _choiceRoll, bestTilt = _choiceTilt, bestHeight = _choiceHeight, bestYaw = _choiceYaw;
            int bestPole = _choicePole;
            foreach (float yaw in YawCandidates)
            foreach (float roll in RollCandidates)
            foreach (float tilt in TiltCandidates)
            foreach (float height in HeightCandidates)
            for (int pole = 0; pole < PoleCount; pole++)
            {
                RestoreArm();
                float cost = PoseRightArm(roll, tilt, pole, height, yaw, beam)
                    + 0.2f * Sq((roll - _palmDownDegrees) / 60f)
                    + 0.1f * Sq(tilt / 40f)   // a igualdad, el objeto más alineado con los nudillos
                    + 0.1f * Sq(yaw / 15f);   // y apuntando recto
                // A igualdad se queda la que había: dos opciones de coste parecido no alternan.
                if (_hasChoice && Mathf.Approximately(roll, _choiceRoll) && Mathf.Approximately(tilt, _choiceTilt)
                    && Mathf.Approximately(height, _choiceHeight) && Mathf.Approximately(yaw, _choiceYaw) && pole == _choicePole)
                    cost -= 0.05f;
                if (cost >= best)
                    continue;
                best = cost;
                bestRoll = roll;
                bestTilt = tilt;
                bestHeight = height;
                bestYaw = yaw;
                bestPole = pole;
            }
            RestoreArm();

            // Cambiar de inclinación cambia dónde tocan los dedos: se vuelven a cerrar.
            if (!_hasChoice || !Mathf.Approximately(bestTilt, _choiceTilt) || !Mathf.Approximately(bestRoll, _choiceRoll))
                _curlSolved = false;
            _choiceRoll = bestRoll;
            _choiceTilt = bestTilt;
            _choiceHeight = bestHeight;
            _choiceYaw = bestYaw;
            _choicePole = bestPole;
            _choicePitch = pitch;
            _hasChoice = true;
        }

        private void RestoreArm()
        {
            _upperArm.localRotation = _upperRest;
            _lowerArm.localRotation = _lowerRest;
            _hand.localRotation = _handRest;
        }

        /// <summary>
        /// Pone el brazo derecho en un candidato y devuelve su coste de naturalidad: muñeca por IK de dos huesos con
        /// el codo hacia <paramref name="pole"/>, mano con los nudillos por el haz y la palma girada
        /// <paramref name="roll"/> grados desde la línea media, y la mitad de esa torsión pasada al antebrazo.
        /// </summary>
        private float PoseRightArm(float roll, float tilt, int pole, float height, float yaw, Vector3 beam)
        {
            Transform root = transform;
            beam = Quaternion.AngleAxis(yaw, root.up) * beam;
            float armLength = Vector3.Distance(_upperArm.position, _lowerArm.position)
                + Vector3.Distance(_lowerArm.position, _hand.position);
            Vector3 shoulder = _upperArm.position;
            Vector3 target = shoulder - root.up * ((_holdDown + height) * armLength)
                + root.forward * (_holdForward * armLength) + root.right * (_twoHanded ? _twoHandOutward : _holdOutward);
            ProxyGripSolver.TwoBoneIk(_upperArm, _lowerArm, _hand, target, shoulder + PoleDirection(pole) * armLength);

            var frame = Frame();
            Vector3 untwisted = frame.PalmNormal;
            Vector3 palmTarget = Quaternion.AngleAxis(roll, beam) * Vector3.ProjectOnPlane(-root.right, beam).normalized;
            // El objeto va por el haz y cruza el puño inclinado `tilt`: los nudillos quedan girados −tilt sobre la palma.
            Vector3 knuckles = Quaternion.AngleAxis(-tilt, palmTarget) * beam;
            Quaternion current = Quaternion.LookRotation(frame.KnuckleAxis, frame.PalmNormal);
            Quaternion wanted = Quaternion.LookRotation(knuckles, palmTarget);
            _hand.rotation = wanted * Quaternion.Inverse(current) * _hand.rotation;

            frame = Frame();
            ProxyArmNaturalness.ShareForearmTwist(_lowerArm, _hand, frame.PalmNormal, untwisted);

            var measure = ProxyArmNaturalness.Take(shoulder, _lowerArm.position, _hand.position, _middleKnuckle.position,
                frame.PalmNormal, frame.KnuckleAxis, root.right, root.up, true);
            return ProxyArmNaturalness.Cost(measure);
        }

        private Vector3 PoleDirection(int pole)
        {
            Transform root = transform;
            switch (pole)
            {
                case 0: return -root.up * 0.4f - root.forward * 0.5f + root.right * _elbowOutward; // atrás, algo fuera
                case 1: return -root.up - root.forward * 0.15f + root.right * 0.05f;                // abajo, pegado
                default: return -root.up * 0.5f - root.forward * 0.2f + root.right * 0.45f;         // abierto
            }
        }

        private static float Sq(float x) => x * x;

        /// <summary>
        /// La izquierda en el pomo, cada fotograma: el pomo gira con la cuerda y la mano lo sigue. La primera vez se
        /// barren entera la posición alrededor del pomo, el sentido de los nudillos y el codo; después sólo una
        /// ventana de ±30° alrededor de la elección anterior, así que la mano rueda con el pomo sin saltar
        /// (elegir → seguir cerca, lección del tembleque de primera persona).
        /// </summary>
        private void ApplyLeftHandOnKnob(Vector3 bodyAxisPoint, Vector3 bodyAxisDir, Vector3 rightKnuckles)
        {
            Vector3 knob = _crank.TransformPoint(_knobLocal);
            Vector3 axis = _crank.TransformDirection(Vector3.forward).normalized;

            _upperLRest = _upperArmL.localRotation;
            _lowerLRest = _lowerArmL.localRotation;
            _handLRest = _handL.localRotation;

            float best = float.MaxValue;
            float bestClock = _leftClock, bestSign = _leftSign, bestTilt = _leftTilt;
            int bestPole = _leftPole;

            void Try(float clock, float sign, float tilt, int pole)
            {
                RestoreLeftArm();
                float cost = PoseLeftArm(clock, sign, tilt, pole, knob, axis, bodyAxisPoint, bodyAxisDir, rightKnuckles)
                    + 0.1f * Sq(tilt / 30f);
                if (_hasLeftChoice && Mathf.Approximately(sign, _leftSign) && pole == _leftPole)
                    cost -= 0.05f;
                if (cost >= best)
                    return;
                best = cost;
                bestClock = clock;
                bestSign = sign;
                bestTilt = tilt;
                bestPole = pole;
            }

            if (!_hasLeftChoice)
            {
                // Con inclinación: el pomo cruza la mano en diagonal, lo mismo que dejó la muñeca derecha en 0.
                // Sin ella la izquierda medía 94° de pronación y 24° de flexión («la muñeca medio rota», Joel 14-09).
                for (float clock = 0f; clock < 360f; clock += 30f)
                    foreach (float sign in new[] { 1f, -1f })
                        foreach (float tilt in LeftTiltCandidates)
                            for (int pole = 0; pole < PoleCount; pole++)
                                Try(clock, sign, tilt, pole);
            }
            else
            {
                for (float clock = _leftClock - 30f; clock <= _leftClock + 30f + 1e-3f; clock += 15f)
                    for (float tilt = _leftTilt - 15f; tilt <= _leftTilt + 15f + 1e-3f; tilt += 15f)
                        Try(clock, _leftSign, Mathf.Clamp(tilt, -30f, 30f), _leftPole);
            }

            RestoreLeftArm();
            if (_hasLeftChoice && !Mathf.Approximately(bestSign, _leftSign))
                _leftCurlSolved = false;
            _leftClock = Mathf.Repeat(bestClock, 360f);
            _leftSign = bestSign;
            _leftTilt = bestTilt;
            _leftPole = bestPole;
            _hasLeftChoice = true;

            LastLeftArmCost = PoseLeftArm(_leftClock, _leftSign, _leftTilt, _leftPole, knob, axis, bodyAxisPoint, bodyAxisDir, rightKnuckles);

            var frame = FrameL();
            if (!_leftCurlSolved)
            {
                // Índice y corazón cierran hasta tocar el pomo; anular y meñique, que no caben, se recogen con el
                // mismo signo de cierre que el índice. El pulgar opone por el eje de los dedos.
                _leftCurl[0] = ProxyGripSolver.CurlToTouch(_curlChainsL[0], frame.KnuckleAxis, knob, axis, _knobRadius);
                _leftCurl[1] = ProxyGripSolver.CurlToTouch(_curlChainsL[1], frame.KnuckleAxis, knob, axis, _knobRadius);
                _leftThumb = ProxyGripSolver.CurlToTouch(_thumbChainL, frame.FingerAxis, knob, axis, _knobRadius);
                _leftCurlSolved = true;
            }
            else
            {
                ProxyGripSolver.Curl(_curlChainsL[0], frame.KnuckleAxis, _leftCurl[0]);
                ProxyGripSolver.Curl(_curlChainsL[1], frame.KnuckleAxis, _leftCurl[1]);
                ProxyGripSolver.Curl(_thumbChainL, frame.FingerAxis, _leftThumb);
            }
            float tuckSign = _leftCurl[0] < 0f ? -1f : 1f;
            ProxyGripSolver.Curl(_curlChainsL[2], frame.KnuckleAxis, tuckSign * _tuckedFingersDegrees);
            ProxyGripSolver.Curl(_curlChainsL[3], frame.KnuckleAxis, tuckSign * _tuckedFingersDegrees);
        }

        private void RestoreLeftArm()
        {
            _upperArmL.localRotation = _upperLRest;
            _lowerArmL.localRotation = _lowerLRest;
            _handL.localRotation = _handLRest;
        }

        /// <summary>
        /// Pone la izquierda en un candidato: la palma mira al pomo desde <paramref name="clock"/> grados alrededor de
        /// su eje, los nudillos van por el eje en el sentido <paramref name="sign"/>, y el brazo llega por IK con el
        /// codo hacia <paramref name="pole"/>. Coste = naturalidad del brazo + no llegar + nudillos dentro de la
        /// linterna + manos pisándose.
        /// </summary>
        private float PoseLeftArm(float clock, float sign, float tilt, int pole, Vector3 knob, Vector3 axis,
            Vector3 bodyAxisPoint, Vector3 bodyAxisDir, Vector3 rightKnuckles)
        {
            Transform root = transform;
            Vector3 reference = Vector3.ProjectOnPlane(-root.forward, axis);
            if (reference.sqrMagnitude < 1e-6f)
                reference = Vector3.ProjectOnPlane(root.up, axis);
            reference.Normalize();

            Vector3 palm = Quaternion.AngleAxis(clock, axis) * reference; // de la mano hacia el pomo
            Vector3 knuckleAxis = Quaternion.AngleAxis(tilt, palm) * (axis * sign);
            Vector3 knuckleTarget = knob - palm * (_knobRadius + ProxyGripSolver.SkinMetres);

            var frame = FrameL();
            Quaternion wanted = Quaternion.LookRotation(knuckleAxis, palm);
            Quaternion delta = wanted * Quaternion.Inverse(Quaternion.LookRotation(frame.KnuckleAxis, frame.PalmNormal));
            Vector3 wristTarget = knuckleTarget - delta * (frame.KnuckleCentre - _handL.position);

            float armLength = Vector3.Distance(_upperArmL.position, _lowerArmL.position)
                + Vector3.Distance(_lowerArmL.position, _handL.position);
            Vector3 shoulder = _upperArmL.position;
            ProxyGripSolver.TwoBoneIk(_upperArmL, _lowerArmL, _handL, wristTarget, shoulder + PoleDirectionL(pole) * armLength);

            frame = FrameL();
            Vector3 untwisted = frame.PalmNormal;
            _handL.rotation = wanted * Quaternion.Inverse(Quaternion.LookRotation(frame.KnuckleAxis, frame.PalmNormal)) * _handL.rotation;
            frame = FrameL();
            ProxyArmNaturalness.ShareForearmTwist(_lowerArmL, _handL, frame.PalmNormal, untwisted);

            var measure = ProxyArmNaturalness.Take(shoulder, _lowerArmL.position, _handL.position, _middleKnuckleL.position,
                frame.PalmNormal, frame.KnuckleAxis, root.right, root.up, false);
            float cost = ProxyArmNaturalness.Cost(measure);
            LastLeftKnobMiss = Vector3.Distance(frame.KnuckleCentre, knuckleTarget);
            cost += 4f * Sq(LastLeftKnobMiss / 0.02f);
            if (ProxyGripSolver.DistanceToCylinder(frame.KnuckleCentre, bodyAxisPoint, bodyAxisDir, _radius) < ProxyGripSolver.SkinMetres)
                cost += 20f;
            float apart = Vector3.Distance(frame.KnuckleCentre, rightKnuckles);
            if (apart < _handsApart)
                cost += 10f * Sq((_handsApart - apart) / _handsApart);
            return cost;
        }

        private Vector3 PoleDirectionL(int pole)
        {
            Transform root = transform;
            switch (pole)
            {
                case 0: return -root.up * 0.4f - root.forward * 0.5f - root.right * _elbowOutward;
                case 1: return -root.up - root.forward * 0.15f - root.right * 0.05f;
                default: return -root.up * 0.5f - root.forward * 0.2f - root.right * 0.45f;
            }
        }

        // ─── Libro abierto ────────────────────────────────────────────────────────────────────────────────

        private void SetBookOpen(bool open)
        {
            if (open && _book == null)
                _book = BuildOpenBook();
            open &= _book != null;
            if (open != _bookOpen)
            {
                _hasBookChoice = false;
                ClearBookReadouts();
            }
            _bookOpen = open;
            if (_book != null && _book.activeSelf != open)
                _book.SetActive(open);
            if (_instance != null && _instance.activeSelf == open)
                _instance.SetActive(!open);
        }

        private GameObject BuildOpenBook()
        {
            if (!HasHandRig || !HasLeftRig)
                return null;
            if (!_openBookLoaded)
            {
                _openBookLoaded = true;
                _openBookPrefab = Resources.Load<GameObject>(OpenBookResource);
                MeasureOpenBook(_openBookPrefab);
            }
            if (_openBookPrefab == null)
                return null; // sin el prefab horneado el vecino sigue con lo de la funda, que es lo que hacía

            // Colgado del pecho, el padre común de los dos brazos: si el cabeceo lo dobla después de este hook, libro y
            // brazos se mueven juntos y las manos no se despegan de las tapas.
            Transform parent = CommonAncestor(_upperArm, _upperArmL);
            var go = Instantiate(_openBookPrefab, parent != null ? parent : transform, false);
            ProxyRigUtil.SetLayerRecursive(go, _hand.gameObject.layer);
            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// La tapa de atrás del libro horneado, donde van las manos. Abierto en V, la caja entera no la dice: se mide
        /// sobre los vértices (una vez por proceso). Sin malla legible, tapa plana en el fondo de la caja.
        /// </summary>
        private static void MeasureOpenBook(GameObject prefab)
        {
            var filter = prefab != null ? prefab.GetComponent<MeshFilter>() : null;
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
                return;
            _bookCover = mesh.isReadable
                ? ProxyBookHold.MeasureCover(mesh.vertices)
                : new ProxyBookHold.Cover
                {
                    HalfWidth = mesh.bounds.extents.x, HalfHeight = mesh.bounds.extents.y, BackZAtSpine = mesh.bounds.min.z,
                };
        }

        private static Transform CommonAncestor(Transform a, Transform b)
        {
            if (a == null || b == null)
                return null;
            for (var p = a.parent; p != null; p = p.parent)
                if (b.IsChildOf(p))
                    return p;
            return null;
        }

        /// <summary>
        /// Libro delante del pecho y cada mano detrás de su tapa. Las manos se eligen por naturalidad UNA vez al abrir —
        /// el libro va fijo al cuerpo, no sigue al cabeceo— y cada fotograma se reaplica la elección sobre la pose que
        /// deja el Animator, así que el libro acompaña al andar.
        /// </summary>
        private void ApplyBookHold()
        {
            Transform root = transform;
            ProxyBookHold.BookPose(_upperArmL.position, _upperArm.position, root.forward, root.up,
                out Vector3 centre, out Quaternion rotation);
            _book.transform.SetPositionAndRotation(centre, rotation);

            float shoulderHeight = Vector3.Dot((_upperArmL.position + _upperArm.position) * 0.5f - root.position, root.up);
            if (!_hasBookChoice || Mathf.Abs(shoulderHeight - _bookChoiceShoulderHeight) > BookRechooseMetres)
            {
                ChooseBookHand(_bookRight, true, centre, rotation);
                ChooseBookHand(_bookLeft, false, centre, rotation);
                _bookChoiceShoulderHeight = shoulderHeight;
                _hasBookChoice = true;
            }

            LastBookRightCost = PoseBookArm(true, _bookRight, centre, rotation, out float missRight, out float pierceRight);
            LastBookLeftCost = PoseBookArm(false, _bookLeft, centre, rotation, out float missLeft, out float pierceLeft);
            LastBookMissRight = missRight;
            LastBookMissLeft = missLeft;
            LastBookPierceRight = pierceRight;
            LastBookPierceLeft = pierceLeft;
            LastBookFrontRight = CurlBookFingers(true, _bookRight, centre, rotation);
            LastBookFrontLeft = CurlBookFingers(false, _bookLeft, centre, rotation);
        }

        private void ChooseBookHand(BookHand hand, bool right, Vector3 centre, Quaternion rotation)
        {
            Transform upper = right ? _upperArm : _upperArmL;
            Transform lower = right ? _lowerArm : _lowerArmL;
            Transform end = right ? _hand : _handL;
            Quaternion upperRest = upper.localRotation, lowerRest = lower.localRotation, endRest = end.localRotation;

            var trial = new BookHand();
            float best = float.MaxValue;
            foreach (float spread in BookSpreadCandidates)
            foreach (float along in BookAlongCandidates)
            foreach (float inset in BookInsetCandidates)
            for (int pole = 0; pole < PoleCount; pole++)
            {
                upper.localRotation = upperRest;
                lower.localRotation = lowerRest;
                end.localRotation = endRest;
                trial.Spread = spread;
                trial.Along = along;
                trial.Inset = inset;
                trial.Pole = pole;
                float cost = PoseBookArm(right, trial, centre, rotation, out _, out _) + 0.1f * Sq(spread / 45f);
                if (cost >= best)
                    continue;
                best = cost;
                hand.Spread = spread;
                hand.Along = along;
                hand.Inset = inset;
                hand.Pole = pole;
            }
            upper.localRotation = upperRest;
            lower.localRotation = lowerRest;
            end.localRotation = endRest;
            hand.ThumbSolved = false;
        }

        /// <summary>
        /// Una mano en su canto: nudillos detrás de la tapa, palma contra ella y dedos hacia el lomo. La línea de nudillos
        /// sale de la lateralidad MEDIDA de esa mano. Muñeca por IK, mano girada a su sitio y media torsión al antebrazo,
        /// igual que la linterna. Coste = naturalidad + no llegar.
        /// </summary>
        private float PoseBookArm(bool right, BookHand hand, Vector3 centre, Quaternion rotation, out float miss, out float pierce)
        {
            Transform root = transform;
            Transform upper = right ? _upperArm : _upperArmL;
            Transform lower = right ? _lowerArm : _lowerArmL;
            Transform end = right ? _hand : _handL;
            Transform middle = right ? _middleKnuckle : _middleKnuckleL;

            Vector3 side = ProxyBookHold.SideAxis(rotation, root.right, right);
            var grip = ProxyBookHold.Grip(centre, rotation, side, _bookCover, hand.Inset, hand.Along, hand.Spread);

            var frame = right ? Frame() : FrameL();
            Vector3 knuckleAxis = ProxyBookHold.KnuckleAxis(grip.Fingers, grip.Palm, ProxyBookHold.Chirality(frame));
            Quaternion wanted = Quaternion.LookRotation(knuckleAxis, grip.Palm);
            Quaternion delta = wanted * Quaternion.Inverse(Quaternion.LookRotation(frame.KnuckleAxis, frame.PalmNormal));
            Vector3 wristTarget = grip.Knuckles - delta * (frame.KnuckleCentre - end.position);

            float armLength = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, end.position);
            Vector3 shoulder = upper.position;
            Vector3 pole = right ? PoleDirection(hand.Pole) : PoleDirectionL(hand.Pole);
            ProxyGripSolver.TwoBoneIk(upper, lower, end, wristTarget, shoulder + pole * armLength);

            frame = right ? Frame() : FrameL();
            Vector3 untwisted = frame.PalmNormal;
            end.rotation = wanted * Quaternion.Inverse(Quaternion.LookRotation(frame.KnuckleAxis, frame.PalmNormal)) * end.rotation;
            frame = right ? Frame() : FrameL();
            ProxyArmNaturalness.ShareForearmTwist(lower, end, frame.PalmNormal, untwisted);

            var measure = ProxyArmNaturalness.Take(shoulder, lower.position, end.position, middle.position,
                frame.PalmNormal, frame.KnuckleAxis, root.right, root.up, right);
            miss = Vector3.Distance(frame.KnuckleCentre, grip.Knuckles);
            // Nada de la mano dentro del libro: ni la muñeca, ni la base del pulgar, ni los nudillos, ni la CARNE de la palma
            // (agachado asomaba por la página, 14-09). Los dedos los abre CurlBookFingers y el pulgar rodea el canto por fuera.
            Vector3 palmFlesh = (end.position + frame.KnuckleCentre) * 0.5f + frame.PalmNormal * ProxyBookHold.PalmFleshMetres;
            pierce = Mathf.Max(
                Mathf.Max(ProxyBookHold.PierceDepth(end.position, centre, rotation, _bookCover),
                    ProxyBookHold.PierceDepth((right ? _thumbBase : _thumbBaseL).position, centre, rotation, _bookCover)),
                Mathf.Max(ProxyBookHold.PierceDepth((right ? _indexKnuckle : _indexKnuckleL).position, centre, rotation, _bookCover),
                    ProxyBookHold.PierceDepth((right ? _pinkyKnuckle : _pinkyKnuckleL).position, centre, rotation, _bookCover)));
            pierce = Mathf.Max(pierce, ProxyBookHold.PierceDepth(palmFlesh, centre, rotation, _bookCover));
            // La eminencia del pulgar, el bulto más grueso de la palma: asomaba por el canto de la página (vista de frente
            // a las páginas, 14-09). El pulgar tiene que rodear el canto desde FUERA del libro.
            Vector3 thenar = (right ? _thumbBase : _thumbBaseL).position + frame.PalmNormal * ProxyBookHold.PalmFleshMetres;
            pierce = Mathf.Max(pierce, ProxyBookHold.PierceDepth(thenar, centre, rotation, _bookCover));

            // Yema estimada: los dedos, ya abiertos, van por la tapa en la dirección del agarre.
            var middle3 = (right ? _curlChains : _curlChainsL)[1];
            float fingerLength = FingerLength(middle3);
            float spine = ProxyBookHold.SpineCost(grip.Knuckles + grip.Fingers * fingerLength, centre, side);

            return ProxyArmNaturalness.Cost(measure) + 4f * Sq(miss / 0.02f)
                + ProxyBookHold.ElbowCost(shoulder, lower.position, end.position, root.right, root.up, right)
                + Sq(Mathf.Max(0f, pierce + ProxyGripSolver.SkinMetres) / 0.01f)
                + spine;
        }

        // Largo del dedo estirado: suma de falanges y la yema estimada. El clip lo trae doblado, así que la recta nudillo-yema
        // se quedaría corta.
        private static float FingerLength(Transform[] chain)
        {
            if (chain == null || chain.Length < 2 || chain[0] == null || chain[chain.Length - 1] == null || chain[chain.Length - 2] == null)
                return 0.09f;
            float length = 0f;
            for (int i = 1; i < chain.Length; i++)
                if (chain[i] != null && chain[i - 1] != null)
                    length += Vector3.Distance(chain[i - 1].position, chain[i].position);
            return length + Vector3.Distance(chain[chain.Length - 1].position, ProxyGripSolver.Tip(chain));
        }

        /// <summary>
        /// Dedos detrás de la tapa y pulgar hacia la página. La apertura se resuelve CADA fotograma sobre la pose del
        /// Animator: resuelta una vez de pie y reaplicada agachado, el clip de agachado cierra más los dedos y volvían a
        /// atravesar la tapa. Devuelve lo más adelantado de los dedos respecto de la tapa (m).
        /// </summary>
        private float CurlBookFingers(bool right, BookHand hand, Vector3 centre, Quaternion rotation)
        {
            var chains = right ? _curlChains : _curlChainsL;
            var thumb = right ? _thumbChain : _thumbChainL;
            var frame = right ? Frame() : FrameL();
            Vector3 side = ProxyBookHold.SideAxis(rotation, transform.right, right);
            var grip = ProxyBookHold.Grip(centre, rotation, side, _bookCover, hand.Inset, hand.Along, hand.Spread);
            Vector3 cover = grip.Knuckles + grip.Palm * ProxyBookHold.KnuckleBehindMetres;

            float front = float.MinValue;
            for (int i = 0; i < chains.Length; i++)
            {
                ProxyBookHold.OpenBehindPlane(chains[i], frame.KnuckleAxis, cover, grip.Palm, ProxyBookHold.FingerClearance);
                front = Mathf.Max(front, ProxyBookHold.MaxInFront(chains[i], cover, grip.Palm));
            }
            // El pulgar rodea el canto y se apoya EN la página: su yema, a ThumbInset del canto de fuera y a la altura de la
            // base del pulgar, una piel por delante de las páginas.
            Vector3 toReader = rotation * Vector3.forward;
            Vector3 pageUp = rotation * Vector3.up;
            float thumbX = _bookCover.HalfWidth - ProxyBookHold.ThumbInsetMetres;
            float thumbAlong = Mathf.Clamp(Vector3.Dot((right ? _thumbBase : _thumbBaseL).position - centre, pageUp),
                0.02f - _bookCover.HalfHeight, _bookCover.HalfHeight - 0.02f);
            Vector3 thumbTarget = centre + side * thumbX + pageUp * thumbAlong
                + toReader * (_bookCover.FrontZAtSpine + _bookCover.FrontSlope * thumbX + ProxyBookHold.ThumbAbovePageMetres);
            if (!hand.ThumbSolved)
            {
                var bookCover = _bookCover;
                Vector3 pivot = thumb[0].position;
                ProxyBookHold.ReachWithTip(thumb, frame.FingerAxis, frame.KnuckleAxis, thumbTarget, ProxyBookHold.MaxThumbDegrees,
                    ProxyBookHold.ThumbStepDegrees, out hand.ThumbA, out hand.ThumbB,
                    () => ProxyBookHold.ThumbInsideCost(thumb, centre, rotation, bookCover)
                        + ProxyBookHold.PageUpCost(ProxyGripSolver.Tip(thumb) - pivot, rotation));
                hand.ThumbSolved = true;
            }
            else
            {
                // MISMA reaplicación que ReachWithTip usa al resolver (los dos ejes SOLO en la base): con el Curl
                // progresivo de antes, el pulgar se retorcía en tornillo en cuanto pasaba de este fotograma al
                // siguiente, aunque la búsqueda inicial ya hubiera encontrado el ángulo bueno (Joel, 14-09).
                ProxyBookHold.ApplyRigidReach(thumb, frame.FingerAxis, frame.KnuckleAxis, hand.ThumbA, hand.ThumbB);
            }
            if (right)
                LastBookThumbMissRight = Vector3.Distance(ProxyGripSolver.Tip(thumb), thumbTarget);
            else
                LastBookThumbMissLeft = Vector3.Distance(ProxyGripSolver.Tip(thumb), thumbTarget);
            return front;
        }

        private ProxyGripSolver.HandFrame FrameL() => ProxyGripSolver.Frame(
            _handL.position, _indexKnuckleL.position, _middleKnuckleL.position, _pinkyKnuckleL.position, _thumbBaseL.position);

        private ProxyGripSolver.HandFrame Frame() => ProxyGripSolver.Frame(
            _hand.position, _indexKnuckle.position, _middleKnuckle.position, _pinkyKnuckle.position, _thumbBase.position);

        private static void ApplyCurl(Transform[] bones, Quaternion[] bind, float curlDeg, Vector3 axis)
        {
            if (bones == null)
                return;
            var q = Quaternion.AngleAxis(curlDeg, axis);
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null)
                    bones[i].localRotation = bind[i] * q;
            }
        }

        private static Vector3 SafeScale(Vector3 s)
        {
            // A zero/uninitialized scale would hide the model; fall back to unit scale.
            return (s.x == 0f && s.y == 0f && s.z == 0f) ? Vector3.one : s;
        }

        private void ClearInstance()
        {
            if (_instance != null)
                Destroy(_instance);
            _instance = null;
        }

        // Single traversal → name→Transform map (the finger/hand bones are resolved off this).
        private Dictionary<string, Transform> BuildBoneMap()
        {
            var map = new Dictionary<string, Transform>(64);
            foreach (var tr in GetComponentsInChildren<Transform>(true))
                map[tr.name] = tr; // last-wins is fine; bone names are unique on this rig
            return map;
        }

        private static void CacheFingerChain(Dictionary<string, Transform> bones, string[] names,
            out Transform[] resolved, out Quaternion[] bind)
        {
            resolved = new Transform[names.Length];
            bind = new Quaternion[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                bones.TryGetValue(names[i], out var b);
                resolved[i] = b;
                bind[i] = b != null ? b.localRotation : Quaternion.identity; // bind pose at Awake
            }
        }

        /// <summary>This proxy's networked held item ID, via the RemotePlayerManager view whose root is us.</summary>
        private bool TryResolveHeld(out int heldItem)
        {
            heldItem = 0;
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return false;

            heldItem = view.heldItem;
            _pitch = view.pitch;
            _buttons = view.buttons;
            _dead = view.dead;
            return true;
        }
    }
}
