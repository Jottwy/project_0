#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    /// <summary>
    /// Capturas SIN Play de un wieldable ya posado (el patrón de <c>BackroomsCrankFlashlightShot</c>): cámara
    /// propia a RenderTexture, dos luces, warp del viewmodel APAGADO (se juzga la pose, no la proyección).
    /// Necesita dispositivo gráfico: en headless, <c>-batchmode</c> SIN <c>-nographics</c> o salen negras.
    /// </summary>
    internal static class HandInteractionCapture
    {
        private const int Width = 1280, Height = 720;

        public static List<string> Shoot(HandInteractionRig rig, string outDir, string prefix)
        {
            Directory.CreateDirectory(outDir);
            var written = new List<string>();
            foreach (var smr in rig.Instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true; // fuera de Play nadie recalcula los bounds y los brazos se culan

            var rigGo = new GameObject("[HandInteractionShot]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Shader.SetGlobalFloat("_FOVEnabled", 0f);
                Shader.SetGlobalFloat("_FOV", 55f);
                AddLight(rigGo, "Key", 1.4f, Quaternion.Euler(35f, -35f, 0f));
                AddLight(rigGo, "Fill", 0.5f, Quaternion.Euler(20f, 150f, 0f));

                var camGo = new GameObject("Cam") { hideFlags = HideFlags.HideAndDontSave };
                camGo.transform.SetParent(rigGo.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 20f;

                Vector3 eye = rig.Camera.position;
                Vector3 centre = rig.GripMesh.position;
                Vector3 fwd = rig.InstanceRoot.forward, right = rig.InstanceRoot.right, up = rig.InstanceRoot.up;

                void Shot(string name, Vector3 from, Vector3 at, float fov)
                {
                    camGo.transform.SetPositionAndRotation(from, Quaternion.LookRotation((at - from).normalized, up));
                    cam.fieldOfView = fov;
                    string path = Path.Combine(outDir, $"{prefix}_{name}.png").Replace('\\', '/');
                    Render(cam, path);
                    written.Add(path);
                }

                // El ojo del jugador: posición del hueso Camera, orientación de la raíz (+Z frente).
                camGo.transform.SetPositionAndRotation(eye, rig.InstanceRoot.rotation);
                cam.fieldOfView = 60f;
                {
                    string path = Path.Combine(outDir, $"{prefix}_eye.png").Replace('\\', '/');
                    Render(cam, path);
                    written.Add(path);
                }
                Shot("side_right", centre + right * 0.38f + up * 0.05f, centre, 40f);
                Shot("side_left", centre - right * 0.38f + up * 0.05f, centre, 40f);
                Shot("below", centre - up * 0.35f + fwd * 0.05f + right * 0.02f, centre, 45f);
                Shot("front", centre + fwd * 0.40f + up * 0.06f, centre, 40f);
            }
            finally
            {
                Object.DestroyImmediate(rigGo);
            }
            return written;
        }

        private static void AddLight(GameObject parent, string name, float intensity, Quaternion rotation)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(parent.transform, false);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            go.transform.rotation = rotation;
        }

        private static void Render(Camera cam, string path)
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
