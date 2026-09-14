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

        [Header("Agarre medido (objetos con manivela: la linterna)")]
        [Tooltip("Aire entre el borde del meñique y la manivela, a lo largo del eje del objeto (m).")]
        [SerializeField, Min(0f)] private float _crankClearance = 0.01f;

        [Tooltip("Muñeca por debajo del hombro, en fracción del largo del brazo. Era 0,72; Joel pidió subirla (14-09).")]
        [SerializeField] private float _holdDown = 0.60f;

        [Tooltip("Muñeca por delante del hombro, en fracción del largo del brazo: el brazo algo levantado.")]
        [SerializeField] private float _holdForward = 0.56f;

        [Tooltip("Muñeca hacia fuera del hombro (m). Con 0 la mano quedaba delante de la entrepierna (captura 14-09).")]
        [SerializeField] private float _holdOutward = 0.06f;

        [Tooltip("Codo hacia fuera, en fracción del largo del brazo: cuanto menos, más pegado al costado.")]
        [SerializeField] private float _elbowOutward = 0.15f;

        [Tooltip("Giro PREFERIDO de la palma hacia abajo, en grados. Sólo desempata: el giro real lo elige la " +
                 "búsqueda de naturalidad del brazo.")]
        [SerializeField, Range(0f, 60f)] private float _palmDownDegrees = 20f;

        [Tooltip("La linterna se lleva algo inclinada hacia el suelo de delante, sumada al cabeceo del vecino. " +
                 "Con 0 y cabeceo 0 el haz salía horizontal a la altura de la cadera y no alumbraba nada cercano.")]
        [SerializeField, Range(0f, 45f)] private float _beamDownBias = 12f;

        [Tooltip("Tope del cabeceo que sigue la linterna, en grados.")]
        [SerializeField, Range(0f, 90f)] private float _beamPitchClamp = 60f;

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
        private static readonly float[] RollCandidates = { -20f, 10f, 40f, 70f };
        private static readonly float[] TiltCandidates = { -40f, -20f, 0f, 20f, 40f };
        private static readonly float[] HeightCandidates = { -0.08f, 0f, 0.08f };
        private const int PoleCount = 3;
        private const float RechooseDegrees = 8f;
        private bool _hasChoice;
        private float _choiceRoll, _choiceTilt, _choiceHeight, _choicePitch;
        private int _choicePole;
        private Quaternion _upperRest, _lowerRest, _handRest;

        /// <summary>Coste de naturalidad del brazo derecho en el último fotograma (0 = cómodo). Lo lee el arnés.</summary>
        public float LastArmCost { get; private set; }

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
        }

        private static Transform[] Chain(Dictionary<string, Transform> bones, string finger)
        {
            var chain = new Transform[3];
            for (int i = 0; i < 3; i++)
                bones.TryGetValue($"{finger}.{i + 1}.R", out chain[i]);
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
                _category = "";
                _applied = Unset;
                return;
            }

            if (!TryResolveHeld(out int id) || id == _applied)
                return;

            ClearInstance();
            _category = "";
            if (id != 0)
            {
                var def = DataDefinition<ItemDefinition>.GetWithId(id);
                if (def != null && def.ParentGroup != null)
                    _category = def.ParentGroup.Name; // de-prefixed: "Melee"/"Firearms"/"Tools"
                _instance = BuildHeldModel(def);
            }
            _applied = id;
        }

        // Apply per-category placement + finger grip AFTER the Animator. Re-applied every frame so
        // GripPoseSet edits calibrate live; only runs while an item is held (empty hands → the
        // locomotion clip drives the hand naturally).
        private void LateUpdate()
        {
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
            if (!_hasChoice || Mathf.Abs(pitch - _choicePitch) > RechooseDegrees)
                ChooseRightArm(beam, pitch);
            LastArmCost = PoseRightArm(_choiceRoll, _choiceTilt, _choicePole, _choiceHeight, beam);

            var frame = Frame();
            float grip = ProxyGripSolver.GripAlongAxis(_bodyCentreY, _halfLength, frame.Width, true, _crankY, _crankClearance);
            ProxyGripSolver.PlaceCylinder(frame, _radius, grip, out Vector3 position, out Quaternion rotation, _choiceTilt);
            position -= rotation * new Vector3(_axisOffsetXZ.x, 0f, _axisOffsetXZ.y);
            var t = _instance.transform;
            t.localScale = Vector3.one;
            t.SetPositionAndRotation(position, rotation);

            Vector3 axisPoint = position + rotation * new Vector3(_axisOffsetXZ.x, 0f, _axisOffsetXZ.y);
            Vector3 axisDir = rotation * Vector3.up;

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
        /// queda con su luz de antorcha. Hay que leerlo después del LateUpdate de este hook.
        /// </summary>
        public bool TryGetBeam(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (!_measured || _instance == null || !HasHandRig)
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
            float bestRoll = _choiceRoll, bestTilt = _choiceTilt, bestHeight = _choiceHeight;
            int bestPole = _choicePole;
            foreach (float roll in RollCandidates)
            foreach (float tilt in TiltCandidates)
            foreach (float height in HeightCandidates)
            for (int pole = 0; pole < PoleCount; pole++)
            {
                RestoreArm();
                float cost = PoseRightArm(roll, tilt, pole, height, beam)
                    + 0.2f * Sq((roll - _palmDownDegrees) / 60f)
                    + 0.1f * Sq(tilt / 40f); // a igualdad, el objeto más alineado con los nudillos
                // A igualdad se queda la que había: dos opciones de coste parecido no alternan.
                if (_hasChoice && Mathf.Approximately(roll, _choiceRoll) && Mathf.Approximately(tilt, _choiceTilt)
                    && Mathf.Approximately(height, _choiceHeight) && pole == _choicePole)
                    cost -= 0.05f;
                if (cost >= best)
                    continue;
                best = cost;
                bestRoll = roll;
                bestTilt = tilt;
                bestHeight = height;
                bestPole = pole;
            }
            RestoreArm();

            // Cambiar de inclinación cambia dónde tocan los dedos: se vuelven a cerrar.
            if (!_hasChoice || !Mathf.Approximately(bestTilt, _choiceTilt) || !Mathf.Approximately(bestRoll, _choiceRoll))
                _curlSolved = false;
            _choiceRoll = bestRoll;
            _choiceTilt = bestTilt;
            _choiceHeight = bestHeight;
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
        private float PoseRightArm(float roll, float tilt, int pole, float height, Vector3 beam)
        {
            Transform root = transform;
            float armLength = Vector3.Distance(_upperArm.position, _lowerArm.position)
                + Vector3.Distance(_lowerArm.position, _hand.position);
            Vector3 shoulder = _upperArm.position;
            Vector3 target = shoulder - root.up * ((_holdDown + height) * armLength)
                + root.forward * (_holdForward * armLength) + root.right * _holdOutward;
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
            return true;
        }
    }
}
