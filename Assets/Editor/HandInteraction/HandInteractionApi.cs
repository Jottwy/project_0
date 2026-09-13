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
    /// <summary>Una petición. Sólo se leen los campos que usa cada comando (ver <c>docs/systems/hand-interaction.md</c>).</summary>
    [Serializable]
    public sealed class HandApiRequest
    {
        public string id;
        /// <summary>find | list | inspect | prepare | get | set | nudge | preview | bake | validate | capture</summary>
        public string command;
        public string query;
        public string prefab;
        public string profile;
        public string kind;
        public bool overwrite;
        /// <summary>JSON parcial y plano con campos del perfil (JsonUtility.FromJsonOverwrite); si no cambia nada, PATCH_NO_EFFECT.</summary>
        public string patch;
        /// <summary>R | L</summary>
        public string hand;
        /// <summary>forward | back | up | down | left | right | tip | tail | in | out | clockwise | counterclockwise | tiltUp | tiltDown</summary>
        public string direction;
        public float millimeters;
        public float degrees;
        public bool capture;
        public bool bake;
        /// <summary>Hornea aunque el veredicto sea NO_NATURAL_GRIP.</summary>
        public bool force;
        public string captureDir;
    }

    [Serializable]
    public sealed class HandApiError
    {
        public string code;
        public string message;
        public string hint;
    }

    [Serializable]
    public sealed class HandProfileSummary
    {
        public string path;
        public string prefab;
        public string modelNode;
        public string gripMesh;
        public string kind;
        public HandGripTarget right;
        public HandGripTarget left;
        public string outputFolder;
        public List<string> baseClips = new();
        public List<string> bakedClips = new();
        public string lastBakeUtc;
        public float maxShoulderShiftMeters;
    }

    [Serializable]
    public sealed class HandApiResponse
    {
        public string id;
        public string command;
        public bool ok;
        public List<HandApiError> errors = new();
        public List<string> warnings = new();
        public List<HandWieldableCandidate> candidates = new();
        public HandInspection inspection;
        public HandProfileSummary profile;
        public HandBakeResult bake;
        public HandValidationReport validation;
        public List<string> captures = new();
        public string changed;
    }

    [Serializable]
    public sealed class HandApiBatch
    {
        public List<HandApiRequest> requests = new();
    }

    [Serializable]
    public sealed class HandApiBatchResponse
    {
        public List<HandApiResponse> responses = new();
    }

    /// <summary>
    /// LA API DE AUTOMATIZACIÓN de la herramienta de interacción mano-objeto. Todo lo que hace la ventana pasa
    /// por aquí, así que lo que puede hacer un humano lo puede hacer Claude con el mismo resultado. Tres
    /// entradas y una sola implementación:
    ///   · <see cref="Execute(string)"/>: JSON → JSON, para quien ya esté dentro del editor (MCP, ventana, tests).
    ///   · <see cref="RunCli"/>: <c>-executeMethod</c> con el editor cerrado (<c>-hiRequest</c> / <c>-hiResponse</c>).
    ///   · <see cref="HandInteractionBridge"/>: con el editor ABIERTO, ficheros en <c>Logs/HandInteraction/inbox</c>.
    /// Los errores nunca son excepciones hacia fuera: salen con código estable y una pista de arreglo.
    /// </summary>
    public static class HandInteractionApi
    {
        public const string DefaultCaptureRoot = "Logs/HandInteraction/captures";

        public static string Execute(string requestJson)
        {
            // Un lote es un objeto cuya PRIMERA clave es "requests". Buscar el texto en cualquier sitio convertía en
            // lote una petición con "requests" dentro de su query o su patch, y la perdía en silencio.
            if (System.Text.RegularExpressions.Regex.IsMatch(requestJson ?? "", "^\\s*\\{\\s*\"requests\"\\s*:"))
            {
                var responses = new HandApiBatchResponse();
                try
                {
                    var batch = JsonUtility.FromJson<HandApiBatch>(requestJson);
                    if (batch?.requests == null || batch.requests.Count == 0)
                        throw new HandInteractionException("BAD_BATCH", "lote sin peticiones");
                    foreach (var r in batch.requests) responses.responses.Add(Execute(r));
                }
                catch (Exception e)
                {
                    var bad = new HandApiResponse { ok = false };
                    bad.errors.Add(new HandApiError { code = e is HandInteractionException h ? h.Code : "BAD_BATCH", message = e.Message });
                    responses.responses.Add(bad);
                }
                return JsonUtility.ToJson(responses, false);
            }
            HandApiRequest request;
            try { request = JsonUtility.FromJson<HandApiRequest>(requestJson); }
            catch (Exception e)
            {
                var bad = new HandApiResponse { ok = false };
                bad.errors.Add(new HandApiError { code = "BAD_JSON", message = e.Message });
                return JsonUtility.ToJson(bad, true);
            }
            // Compacto: lo lee una máquina, y JsonUtility escribe hasta los bloques vacíos.
            return JsonUtility.ToJson(Execute(request), false);
        }

        public static HandApiResponse Execute(HandApiRequest req)
        {
            var res = new HandApiResponse { id = req.id, command = req.command, ok = true };
            try
            {
                switch ((req.command ?? "").ToLowerInvariant())
                {
                    case "find": res.candidates = HandInteractionAnalyzer.Find(req.query); break;
                    case "list":
                        foreach (var p in HandInteractionAnalyzer.AllProfiles())
                            res.candidates.Add(new HandWieldableCandidate
                            {
                                prefabPath = p.wieldablePrefab != null ? AssetDatabase.GetAssetPath(p.wieldablePrefab) : "",
                                profilePath = AssetDatabase.GetAssetPath(p),
                            });
                        break;
                    case "inspect": res.inspection = HandInteractionAnalyzer.Inspect(ResolvePrefab(req)); break;
                    case "prepare":
                    {
                        var prefab = ResolvePrefab(req);
                        res.inspection = HandInteractionAnalyzer.Inspect(prefab);
                        var profile = HandInteractionAnalyzer.Prepare(prefab, req.kind, req.profile, req.overwrite, res.inspection, res.warnings);
                        res.profile = Summary(profile);
                        break;
                    }
                    case "get": res.profile = Summary(ResolveProfile(req)); break;
                    case "set":
                    {
                        var profile = ResolveProfile(req);
                        if (string.IsNullOrEmpty(req.patch)) throw new HandInteractionException("PATCH_EMPTY", "'set' necesita 'patch'");
                        Undo.RecordObject(profile, "Hand interaction patch");
                        // JsonUtility y NO EditorJsonUtility: éste espera el JSON envuelto en {"MonoBehaviour":{…}} e
                        // ignora EN SILENCIO un parche plano (medido: el perfil no cambiaba y 'set' decía ok).
                        string before = JsonUtility.ToJson(profile);
                        JsonUtility.FromJsonOverwrite(req.patch, profile);
                        if (JsonUtility.ToJson(profile) == before)
                            throw new HandInteractionException("PATCH_NO_EFFECT",
                                $"el parche no cambió nada del perfil: ¿nombres de campo mal escritos? {req.patch}");
                        EditorUtility.SetDirty(profile);
                        AssetDatabase.SaveAssets();
                        res.changed = req.patch;
                        res.profile = Summary(profile);
                        AfterEdit(req, res, profile);
                        break;
                    }
                    case "nudge":
                    {
                        var profile = ResolveProfile(req);
                        res.changed = Nudge(profile, req);
                        res.profile = Summary(profile);
                        AfterEdit(req, res, profile);
                        break;
                    }
                    case "preview":
                    {
                        var profile = ResolveProfile(req);
                        res.bake = HandInteractionBaker.Bake(profile, write: false, ctx =>
                        {
                            if (req.capture) res.captures = HandInteractionCapture.Shoot(ctx.Rig, CaptureDir(req, profile), "preview");
                        });
                        // Lo resuelto se guarda (sin clips): es lo que necesita 'nudge' para traducir «atrás».
                        EditorUtility.SetDirty(profile);
                        AssetDatabase.SaveAssets();
                        break;
                    }
                    case "bake":
                    {
                        var profile = ResolveProfile(req);
                        res.bake = HandInteractionBaker.Bake(profile, write: true, force: req.force);
                        res.validation = HandInteractionValidator.Validate(profile);
                        if (req.capture) res.captures = CaptureBaked(profile, req);
                        res.ok = res.validation.ok;
                        break;
                    }
                    case "validate":
                    {
                        var profile = ResolveProfile(req);
                        res.validation = HandInteractionValidator.Validate(profile);
                        res.ok = res.validation.ok;
                        break;
                    }
                    case "capture": res.captures = CaptureBaked(ResolveProfile(req), req); break;
                    case "pose": res.changed = PoseDump(ResolveProfile(req)); break;
                    default:
                        throw new HandInteractionException("UNKNOWN_COMMAND",
                            $"comando '{req.command}' desconocido (find, list, inspect, prepare, get, set, nudge, preview, bake, validate, capture)");
                }
            }
            catch (HandInteractionException e)
            {
                res.ok = false;
                res.errors.Add(new HandApiError { code = e.Code, message = e.Message, hint = HintFor(e.Code) });
            }
            catch (Exception e)
            {
                res.ok = false;
                res.errors.Add(new HandApiError { code = "EXCEPTION", message = e.ToString() });
            }
            finally
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            }
            return res;
        }

        /// <summary>'set' y 'nudge' pueden encadenar hornear + validar (+ capturar) en la misma llamada.</summary>
        private static void AfterEdit(HandApiRequest req, HandApiResponse res, HandInteractionProfile profile)
        {
            if (!req.bake) return;
            res.bake = HandInteractionBaker.Bake(profile, write: true, force: req.force);
            res.validation = HandInteractionValidator.Validate(profile);
            res.ok = res.validation.ok;
            if (req.capture) res.captures = CaptureBaked(profile, req);
            res.profile = Summary(profile);
        }

        private static string HintFor(string code) => code switch
        {
            "PREFAB_NOT_FOUND" => "usar 'find' con el nombre del objeto y pasar la ruta exacta en 'prefab'",
            "PROFILE_NOT_FOUND" => "lanzar 'prepare' con el prefab, o 'list' para ver los perfiles existentes",
            "RIG_NOT_FOUND" => "sólo los wieldables con brazos de primera persona (ViewModel/Hand.R/Hand.L) se pueden preparar",
            "NEED_PREVIEW" => "lanzar 'preview' o 'bake' una vez: 'nudge' necesita saber cómo está orientado el objeto en la vista",
            "ROLE_KIND_MISMATCH" => "set patch {\"kind\":1} para dos manos, o poner la mano en Keep/Relaxed",
            _ => "",
        };

        private static GameObject ResolvePrefab(HandApiRequest req)
        {
            if (!string.IsNullOrEmpty(req.prefab))
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(req.prefab);
                if (p != null) return p;
                var found = HandInteractionAnalyzer.Find(req.prefab);
                if (found.Count > 0 && found[0].score > 0)
                    return AssetDatabase.LoadAssetAtPath<GameObject>(found[0].prefabPath);
            }
            if (!string.IsNullOrEmpty(req.profile))
            {
                var prof = AssetDatabase.LoadAssetAtPath<HandInteractionProfile>(req.profile);
                if (prof != null && prof.wieldablePrefab != null) return prof.wieldablePrefab;
            }
            throw new HandInteractionException("PREFAB_NOT_FOUND", $"no hay prefab '{req.prefab}'");
        }

        private static HandInteractionProfile ResolveProfile(HandApiRequest req)
        {
            if (!string.IsNullOrEmpty(req.profile))
            {
                var p = AssetDatabase.LoadAssetAtPath<HandInteractionProfile>(req.profile);
                if (p != null) return p;
                throw new HandInteractionException("PROFILE_NOT_FOUND", $"no hay perfil en '{req.profile}'");
            }
            if (!string.IsNullOrEmpty(req.prefab))
            {
                var prefab = ResolvePrefab(req);
                var p = HandInteractionAnalyzer.AllProfiles().FirstOrDefault(x => x.wieldablePrefab == prefab);
                if (p != null) return p;
            }
            throw new HandInteractionException("PROFILE_NOT_FOUND", "ni 'profile' ni un 'prefab' con perfil");
        }

        public static HandProfileSummary Summary(HandInteractionProfile p) => new()
        {
            path = AssetDatabase.GetAssetPath(p),
            prefab = p.wieldablePrefab != null ? AssetDatabase.GetAssetPath(p.wieldablePrefab) : "",
            modelNode = p.modelNodeName,
            gripMesh = p.gripMeshNodeName,
            kind = p.kind.ToString(),
            right = p.rightHand,
            left = p.leftHand,
            outputFolder = p.outputFolder,
            baseClips = (p.baseClips ?? Array.Empty<AnimationClip>()).Select(c => c != null ? AssetDatabase.GetAssetPath(c) : "(falta)").ToList(),
            bakedClips = (p.bakedClips ?? Array.Empty<AnimationClip>()).Select(c => c != null ? AssetDatabase.GetAssetPath(c) : "(falta)").ToList(),
            lastBakeUtc = p.lastBakeUtc,
            maxShoulderShiftMeters = p.maxShoulderShiftMeters,
        };

        /// <summary>
        /// El esqueleto de las dos manos en el idle efectivo (t = 0), en espacio del jugador: posiciones de hombro,
        /// codo, muñeca y nudillos, ejes del antebrazo y de la mano y la rotación local de la mano. Es para MEDIR
        /// cuando una métrica sale absurda, en vez de suponer por qué.
        /// </summary>
        private static string PoseDump(HandInteractionProfile profile)
        {
            using var ctx = HandInteractionBaker.Open(profile);
            var rig = ctx.Rig;
            ctx.IdleEffective.SampleAnimation(rig.Animator.gameObject, 0f);
            rig.Animator.localPosition = Vector3.zero;
            rig.Animator.localRotation = Quaternion.identity;
            var sb = new System.Text.StringBuilder();
            string V(Vector3 v) => $"({v.x:0.000},{v.y:0.000},{v.z:0.000})";
            sb.AppendLine($"idle '{ctx.IdleEffective.name}', portadora {(ctx.Carrier != null ? ctx.Carrier.Suffix : "-")}, ojo {V(rig.Camera.position)}");
            foreach (var side in new[] { rig.R, rig.L })
            {
                Vector3 s = side.Upper.position, e = side.Fore.position, w = side.Hand.position, k = side.Fingers[2][0].position;
                sb.AppendLine($"{side.Suffix}: hombro {V(s)} codo {V(e)} muñeca {V(w)} nudillo-corazón {V(k)}");
                sb.AppendLine($"   |hombro-codo| {(e - s).magnitude:0.000} |codo-muñeca| {(w - e).magnitude:0.000} |muñeca-nudillo| {(k - w).magnitude:0.000}; " +
                              $"ángulo antebrazo/metacarpo {Vector3.Angle(w - e, k - w):0}°; ángulo Fore.up/(muñeca-codo) {Vector3.Angle(side.Fore.up, w - e):0}°; " +
                              $"ángulo Hand.up/(nudillo-muñeca) {Vector3.Angle(side.Hand.up, k - w):0}°");
                sb.AppendLine($"   Hand local euler {V(side.Hand.localEulerAngles)}, padre de Hand '{side.Hand.parent.name}', padre de Fore '{side.Fore.parent.name}'; " +
                              $"palma local {V(side.PalmNormalLocal)} nudillos local {V(side.KnuckleLineLocal)} hueco local {V(side.EnclosedLocal)}");
            }
            return sb.ToString();
        }

        private static string CaptureDir(HandApiRequest req, HandInteractionProfile profile)
            => string.IsNullOrEmpty(req.captureDir) ? $"{DefaultCaptureRoot}/{profile.name}" : req.captureDir;

        private static List<string> CaptureBaked(HandInteractionProfile profile, HandApiRequest req)
        {
            using var ctx = HandInteractionBaker.Open(profile);
            ctx.IdleEffective.SampleAnimation(ctx.Rig.Animator.gameObject, 0f);
            ctx.Rig.Animator.localPosition = Vector3.zero;
            ctx.Rig.Animator.localRotation = Quaternion.identity;
            return HandInteractionCapture.Shoot(ctx.Rig, CaptureDir(req, profile), "baked");
        }

        /// <summary>
        /// «La mano izquierda está demasiado atrás» → <c>nudge hand=L direction=forward millimeters=20</c>. Las
        /// direcciones de VISTA (forward/back/up/down/left/right) se traducen al espacio del objeto con la
        /// orientación resuelta en el último horneado: la parte a lo largo del eje mueve <c>alongAxis</c>, la
        /// perpendicular va a <c>offsetMeters</c>. Los giros fijan el reloj o la inclinación y apagan su barrido,
        /// para que el siguiente horneado respete la corrección en vez de volver a elegir.
        /// </summary>
        private static string Nudge(HandInteractionProfile profile, HandApiRequest req)
        {
            bool right = string.Equals(req.hand, "R", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(req.hand, "right", StringComparison.OrdinalIgnoreCase);
            if (!right && !(string.Equals(req.hand, "L", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(req.hand, "left", StringComparison.OrdinalIgnoreCase)))
                throw new HandInteractionException("BAD_HAND", "hand tiene que ser R o L");
            var t = profile.Hand(right);
            var before = JsonUtility.ToJson(t);
            Undo.RecordObject(profile, "Hand interaction nudge");
            string dir = (req.direction ?? "").ToLowerInvariant();
            float meters = req.millimeters / 1000f;

            float Length()
            {
                using var ctx = HandInteractionBaker.Open(profile);
                return ctx.Rig.LengthMeters;
            }

            Vector3? view = dir switch
            {
                "forward" => Vector3.forward, "back" => Vector3.back, "up" => Vector3.up, "down" => Vector3.down,
                "left" => Vector3.left, "right" => Vector3.right, _ => null,
            };
            // Moverse a lo largo del eje parte de donde la búsqueda DEJÓ la mano y fija el barrido del eje: si no,
            // el siguiente horneado volvería a elegir y la corrección se perdería.
            float AlongFrom() => t.resolved ? t.resolvedAlongAxis : t.alongAxis;
            if (view.HasValue)
            {
                if (!profile.hasResolvedView)
                    throw new HandInteractionException("NEED_PREVIEW", "el perfil no tiene orientación resuelta");
                Vector3 local = Quaternion.Inverse(profile.resolvedObjectRotationInView) * view.Value * meters;
                float length = Length();
                t.alongAxis = Mathf.Clamp01(AlongFrom() + local.y / Mathf.Max(0.01f, length));
                t.searchAlongRange = 0f;
                t.offsetMeters += new Vector3(local.x, 0f, local.z);
            }
            else switch (dir)
            {
                case "tip": t.alongAxis = Mathf.Clamp01(AlongFrom() + meters / Mathf.Max(0.01f, Length())); t.searchAlongRange = 0f; break;
                case "tail": t.alongAxis = Mathf.Clamp01(AlongFrom() - meters / Mathf.Max(0.01f, Length())); t.searchAlongRange = 0f; break;
                case "out": t.palmOffsetMeters += meters; break;
                case "in": t.palmOffsetMeters -= meters; break;
                case "clockwise":
                case "counterclockwise":
                    t.clockDegrees = (t.resolved ? t.resolvedClockDegrees : t.clockDegrees) + (dir == "clockwise" ? req.degrees : -req.degrees);
                    t.searchClockRange = 0f;
                    if (t.resolved) t.tiltDegrees = t.resolvedTiltDegrees;
                    break;
                case "tiltup":
                case "tiltdown":
                    t.tiltDegrees = (t.resolved ? t.resolvedTiltDegrees : t.tiltDegrees) + (dir == "tiltup" ? req.degrees : -req.degrees);
                    t.searchTiltRange = 0f;
                    if (t.resolved) t.clockDegrees = t.resolvedClockDegrees;
                    break;
                default:
                    throw new HandInteractionException("BAD_DIRECTION",
                        "direction: forward|back|up|down|left|right (vista), tip|tail (eje), in|out (palma), clockwise|counterclockwise|tiltUp|tiltDown (grados)");
            }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return $"mano {(right ? "R" : "L")}: {before} → {JsonUtility.ToJson(t)}";
        }

        // ─── Línea de comandos (editor cerrado) ──────────────────────────────────────────────────

        /// <summary>
        /// <c>Unity.exe -batchmode -projectPath . -executeMethod BackroomsSurvival.EditorTools.HandInteraction.HandInteractionApi.RunCli
        /// -hiRequest Logs/HandInteraction/req.json -hiResponse Logs/HandInteraction/res.json</c> (sin <c>-quit</c>:
        /// sale solo con código 0 si todo fue ok). Para capturas, SIN <c>-nographics</c>.
        /// </summary>
        public static void RunCli()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            string requestPath = Arg("-hiRequest"), responsePath = Arg("-hiResponse") ?? "Logs/HandInteraction/response.json";
            int code = 1;
            try
            {
                string json = requestPath != null && File.Exists(requestPath) ? File.ReadAllText(requestPath) : "{\"command\":\"\"}";
                string output = Execute(json);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(responsePath)) ?? ".");
                File.WriteAllText(responsePath, output);
                code = AllOk(output) ? 0 : 2;
            }
            catch (Exception e)
            {
                File.WriteAllText(responsePath, "{\"ok\":false,\"errors\":[{\"code\":\"CLI\",\"message\":" + JsonString(e.ToString()) + "}]}");
            }
            EditorApplication.Exit(code);
        }

        /// <summary>El `ok` de NIVEL SUPERIOR, deserializado: el texto lleva también bloques vacíos con "ok":false.</summary>
        public static bool AllOk(string output)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(output ?? "", "^\\s*\\{\\s*\"responses\"\\s*:"))
            {
                var batch = JsonUtility.FromJson<HandApiBatchResponse>(output);
                return batch?.responses != null && batch.responses.Count > 0 && batch.responses.All(r => r.ok);
            }
            var single = JsonUtility.FromJson<HandApiResponse>(output);
            return single != null && single.ok;
        }

        private static string JsonString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    /// <summary>
    /// Puente con el editor ABIERTO: cada <c>Logs/HandInteraction/inbox/&lt;id&gt;.json</c> se ejecuta y su respuesta
    /// sale en <c>Logs/HandInteraction/outbox/&lt;id&gt;.json</c>. Un fichero por petición y con nombre propio: dos
    /// sesiones sobre el mismo editor no se pisan (el runner de un único fichero sí lo hacía).
    /// No actúa compilando, importando ni en Play.
    /// </summary>
    [InitializeOnLoad]
    public static class HandInteractionBridge
    {
        public const string Inbox = "Logs/HandInteraction/inbox";
        public const string Outbox = "Logs/HandInteraction/outbox";
        private static double _next;

        static HandInteractionBridge()
        {
            if (Application.isBatchMode) return;
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 0.5;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!Directory.Exists(Inbox)) return;

            var file = Directory.GetFiles(Inbox, "*.json").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (file == null) return;
            string json;
            try { json = File.ReadAllText(file); File.Delete(file); }
            catch (IOException) { return; } // aún escribiéndose

            Directory.CreateDirectory(Outbox);
            // La petición ya se borró del inbox: pase lo que pase, tiene que salir una respuesta.
            string output;
            try { output = HandInteractionApi.Execute(json); }
            catch (Exception e)
            {
                output = "{\"ok\":false,\"errors\":[{\"code\":\"BRIDGE\",\"message\":\"" +
                         e.Message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"}]}";
            }
            string outPath = Path.Combine(Outbox, Path.GetFileName(file));
            File.WriteAllText(outPath + ".tmp", output);
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(outPath + ".tmp", outPath);
        }
    }
}
#endif
