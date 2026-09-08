#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.Gameplay.Medical;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Retrata la venda puesta, en los dos sitios donde tiene que verse, SIN entrar en Play.
    ///
    /// Mismo molde que <see cref="BackroomsCrankFlashlightShot"/> y por el mismo motivo: lo que
    /// puede salir mal aquí —el eje del hueso, el tamaño, el material magenta— se ve en una imagen
    /// en segundos, y comprobarlo con una partida cuesta noventa segundos de carga, un jugador que
    /// se hiera y un segundo jugador que mire. Esto no sustituye al playtest, lo hace barato:
    /// separa "la venda está mal dibujada" de "la venda no llega a dibujarse".
    ///
    /// Se instancian los prefabs REALES —el wieldable horneado y el avatar remoto horneado— y se
    /// cuelga la banda con el MISMO <see cref="BandageVisual.Attach"/> que usan los dos hooks en
    /// juego. Lo que no cubre, y hay que decirlo: que los hooks la enciendan. Eso es estado en
    /// runtime (el wieldable activo en 1P, el bit de `buttons` en 3P) y sólo se ve jugando.
    /// </summary>
    public static class BackroomsBandageShot
    {
        /// <summary>
        /// Las capturas NO van a <c>Temp/</c>, aunque el molde de la linterna las deje ahí: Unity en
        /// modo batch **borra `Temp/` al cerrar**, así que un `-executeMethod` escribía las PNG y se
        /// las llevaba por delante al salir — exit 0, log diciendo que las había escrito, y ni un
        /// fichero en el disco. Con el editor abierto no se nota, y por eso el molde no lo sufre.
        ///
        /// `Builds/` también está en `.gitignore` (línea 7), así que esto no ensucia el repo.
        /// </summary>
        private const string OutDir = "Builds/Captures";
        private const int Width = 1280;
        private const int Height = 720;

        private const string ProxyPrefabPath =
            "Assets/_Migration/STPIntegration/Resources/RemotePlayerAvatar.prefab";

        /// <summary>
        /// Cuántos huesos con ese nombre hay en el avatar remoto, y de qué esqueleto es cada uno.
        /// La pregunta no es retórica: un proxy lleva DOS esqueletos humanoides vivos (el cuerpo del
        /// vendor y la forma real del robapieles), así que "buscar el hueso por nombre" puede
        /// devolver el del cuerpo que NO se ve.
        /// </summary>
        [MenuItem("Backrooms/Venda/Sondear los huesos del proxy", false, 97)]
        public static void ProbeProxyBones()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[VendaSonda] No hay avatar remoto en '{ProxyPrefabPath}'.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                foreach (string bone in new[] { "LowerArm.L", "LowerArm.R" })
                {
                    int n = 0;
                    foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name != bone) continue;
                        n++;
                        Debug.Log($"[VendaSonda] {bone} #{n}: {PathOf(instance.transform, t)} " +
                                  $"(activo={t.gameObject.activeInHierarchy})");
                    }
                    Debug.Log($"[VendaSonda] TOTAL '{bone}': {n}");
                }

                foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var root = smr.rootBone != null ? smr.rootBone
                             : (smr.bones != null && smr.bones.Length > 0 ? smr.bones[0] : null);
                    Debug.Log($"[VendaSonda] malla '{smr.name}' activa={smr.gameObject.activeInHierarchy} " +
                              $"enabled={smr.enabled} rootBone={(root != null ? PathOf(instance.transform, root) : "?")}");
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// MIDE el grosor real del antebrazo en los dos rigs, en vez de ajustar el radio de la venda
        /// a ojo sobre una captura. Dos intentos a ojo ya fallaron en direcciones opuestas —0,058
        /// dejaba una escayola, 0,030 se hundía dentro del brazo— porque la piel de cada rig está a
        /// una distancia distinta del hueso y ninguna de las dos se parece a un número redondo.
        ///
        /// Cómo: se cogen los vértices de la malla que MANDA ese hueso (peso dominante) y se mide su
        /// distancia al EJE del hueso, no a su origen. El percentil 90 y no el máximo: el máximo lo
        /// fija un vértice suelto del codo o de la muñeca y engorda la venda por un solo punto.
        ///
        /// SÓLO VALE PARA EL CUERPO EN TERCERA PERSONA, y hay que saberlo antes de creerse un
        /// número: los vértices se llevan a mundo con la matriz del propio <c>SkinnedMeshRenderer</c>,
        /// lo que asume que la malla está autorada en el espacio de ese renderer. En el cuerpo del
        /// vendor se cumple; en los brazos del viewmodel NO, y salen radios de medio metro. Si un
        /// resultado pasa de 20 cm, la suposición ha fallado y el número es basura, no un brazo
        /// gordo — el aviso de abajo lo dice.
        /// </summary>
        [MenuItem("Backrooms/Venda/Medir el grosor del antebrazo", false, 98)]
        public static void MeasureArmRadii()
        {
            Measure(BackroomsBandageCreator.PrefabPath, "Forearm.L", "1P");
            Measure(BackroomsBandageCreator.PrefabPath, "Forearm.R", "1P");
            Measure(ProxyPrefabPath, "LowerArm.L", "3P");
            Measure(ProxyPrefabPath, "LowerArm.R", "3P");
        }

        private static void Measure(string prefabPath, string boneName, string label)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[VendaMedida] Sin prefab en '{prefabPath}'.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var bone = Find(instance, boneName);
                if (bone == null)
                {
                    Debug.LogError($"[VendaMedida] {label} {boneName}: hueso no encontrado.");
                    return;
                }

                // El eje del hueso, igual que lo deriva BandageVisual: hacia su hijo.
                Vector3 axis = bone.childCount > 0
                    ? (bone.GetChild(0).position - bone.position).normalized
                    : bone.up;
                Vector3 origin = bone.position;

                // Donde cae el centro de la banda, con el mismo reparto que usa BandageVisual.
                float boneLength = bone.childCount > 0
                    ? Vector3.Distance(bone.position, bone.GetChild(0).position)
                    : 0.25f;
                float bandCentre = boneLength * BandageVisual.DefaultAlongBone;

                var radii = new System.Collections.Generic.List<float>();
                foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    // Sólo la piel que se ve: las prendas apagadas del armario del vendor no cuentan.
                    if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                    var mesh = smr.sharedMesh;
                    if (mesh == null || smr.bones == null) continue;

                    // El hueso Y SUS DESCENDIENTES: en los brazos de primera persona la piel del
                    // antebrazo NO la manda `Forearm.L`, la mandan sus huesos de torsión
                    // (`ForearmTwist.N.L`), que cuelgan de él. Buscando sólo el hueso exacto salían
                    // cero vértices y la medida no existía.
                    var accepted = new System.Collections.Generic.HashSet<int>();
                    for (int b = 0; b < smr.bones.Length; b++)
                    {
                        var candidate = smr.bones[b];
                        if (candidate != null && (candidate == bone || candidate.IsChildOf(bone)))
                            accepted.Add(b);
                    }
                    if (accepted.Count == 0) continue;

                    var vertices = mesh.vertices;
                    var weights = mesh.boneWeights;
                    var toWorld = smr.transform.localToWorldMatrix;

                    for (int i = 0; i < vertices.Length && i < weights.Length; i++)
                    {
                        var w = weights[i];
                        if (!accepted.Contains(w.boneIndex0) || w.weight0 < 0.5f) continue;

                        Vector3 v = toWorld.MultiplyPoint3x4(vertices[i]) - origin;

                        // Sólo la FRANJA donde la venda se pone. Sin esto manda el codo, que es lo
                        // más gordo del hueso, y sale un radio que allí no hace falta.
                        float along = Vector3.Dot(v, axis);
                        if (Mathf.Abs(along - bandCentre) > BandageVisual.DefaultLength * 0.5f) continue;

                        radii.Add(Vector3.ProjectOnPlane(v, axis).magnitude);
                    }
                }

                if (radii.Count == 0)
                {
                    Debug.LogWarning($"[VendaMedida] {label} {boneName}: ningún vértice dominado por ese hueso.");
                    return;
                }

                radii.Sort();
                float p90 = radii[Mathf.Clamp(Mathf.RoundToInt(radii.Count * 0.90f), 0, radii.Count - 1)];
                float median = radii[radii.Count / 2];
                if (p90 > 0.20f)
                {
                    Debug.LogWarning($"[VendaMedida] {label} {boneName}: p90 {p90:F4} m — eso no es un " +
                                     "antebrazo. La malla no está autorada en el espacio de su " +
                                     "renderer, así que esta medida NO vale para este rig.");
                    return;
                }

                Debug.Log($"[VendaMedida] {label} {boneName}: {radii.Count} vértices, " +
                          $"mediana {median:F4} m, p90 {p90:F4} m → radio de venda sugerido {p90 * 1.08f:F4} m.");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static string PathOf(Transform root, Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null && p != root; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }

        [MenuItem("Backrooms/Venda/Capturar la venda puesta", false, 96)]
        public static void Capture()
        {
            Directory.CreateDirectory(OutDir);
            ShootFirstPerson();
            ShootThirdPerson();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Primera persona: los brazos que trae el propio wieldable de la venda. La cámara se pone
        /// donde el hueso <c>Camera</c> del rig, que es donde está el ojo del jugador — el root del
        /// prefab queda muy por debajo de las manos.
        /// </summary>
        private static void ShootFirstPerson()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BackroomsBandageCreator.PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[VendaShot] No hay prefab en '{BackroomsBandageCreator.PrefabPath}'. " +
                               "Ejecuta antes 'Backrooms ▸ Venda ▸ Crear venda'.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var rig = BuildRig(out var cam, out var camT);
            try
            {
                SetActiveDeep(instance);

                // SOLO EL IZQUIERDO: es la regla de Alpha 1 (`PlayerMedicalState.RestrictToLeftArm`),
                // y vendar los dos aquí daría una foto de algo que en juego no pasa nunca.
                var left = Find(instance, "Forearm.L");
                var leftBand = AttachAndReport(left, "1P Forearm.L", BandageVisual.DefaultRadius);

                var eye = Find(instance, "Camera");
                Vector3 eyePos = eye != null ? eye.position : instance.transform.position;

                camT.SetPositionAndRotation(eyePos, instance.transform.rotation);
                cam.fieldOfView = 60f;
                RenderTo(cam, Path.Combine(OutDir, "venda_1p_fps.png"));

                // Detalle de cada antebrazo: la cámara gira ALREDEDOR DE LA BANDA, no del origen —
                // apuntar al origen deja la venda como una mota en el centro del fotograma.
                if (leftBand != null)
                    Shoot(cam, camT, leftBand.transform.position + new Vector3(-0.10f, 0.12f, -0.22f),
                          leftBand.transform.position, 32f, "venda_1p_detalle_izq");
            }
            finally
            {
                Object.DestroyImmediate(instance);
                Object.DestroyImmediate(rig);
            }
        }

        /// <summary>
        /// Tercera persona: el avatar remoto horneado, con los huesos que lee
        /// <c>ProxyBandageHook</c> — que NO se llaman igual que en primera persona.
        /// </summary>
        private static void ShootThirdPerson()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[VendaShot] No hay avatar remoto en '{ProxyPrefabPath}'.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var rig = BuildRig(out var cam, out var camT);
            try
            {
                // AQUÍ NO se activa todo a lo bruto, al revés que en primera persona: el proxy trae
                // el cuerpo de la forma real del robapieles APAGADO a propósito (ADR-038), y
                // encenderlo mete una segunda piel gris en el retrato que en juego nadie ve. Lo que
                // hay que fotografiar es el avatar tal y como sale del pool.
                instance.SetActive(true);

                // Sólo el izquierdo, por lo mismo que en primera persona.
                var left = Find(instance, "LowerArm.L");
                var leftBand = AttachAndReport(left, "3P LowerArm.L", BandageVisual.ProxyRadius);

                // De frente, a la altura del pecho: el encuadre en el que un jugador ve a otro.
                var head = Find(instance, "Head");
                Vector3 chest = head != null
                    ? head.position - new Vector3(0f, 0.35f, 0f)
                    : instance.transform.position + new Vector3(0f, 1.3f, 0f);

                Shoot(cam, camT, chest + new Vector3(0f, 0f, 2.2f), chest, 40f, "venda_3p_frente");
                if (leftBand != null)
                    Shoot(cam, camT, leftBand.transform.position + new Vector3(-0.25f, 0.15f, 0.45f),
                          leftBand.transform.position, 28f, "venda_3p_detalle");
            }
            finally
            {
                Object.DestroyImmediate(instance);
                Object.DestroyImmediate(rig);
            }
        }

        /// <summary>
        /// Cuelga la banda y ESCRIBE SUS MEDIDAS EN METROS DE MUNDO. Los números importan tanto
        /// como la imagen: una venda con el eje bien y el tamaño de un brazo se ve rara en la foto
        /// y se explica sola en la línea de log.
        /// </summary>
        private static GameObject AttachAndReport(Transform forearm, string label, float radius)
        {
            if (forearm == null)
            {
                Debug.LogError($"[VendaShot] {label}: hueso NO encontrado. La venda no se dibujaría ahí.");
                return null;
            }

            var band = BandageVisual.Attach(forearm, radius);
            if (band == null)
            {
                Debug.LogError($"[VendaShot] {label}: Attach devolvió null.");
                return null;
            }

            band.SetActive(true);

            var renderer = band.GetComponent<Renderer>();
            string material = renderer != null && renderer.sharedMaterial != null
                ? renderer.sharedMaterial.name + " / " + renderer.sharedMaterial.shader.name
                : "SIN MATERIAL (saldría rosa)";
            var size = renderer != null ? renderer.bounds.size : Vector3.zero;

            Debug.Log($"[VendaShot] {label}: banda en {band.transform.position}, " +
                      $"tamaño mundo {size.x:F3} × {size.y:F3} × {size.z:F3} m, material {material}.");
            return band;
        }

        private static GameObject BuildRig(out Camera cam, out Transform camT)
        {
            var rigGo = new GameObject("[VendaShotRig]") { hideFlags = HideFlags.HideAndDontSave };

            var key = new GameObject("Key") { hideFlags = HideFlags.HideAndDontSave };
            key.transform.SetParent(rigGo.transform, false);
            var keyLight = key.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.intensity = 1.2f;
            key.transform.rotation = Quaternion.Euler(35f, -35f, 0f);

            var fill = new GameObject("Fill") { hideFlags = HideFlags.HideAndDontSave };
            fill.transform.SetParent(rigGo.transform, false);
            var fillLight = fill.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.5f;
            fill.transform.rotation = Quaternion.Euler(20f, 150f, 0f);

            var camGo = new GameObject("Cam") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(rigGo.transform, false);
            cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            camT = camGo.transform;
            return rigGo;
        }

        private static void Shoot(Camera cam, Transform camT, Vector3 from, Vector3 at, float fov, string name)
        {
            camT.position = from;
            camT.rotation = Quaternion.LookRotation((at - from).normalized, Vector3.up);
            cam.fieldOfView = fov;

            string path = Path.Combine(OutDir, name + ".png");
            RenderTo(cam, path);
            Debug.Log($"[VendaShot] '{path}' desde {from} mirando a {at}, FOV {fov}.");
        }

        private static void SetActiveDeep(GameObject root)
        {
            root.SetActive(true);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.SetActive(true);
        }

        private static Transform Find(GameObject root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        private static void RenderTo(Camera cam, string path)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
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
