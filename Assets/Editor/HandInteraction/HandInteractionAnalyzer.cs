#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackroomsSurvival.Gameplay.HandInteraction;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    [Serializable]
    public sealed class HandWieldableCandidate
    {
        public string prefabPath;
        public int score;
        public string profilePath;
    }

    [Serializable]
    public sealed class HandMeshPart
    {
        public string name;
        public string path;
        public float lengthMm;
        public float meanRadiusMm;
        public float tailRadiusMm, tipRadiusMm;
        /// <summary>|cos| entre el eje principal de los vértices y el +Y local: el solver asume el eje en +Y.</summary>
        public float axisAlignment;
        public float slenderness;
        public int vertices;
    }

    [Serializable]
    public sealed class HandInspection
    {
        public string prefabPath;
        public bool hasFirstPersonRig;
        public string modelNode;
        public List<string> modelNodeCandidates = new();
        public string gripMesh;
        public List<HandMeshPart> parts = new();
        public string carrierHand;
        public float carrierAlong;
        public float carrierClockDeg;
        public List<string> baseLayerClips = new();
        public List<string> existingProfiles = new();
        public string category;
        public string suggestedKind;
        public string suggestedOutputFolder;
        public string suggestedProfilePath;
        public List<string> notes = new();
        public List<string> manualSteps = new();
    }

    /// <summary>
    /// Mira un wieldable y decide lo que se pueda decidir sin nadie: qué nodo es el objeto, qué malla se
    /// agarra, de qué mano cuelga, dónde está esa mano sobre el eje, qué clips hay que tocar y un objetivo
    /// de partida por categoría. Lo que NO se puede decidir fiable (una caja a dos manos, un eje que no es
    /// +Y) sale como paso manual, no como suposición.
    /// </summary>
    internal static class HandInteractionAnalyzer
    {
        public const string ProfileFolder = "Assets/Data/HandInteraction";

        private static readonly string[] SearchRoots =
        {
            "Assets/Prefabs/Wieldables", "Assets/Resources/Wieldables", "Assets/PolymindGames/STP/Prefabs/Wieldables",
        };

        /// <summary>Sinónimos en español de lo que ya existe: «prepara la linterna» tiene que encontrar CrankFlashlight.</summary>
        private static readonly Dictionary<string, string[]> Synonyms = new()
        {
            ["linterna"] = new[] { "flashlight", "torch" },
            ["antorcha"] = new[] { "torch" },
            ["destornillador"] = new[] { "screwdriver" },
            ["spray"] = new[] { "spray" },
            ["bote"] = new[] { "spray", "can" },
            ["venda"] = new[] { "bandage" },
            ["reloj"] = new[] { "watch" },
            ["hacha"] = new[] { "axe" },
            ["cuchillo"] = new[] { "knife" },
            ["lanza"] = new[] { "spear" },
            ["pico"] = new[] { "pickaxe" },
            ["arco"] = new[] { "bow" },
            ["rifle"] = new[] { "marlin" },
            ["libro"] = new[] { "book" },
            ["brujula"] = new[] { "compass" },
            ["brújula"] = new[] { "compass" },
            ["maza"] = new[] { "club" },
            ["manivela"] = new[] { "crank" },
        };

        public static List<HandWieldableCandidate> Find(string query)
        {
            var tokens = (query ?? "").ToLowerInvariant()
                .Split(new[] { ' ', '_', '-', '/', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(t => Synonyms.TryGetValue(t, out var s) ? s.Append(t) : new[] { t })
                .Where(t => t.Length > 2)
                .Distinct()
                .ToList();
            var profiles = AllProfiles();
            var list = new List<HandWieldableCandidate>();
            var roots = SearchRoots.Where(AssetDatabase.IsValidFolder).ToArray();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", roots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                int score = tokens.Count(t => name.Contains(t)) * 10;
                if (tokens.Count > 0 && score == 0) continue;
                if (path.StartsWith("Assets/Prefabs/", StringComparison.Ordinal)) score += 2; // lo del proyecto antes que el vendor
                var prof = profiles.FirstOrDefault(p => p.wieldablePrefab != null && AssetDatabase.GetAssetPath(p.wieldablePrefab) == path);
                list.Add(new HandWieldableCandidate { prefabPath = path, score = score, profilePath = prof != null ? AssetDatabase.GetAssetPath(prof) : "" });
            }
            return list.OrderByDescending(c => c.score).ThenBy(c => c.prefabPath, StringComparer.Ordinal).ToList();
        }

        public static List<HandInteractionProfile> AllProfiles()
            => AssetDatabase.FindAssets("t:HandInteractionProfile")
                .Select(g => AssetDatabase.LoadAssetAtPath<HandInteractionProfile>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(p => p != null)
                .OrderBy(p => AssetDatabase.GetAssetPath(p), StringComparer.Ordinal)
                .ToList();

        public static HandInspection Inspect(GameObject prefab)
        {
            var ins = new HandInspection { prefabPath = AssetDatabase.GetAssetPath(prefab) };
            var all = prefab.GetComponentsInChildren<Transform>(true);
            ins.hasFirstPersonRig = all.Any(t => t.name == HandInteractionRig.AnimatorNodeName) &&
                                    all.Any(t => t.name == "Hand.R") && all.Any(t => t.name == "Hand.L");
            if (!ins.hasFirstPersonRig)
            {
                ins.notes.Add("RIG_NOT_FOUND: el prefab no trae los brazos de primera persona (ViewModel, Hand.R, Hand.L).");
                return ins;
            }

            // El nodo del modelo: sube desde cada malla hasta el hijo directo de un HUESO. Los huesos son los
            // de los SkinnedMeshRenderer del prefab (brazos y restos del donante).
            var bones = new HashSet<Transform>(prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(s => s.bones).Where(b => b != null));
            var nodes = new Dictionary<Transform, int>();
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                var t = mf.transform;
                while (t.parent != null && !bones.Contains(t.parent)) t = t.parent;
                if (t.parent == null) continue;
                int score = mf.sharedMesh.vertexCount + (ActiveInPrefab(mf.transform) && mr.enabled ? 100000 : 0) +
                            (t.name.EndsWith("Model", StringComparison.Ordinal) ? 50000 : 0);
                nodes[t] = nodes.TryGetValue(t, out int s) ? Math.Max(s, score) : score;
            }
            var ordered = nodes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.name, StringComparer.Ordinal).ToList();
            ins.modelNodeCandidates = ordered.Select(kv => kv.Key.name).ToList();
            if (ordered.Count == 0)
            {
                ins.notes.Add("MODEL_NODE_NOT_FOUND: no hay ninguna malla colgando de un hueso del rig.");
                return ins;
            }
            var node = ordered[0].Key;
            ins.modelNode = node.name;

            foreach (var mf in node.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh != null))
                ins.parts.Add(DescribePart(mf, node));
            var grip = ins.parts.OrderByDescending(p => p.lengthMm * p.meanRadiusMm * p.meanRadiusMm).First();
            ins.gripMesh = grip.name;
            if (grip.axisAlignment < 0.9f)
                ins.manualSteps.Add($"GRIP_AXIS_NOT_Y: el eje principal de '{grip.name}' no es su +Y (|cos| {grip.axisAlignment:0.00}). " +
                                    "El solver mide el radio sobre +Y: rehornear la malla con el eje en +Y antes de preparar la interacción.");

            ins.carrierHand = IsUnder(node, "Hand.R") ? "R" : IsUnder(node, "Hand.L") ? "L" : "";
            if (ins.carrierHand == "")
                ins.notes.Add("El modelo cuelga de un hueso del donante, no de una mano: las dos manos pueden ser secundarias.");

            string lower = ins.prefabPath.ToLowerInvariant();
            ins.category = lower.Contains("flashlight") || lower.Contains("torch") ? "flashlight"
                : lower.Contains("screwdriver") || lower.Contains("axe") || lower.Contains("knife") || lower.Contains("pickaxe") || lower.Contains("club") ? "tool"
                : lower.Contains("spray") || lower.Contains("can") || lower.Contains("bottle") ? "container"
                : lower.Contains("spear") || lower.Contains("marlin") || lower.Contains("bow") ? "long"
                : grip.slenderness < 2f ? "box" : "generic";
            bool slender = grip.slenderness >= 2.2f;
            bool roomForTwo = grip.lengthMm >= 170f;
            ins.suggestedKind = (ins.category == "long" || (ins.category == "flashlight" && roomForTwo)) && slender ? "TwoHand" : "OneHand";
            if (!slender)
                ins.manualSteps.Add("BOX_LIKE: objeto poco esbelto. El agarre a dos manos por las caras (cajas, documentos) no está implementado en v1: " +
                                    "sólo se puede preparar a una mano, rodeando.");
            if (ins.category == "flashlight" && !roomForTwo)
                ins.notes.Add($"El cuerpo mide {grip.lengthMm:0} mm: no caben dos puños (~170 mm). Se sugiere una mano.");

            // Dónde está la portadora sobre el eje, en el idle efectivo.
            try
            {
                var tmp = ScriptableObject.CreateInstance<HandInteractionProfile>();
                tmp.wieldablePrefab = prefab;
                tmp.modelNodeName = node.name;
                tmp.gripMeshNodeName = grip.name;
                using var ctx = HandInteractionBaker.Open(tmp);
                ins.baseLayerClips = ctx.Pairs.Select(p => $"{p.original.name} → {AssetDatabase.GetAssetPath(p.effective)}").ToList();
                if (ctx.Carrier != null)
                {
                    ctx.IdleEffective.SampleAnimation(ctx.Rig.Animator.gameObject, 0f);
                    ctx.Rig.Animator.localPosition = Vector3.zero;
                    ctx.Rig.Animator.localRotation = Quaternion.identity;
                    Vector3 knuckles = Vector3.zero;
                    for (int f = 1; f < 5; f++) knuckles += ctx.Carrier.Fingers[f][0].position;
                    knuckles /= 4f;
                    Vector3 local = ctx.Rig.GripMesh.InverseTransformPoint(knuckles);
                    ins.carrierAlong = Mathf.InverseLerp(ctx.Rig.ProfileMinY, ctx.Rig.ProfileMaxY, local.y);
                    ins.carrierClockDeg = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                }
                string idlePath = AssetDatabase.GetAssetPath(ctx.IdleEffective);
                ins.suggestedOutputFolder = idlePath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) && !idlePath.StartsWith("Assets/PolymindGames/")
                    ? Path.GetDirectoryName(idlePath)?.Replace('\\', '/')
                    : $"Assets/Art/Items/{Path.GetFileNameWithoutExtension(ins.prefabPath).Replace("BR_Wieldable_", "")}/Anim";
                UnityEngine.Object.DestroyImmediate(tmp);
            }
            catch (HandInteractionException e)
            {
                ins.notes.Add($"{e.Code}: {e.Message}");
            }

            ins.existingProfiles = AllProfiles().Where(p => p.wieldablePrefab == prefab).Select(AssetDatabase.GetAssetPath).ToList();
            ins.suggestedProfilePath = ins.existingProfiles.FirstOrDefault() ?? $"{ProfileFolder}/{prefab.name}_Hands.asset";
            ins.manualSteps.Add("Revisar las capturas tras hornear: la naturalidad se mide, pero el encuadre final se juzga mirando.");
            return ins;
        }

        private static bool ActiveInPrefab(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (!p.gameObject.activeSelf) return false;
            return true;
        }

        private static bool IsUnder(Transform t, string name)
        {
            for (var p = t.parent; p != null; p = p.parent)
                if (p.name == name) return true;
            return false;
        }

        private static HandMeshPart DescribePart(MeshFilter mf, Transform node)
        {
            var mesh = mf.sharedMesh;
            var v = mesh.vertices;
            var b = mesh.bounds;
            float scale = mf.transform.lossyScale.y / Mathf.Max(1e-5f, node.lossyScale.y);
            Vector3 c = Vector3.zero;
            foreach (var p in v) c += p;
            c /= Mathf.Max(1, v.Length);
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in v)
            {
                var d = p - c;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            var axis = new Vector3(0.3f, 1f, 0.2f).normalized;
            for (int i = 0; i < 32; i++)
            {
                var next = new Vector3(xx * axis.x + xy * axis.y + xz * axis.z, xy * axis.x + yy * axis.y + yz * axis.z,
                    xz * axis.x + yz * axis.y + zz * axis.z);
                if (next.sqrMagnitude < 1e-12f) break;
                axis = next.normalized;
            }
            float span = Mathf.Max(1e-5f, b.size.y);
            float sum = 0f, tail = 0f, tip = 0f; int nTail = 0, nTip = 0;
            foreach (var p in v)
            {
                float r = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                sum += r;
                float u = (p.y - b.min.y) / span;
                if (u < 0.15f) { tail += r; nTail++; }
                else if (u > 0.85f) { tip += r; nTip++; }
            }
            float mean = sum / Mathf.Max(1, v.Length);
            return new HandMeshPart
            {
                name = mf.name,
                path = AnimationUtility.CalculateTransformPath(mf.transform, node),
                lengthMm = b.size.y * scale * 1000f,
                meanRadiusMm = mean * scale * 1000f,
                tailRadiusMm = (nTail > 0 ? tail / nTail : 0f) * scale * 1000f,
                tipRadiusMm = (nTip > 0 ? tip / nTip : 0f) * scale * 1000f,
                axisAlignment = Mathf.Abs(axis.y),
                slenderness = b.size.y / Mathf.Max(1e-5f, 2f * Mathf.Max(b.extents.x, b.extents.z)),
                vertices = v.Length,
            };
        }

        /// <summary>
        /// Crea (o rehace, con <paramref name="overwrite"/>) el perfil de un wieldable con los objetivos de
        /// partida. No hornea: eso es un paso aparte, para poder corregir antes.
        /// </summary>
        public static HandInteractionProfile Prepare(GameObject prefab, string kind, string profilePath, bool overwrite,
            HandInspection ins, List<string> warnings)
        {
            if (!ins.hasFirstPersonRig || string.IsNullOrEmpty(ins.modelNode))
                throw new HandInteractionException("NOT_PREPARABLE", string.Join(" ", ins.notes));

            string path = string.IsNullOrEmpty(profilePath) ? ins.suggestedProfilePath : profilePath;
            var profile = AssetDatabase.LoadAssetAtPath<HandInteractionProfile>(path);
            bool exists = profile != null;
            if (exists && !overwrite)
            {
                warnings.Add($"PROFILE_EXISTS: '{path}' ya existe; se devuelve tal cual (overwrite=true para rehacer los objetivos).");
                return profile;
            }
            if (!exists)
            {
                BackroomsEditorFolders.EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                profile = ScriptableObject.CreateInstance<HandInteractionProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }

            var k = string.IsNullOrEmpty(kind)
                ? (ins.suggestedKind == "TwoHand" ? HandInteractionKind.TwoHand : HandInteractionKind.OneHand)
                : (HandInteractionKind)Enum.Parse(typeof(HandInteractionKind), kind, ignoreCase: true);

            profile.version = HandInteractionProfile.CurrentVersion;
            profile.wieldablePrefab = prefab;
            profile.modelNodeName = ins.modelNode;
            profile.gripMeshNodeName = ins.gripMesh;
            profile.kind = k;
            if (string.IsNullOrEmpty(profile.outputFolder)) profile.outputFolder = ins.suggestedOutputFolder;

            var grip = ins.parts.First(p => p.name == ins.gripMesh);
            bool carrierRight = ins.carrierHand != "L";
            var carrier = carrierRight ? profile.rightHand : profile.leftHand;
            var other = carrierRight ? profile.leftHand : profile.rightHand;

            // La portadora: su brazo no se mueve; si ya hay un agarre horneado se conserva entero.
            carrier.role = HandRole.Keep;
            carrier.alongAxis = ins.carrierAlong;
            carrier.clockDegrees = ins.carrierClockDeg;

            if (k == HandInteractionKind.OneHand)
            {
                other.role = HandRole.Keep;
            }
            else
            {
                other.role = HandRole.Grip;
                other.fingers = HandFingerStyle.Wrap;
                other.autoSearch = true;
                other.searchClockRange = 180f;
                other.searchTiltRange = 20f;
                other.tiltDegrees = 15f;
                other.indexTowardTip = true;
                other.clockDegrees = ins.carrierClockDeg;
                other.offsetMeters = Vector3.zero;
                other.palmOffsetMeters = 0f;
                other.weight = 1f;
                // Un puño mide ~85 mm a lo ancho: la otra mano va a un puño y algo de la portadora, hacia donde
                // quepa (en una linterna, hacia la lente; si no cabe, hacia la culata).
                float step = 95f / Mathf.Max(1f, grip.lengthMm);
                float towardTip = ins.carrierAlong + step, towardTail = ins.carrierAlong - step;
                bool room = towardTip <= 0.92f || towardTail >= 0.08f;
                other.alongAxis = towardTip <= 0.92f ? towardTip : towardTail >= 0.08f ? towardTail : 0.85f;
                // Sin sitio para un puño entero, el punto del eje lo elige la medida (manos juntas, rodear, alcance).
                other.searchAlongRange = room ? 0.15f : 0.45f;
                if (!room)
                    warnings.Add($"NO_ROOM_FOR_SECOND_HAND: {grip.lengthMm:0} mm de cuerpo y la portadora en {ins.carrierAlong:0.00}: " +
                                 "no cabe un puño entero; el eje se barre entero y la validación dirá si las manos se pisan.");
                // Un rig 1P sin torso: el hombro de la mano libre suele estar muy atrás (el de la antorcha, 42 cm).
                profile.maxShoulderShiftMeters = Mathf.Max(profile.maxShoulderShiftMeters, 0.35f);
            }
            profile.lastBakeUtc = exists ? profile.lastBakeUtc : "";
            profile.notes = $"Preparado {DateTime.UtcNow:yyyy-MM-dd} por la API (categoría {ins.category}).";
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }
    }
}
#endif
