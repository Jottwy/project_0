#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-133 bloque A — retrata la linterna EN LA MANO sin entrar en Play. Se ejecuta desde
    /// "Backrooms ▸ Linterna ▸ Capturar en la mano".
    ///
    /// POR QUÉ NO VALE EL ARNÉS DE CAPTURAS QUE YA HAY: <c>_ClaudeCaptureRunner</c> retrata el
    /// MUNDO — entra en Play, espera a que el streamer construya el chunk y coloca al jugador. Lo
    /// que hay que mirar aquí no es el mundo sino la pose de un prefab, y para eso Play es un
    /// rodeo de noventa segundos con backends de por medio.
    ///
    /// Y POR QUÉ FUNCIONA SIN PLAY, que es lo que lo hace barato: el prefab del wieldable trae
    /// dentro los brazos, su esqueleto y la malla, y en modo edición Unity NO llama a <c>Awake</c>
    /// de un MonoBehaviour normal — así que la instancia se queda quieta, activa y en pose de bind,
    /// que es exactamente el fotograma que hay que juzgar. Se planta una cámara en el origen del
    /// prefab (donde va la del jugador), se renderiza a una RenderTexture y se tira la instancia.
    ///
    /// LO QUE ESTA CAPTURA NO PUEDE DECIR, declarado para que nadie la use de más: la pose de bind
    /// no es la pose animada de "equipado en la mano". Sirve para ver si la linterna está EN el
    /// puño y con qué orientación, si la manivela cae en el costado y hacia dónde sale el haz. No
    /// sirve para juzgar el encuadre final en pantalla, que depende de la animación y del warp de
    /// FOV del viewmodel (ADR-077).
    ///
    /// No toca la escena abierta: crea la instancia, renderiza y la destruye.
    /// </summary>
    public static class BackroomsCrankFlashlightShot
    {
        private const string OutDir = "Temp/captures";
        private const int Width = 1280;
        private const int Height = 720;

        /// <summary>
        /// Los tres puntos de vista. El primero es el del jugador —cámara en el origen del prefab,
        /// mirando a +Z— y por eso su encuadre es absoluto. Los otros dos giran ALREDEDOR DE LA
        /// MANO y no del origen: el modelo cuelga de <c>Hand.R</c>, que está a medio brazo de
        /// distancia, y apuntar al origen dejaba la linterna como una mota en el centro del
        /// fotograma. Su posición es un desplazamiento sobre la mano, no un punto del mundo.
        /// </summary>
        private static readonly (string name, Vector3 offset, bool aroundHand, float fov)[] Shots =
        {
            ("linterna_mano_fps", new Vector3(0f, 0f, -0.05f), false, 60f),
            ("linterna_mano_lado", new Vector3(0.30f, 0.06f, 0.22f), true, 40f),
            ("linterna_mano_manivela", new Vector3(0.16f, -0.02f, 0.13f), true, 30f),
        };

        [MenuItem("Backrooms/Linterna/Capturar en la mano", false, 92)]
        public static void Capture()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                BackroomsCrankFlashlightCreator.PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[CrankFlashlightShot] No hay prefab en " +
                               $"'{BackroomsCrankFlashlightCreator.PrefabPath}'.");
                return;
            }

            Directory.CreateDirectory(OutDir);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            SetActiveDeep(instance);

            var rigGo = new GameObject("[FlashlightShotRig]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // Los uniforms GLOBALES del viewmodel (ADR-077): fuera de Play no los pone nadie, y
                // `_FOV` a 0 es un valor que el shader de los brazos nunca ve en juego. Se dejan
                // como los deja `CameraFOVHandler` con el warp APAGADO — lo que se juzga aquí es la
                // pose, no el encuadre deformado.
                Shader.SetGlobalFloat("_FOVEnabled", 0f);
                Shader.SetGlobalFloat("_FOV", 55f);

                // Dos luces y nada de cielo: la escena abierta puede ser cualquiera, y con su
                // iluminación la captura diría más de la escena que del objeto.
                var key = new GameObject("Key") { hideFlags = HideFlags.HideAndDontSave };
                key.transform.SetParent(rigGo.transform, false);
                var keyLight = key.AddComponent<Light>();
                keyLight.type = LightType.Directional;
                keyLight.intensity = 1.4f;
                key.transform.rotation = Quaternion.Euler(35f, -35f, 0f);

                var fill = new GameObject("Fill") { hideFlags = HideFlags.HideAndDontSave };
                fill.transform.SetParent(rigGo.transform, false);
                var fillLight = fill.AddComponent<Light>();
                fillLight.type = LightType.Directional;
                fillLight.intensity = 0.5f;
                fill.transform.rotation = Quaternion.Euler(20f, 150f, 0f);

                var camGo = new GameObject("Cam") { hideFlags = HideFlags.HideAndDontSave };
                camGo.transform.SetParent(rigGo.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 20f;

                // LA POSE DE JUEGO, NO LA DE BIND. Se muestrea el clip de idle del propio wieldable
                // sobre la instancia: es la pose en la que el jugador ve la linterna el 99 % del
                // tiempo, y la de bind no se le parece (el agarre por nudillos salió perfecto en
                // bind y torcido en juego). Con la pose puesta, la cámara del jugador es el hueso
                // `Camera` del rig, y el ángulo lente/frente se mide, no se adivina.
                // La vista del jugador: la POSICIÓN del hueso «Camera» del rig (el root está a 1,6 m
                // por debajo de las manos: es espacio de esqueleto, medido) con la ORIENTACIÓN del
                // root, cuyo +Z es el frente (con la rotación del hueso no se veían ni las manos).
                bool posed = TrySampleIdle(instance, out string clipName);
                if (!posed)
                    Debug.LogWarning("[CrankFlashlightShot] Sin clip de idle muestreable: capturas en pose de BIND.");
                Transform rigCamera = FindChild(instance, "Camera");
                var eye = new GameObject("[Eye]") { hideFlags = HideFlags.HideAndDontSave };
                eye.transform.SetParent(rigGo.transform, false);
                eye.transform.SetPositionAndRotation(
                    rigCamera != null ? rigCamera.position : instance.transform.position,
                    instance.transform.rotation);
                camGo.transform.SetPositionAndRotation(eye.transform.position, eye.transform.rotation);
                cam.fieldOfView = 60f;
                RenderTo(cam, Path.Combine(OutDir, "linterna_juego_fps.png"));
                ReportLensAgainstCamera(instance, eye.transform, posed ? clipName : "bind");

                Vector3 hand = FindHandPoint(instance);

                foreach (var shot in Shots)
                {
                    Vector3 from = shot.aroundHand ? hand + shot.offset : shot.offset;
                    Vector3 at = shot.aroundHand ? hand : shot.offset + Vector3.forward;

                    camGo.transform.position = from;
                    camGo.transform.rotation = Quaternion.LookRotation((at - from).normalized, Vector3.up);
                    cam.fieldOfView = shot.fov;

                    string path = Path.Combine(OutDir, shot.name + ".png");
                    RenderTo(cam, path);
                    Debug.Log($"[CrankFlashlightShot] '{path}' desde {from} mirando a {at}, FOV {shot.fov}.");
                }

                ShootBareModel(instance, cam, camGo.transform);

                // Equipar a mitad de recorrido, desde el ojo: la entrada de la mano en plano.
                if (TrySampleOverride(instance, "equip", 0.5f, out _))
                {
                    camGo.transform.SetPositionAndRotation(eye.transform.position, eye.transform.rotation);
                    cam.fieldOfView = 60f;
                    RenderTo(cam, Path.Combine(OutDir, "linterna_equipar_mitad.png"));
                }

                ShootCranking(instance, cam, camGo.transform, eye.transform);

                if (UnityEditor.AnimationMode.InAnimationMode()) UnityEditor.AnimationMode.StopAnimationMode();
            }
            finally
            {
                Object.DestroyImmediate(rigGo);
                Object.DestroyImmediate(instance);
            }

            Debug.Log($"[CrankFlashlightShot] {Shots.Length} capturas en '{OutDir}'.");
        }

        /// <summary>
        /// El prefab guarda apagados el tronco de antorcha y los nodos de fuego, y eso está bien en
        /// juego; pero también podría traer apagado el propio ViewModel. Se encienden sólo los
        /// nodos del camino hasta el modelo y los brazos, nunca todo: encender el fuego devolvería
        /// la llama a la foto.
        /// </summary>
        private static void SetActiveDeep(GameObject root)
        {
            if (!root.activeSelf) root.SetActive(true);

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "ViewModel" && t.name != "Root") continue;
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            }

            // LOS BRAZOS SE CULABAN, y sin brazos esta captura no contesta nada: la primera pasada
            // salió con la linterna flotando en negro. Un `SkinnedMeshRenderer` fuera de Play no
            // recalcula sus bounds —nadie lo anima—, así que la caja con la que se decide si entra
            // en cámara es la de bind y puede quedar en cualquier parte. `updateWhenOffscreen` la
            // recalcula cada fotograma y con eso aparecen.
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;
        }

        /// <summary>
        /// Dos tomas SIN BRAZOS, y alineadas al eje del cuerpo en vez de a los ejes del mundo. Las
        /// tres de arriba enseñan el agarre pero la mano tapa justo lo que queda por decidir: en
        /// qué costado cae la manivela y qué extremo del cuerpo es la lente. El perfil mira el
        /// cuerpo de lado; la otra se pone donde saldría el haz y mira hacia atrás — si ahí está la
        /// lente, la luz sale por donde debe, y si se ve la tapa del culo, el haz apunta al jugador.
        /// </summary>
        private static void ShootBareModel(GameObject instance, Camera cam, Transform camT)
        {
            Transform body = null;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != BackroomsCrankFlashlightModelApplier.BodyNodeName) continue;
                body = t;
                break;
            }

            if (body == null)
            {
                Debug.LogWarning("[CrankFlashlightShot] Sin nodo 'Body': no hay tomas del modelo desnudo.");
                return;
            }

            var arms = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in arms) smr.enabled = false;

            try
            {
                Vector3 centre = body.position;
                // El eje largo del cuerpo es su +Y local, por la malla canónica.
                Vector3 axis = body.up;
                Vector3 side = body.right;

                Shoot(cam, camT, centre + side * 0.30f, centre, 32f, "linterna_modelo_perfil");
                Shoot(cam, camT, centre + axis * 0.32f, centre, 32f, "linterna_modelo_lente");
                Shoot(cam, camT, centre - axis * 0.32f, centre, 32f, "linterna_modelo_culata");

                // La manivela a MEDIO BARRIDO, que es la única pose que contesta si el brazo pasa
                // por dentro de la carcasa: en reposo va tumbada contra ella y ahí todo eje parece
                // bueno. Se gira 90° sobre el eje que usará en juego y se devuelve.
                var crank = FindChild(instance, BackroomsCrankFlashlightModelApplier.CrankNodeName);
                if (crank != null)
                {
                    var rest = crank.localRotation;
                    crank.localRotation = rest * Quaternion.AngleAxis(90f,
                        BackroomsCrankFlashlightModelApplier.CrankSpinAxis);
                    Shoot(cam, camT, centre + side * 0.26f + axis * 0.10f, centre, 32f,
                        "linterna_modelo_manivela90");
                    crank.localRotation = rest;
                }
            }
            finally
            {
                foreach (var smr in arms) smr.enabled = true;
            }
        }

        private static Transform FindChild(GameObject root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>
        /// Pone la instancia en la pose de IDLE del wieldable muestreando su clip en modo edición.
        /// Se elige el clip del controller cuyo nombre contenga "idle"; si hay varios, el primero,
        /// y se imprimen todos para que se vea cuál fue. El tiempo de muestreo es medio clip: en un
        /// idle en bucle cualquier instante vale, y el medio esquiva el fotograma de entrada.
        /// Deja `AnimationMode` ABIERTO: quien llama lo cierra al terminar de retratar.
        /// </summary>
        private static bool TrySampleIdle(GameObject instance, out string clipName)
            => TrySampleOverride(instance, "idle", 0.5f, out clipName);

        /// <summary>
        /// Muestrea el clip QUE SE VE EN JUEGO: el override del <c>WieldableAnimator</c> cuyo
        /// original se llama como la pista, en la fracción pedida de su duración. Antes se cogía
        /// el clip del controller pelado, que es la PLANTILLA del vendor (`Template_Idle`) y no la
        /// antorcha ni, ahora, los clips horneados: retrataba una pose que nadie juega.
        /// Deja `AnimationMode` ABIERTO: quien llama lo cierra al terminar de retratar.
        /// </summary>
        private static bool TrySampleOverride(GameObject instance, string originalHint, float fraction, out string clipName)
        {
            clipName = null;
            Animator animator = null;
            foreach (var a in instance.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null) continue;
                animator = a;
                break;
            }
            if (animator == null)
            {
                Debug.LogWarning("[CrankFlashlightShot] La instancia no trae ningún Animator con controller.");
                return false;
            }

            var clip = FindOverrideClip(instance, originalHint, out string listing);
            if (clip == null)
            {
                Debug.LogWarning($"[CrankFlashlightShot] Sin override cuyo original contenga '{originalHint}'. " +
                                 $"Pares: {listing}");
                return false;
            }

            if (!UnityEditor.AnimationMode.InAnimationMode()) UnityEditor.AnimationMode.StartAnimationMode();
            UnityEditor.AnimationMode.BeginSampling();
            UnityEditor.AnimationMode.SampleAnimationClip(animator.gameObject, clip, clip.length * Mathf.Clamp01(fraction));
            UnityEditor.AnimationMode.EndSampling();

            clipName = clip.name;
            Debug.Log($"[CrankFlashlightShot] Pose muestreada: clip '{clip.name}' ({clip.length:F2} s) al " +
                      $"{fraction:P0} sobre '{animator.gameObject.name}'. Pares: {listing}");
            return true;
        }

        private static AnimationClip FindOverrideClip(GameObject instance, string originalHint, out string listing)
        {
            var sb = new System.Text.StringBuilder();
            AnimationClip found = null;
            var wieldableAnimator = instance.GetComponentInChildren<PolymindGames.WieldableSystem.WieldableAnimator>(true);
            if (wieldableAnimator != null)
            {
                var so = new SerializedObject(wieldableAnimator);
                var pairs = so.FindProperty("_clips._clips");
                for (int i = 0; pairs != null && i < pairs.arraySize; i++)
                {
                    var e = pairs.GetArrayElementAtIndex(i);
                    var original = e.FindPropertyRelative("Original").objectReferenceValue as AnimationClip;
                    var over = e.FindPropertyRelative("Override").objectReferenceValue as AnimationClip;
                    if (original == null) continue;
                    sb.Append(original.name).Append("→").Append(over != null ? over.name : "(original)").Append(", ");
                    if (found == null && original.name.ToLowerInvariant().Contains(originalHint.ToLowerInvariant()))
                        found = over != null ? over : original;
                }
            }
            listing = sb.ToString();
            return found;
        }

        /// <summary>
        /// La cuerda a cuatro fases, desde el ojo: la izquierda sobre el pomo y la manivela girada
        /// EXACTAMENTE como la pone el componente a esa fase (reposo · giro sobre su eje). Y una
        /// toma de lado a un cuarto de vuelta, que es donde se ve si el pomo va dentro de la mano
        /// o al lado.
        /// </summary>
        private static void ShootCranking(GameObject instance, Camera cam, Transform camT, Transform eye)
        {
            var crank = FindChild(instance, BackroomsCrankFlashlightModelApplier.CrankNodeName);
            var rest = crank != null ? crank.localRotation : Quaternion.identity;
            foreach (float phase in new[] { 0f, 0.25f, 0.5f, 0.75f })
            {
                if (!TrySampleOverride(instance, "crank", phase, out _))
                    return;
                if (crank != null)
                    crank.localRotation = BackroomsCrankFlashlightModelApplier.CrankLocalRotation *
                                          Quaternion.AngleAxis(phase * 360f, BackroomsCrankFlashlightModelApplier.CrankSpinAxis);

                camT.SetPositionAndRotation(eye.position, eye.rotation);
                cam.fieldOfView = 60f;
                RenderTo(cam, Path.Combine(OutDir, $"linterna_cuerda_{Mathf.RoundToInt(phase * 360f)}.png"));

                if (Mathf.Approximately(phase, 0.25f))
                {
                    Vector3 hand = FindHandPoint(instance);
                    Shoot(cam, camT, hand + new Vector3(-0.35f, 0.12f, 0.10f), hand, 40f, "linterna_cuerda_lado");
                    Shoot(cam, camT, hand + new Vector3(0.05f, 0.40f, 0.05f), hand, 40f, "linterna_cuerda_cenital");
                }
            }
            if (crank != null) crank.localRotation = rest;
        }

        /// <summary>
        /// El número que el aplicador necesita: cuánto se desvía la lente del frente de la cámara
        /// en la pose de juego, y hacia dónde. Se imprime en el espacio de la cámara del rig
        /// (x = derecha, y = arriba, z = delante), junto con dónde cae el modelo respecto a ella.
        /// </summary>
        private static void ReportLensAgainstCamera(GameObject instance, Transform rigCamera, string clipName)
        {
            var model = FindChild(instance, BackroomsCrankFlashlightModelApplier.NodeName);
            if (model == null)
            {
                Debug.LogWarning("[CrankFlashlightShot] No hay nodo del modelo que medir.");
                return;
            }

            Vector3 lens = rigCamera.InverseTransformDirection(model.up);
            Vector3 pos = rigCamera.InverseTransformPoint(model.position);
            float pitch = Mathf.Atan2(lens.y, lens.z) * Mathf.Rad2Deg;
            float yaw = Mathf.Atan2(lens.x, lens.z) * Mathf.Rad2Deg;

            Debug.Log($"[CrankFlashlightShot] En la pose '{clipName}': la lente apunta a ({lens.x:F2}, {lens.y:F2}, " +
                      $"{lens.z:F2}) en espacio de cámara → cabeceo {pitch:F1}° (positivo = hacia arriba), " +
                      $"guiñada {yaw:F1}° (positivo = hacia la derecha); ángulo total con el frente " +
                      $"{Vector3.Angle(lens, Vector3.forward):F1}°. Centro del modelo a ({pos.x:F3}, {pos.y:F3}, " +
                      $"{pos.z:F3}) de la vista (x=derecha, y=arriba, z=delante).");
        }

        private static void Shoot(Camera cam, Transform camT, Vector3 from, Vector3 at, float fov, string name)
        {
            camT.position = from;
            camT.rotation = Quaternion.LookRotation((at - from).normalized, Vector3.up);
            cam.fieldOfView = fov;

            string path = Path.Combine(OutDir, name + ".png");
            RenderTo(cam, path);
            Debug.Log($"[CrankFlashlightShot] '{path}' desde {from} mirando a {at}, FOV {fov}.");
        }

        /// <summary>
        /// El punto al que miran las cámaras de detalle: el nodo del modelo si está, y si no la
        /// mano. En espacio de MUNDO, porque el prefab se instancia en el origen.
        /// </summary>
        private static Vector3 FindHandPoint(GameObject root)
        {
            Transform hand = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == BackroomsCrankFlashlightModelApplier.NodeName) return t.position;
                if (hand == null && t.name == BackroomsCrankFlashlightCreator.HandBoneName) hand = t;
            }

            if (hand != null) return hand.position;

            Debug.LogWarning("[CrankFlashlightShot] Ni nodo del modelo ni hueso de la mano: las cámaras de " +
                             "detalle apuntarán al origen y la linterna saldrá diminuta.");
            return Vector3.zero;
        }

        private static void RenderTo(Camera cam, string path)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4
            };
            var previous = RenderTexture.active;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
#endif
