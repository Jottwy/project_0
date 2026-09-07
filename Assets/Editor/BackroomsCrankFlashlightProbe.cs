#if UNITY_EDITOR
using System.Text;
using BackroomsSurvival.Gameplay;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-133 — sonda de Play: dónde caen la mano, la linterna y la cámara MIENTRAS está
    /// equipada. Se ejecuta desde "Backrooms ▸ Linterna ▸ Sonda: dónde está la linterna" con el
    /// juego en Play y la linterna en la mano; imprime números, no arregla nada.
    ///
    /// Existe porque la captura en pose de bind (`BackroomsCrankFlashlightShot`) daba por bueno un
    /// agarre que en juego salía a la izquierda, alto y apuntando fuera: la animación de equipar
    /// no es la pose de bind, y lo único que contesta «¿dónde está de verdad?» es medirlo con la
    /// animación corriendo. Los números salen en espacio de la CÁMARA (derecha/arriba/delante),
    /// que es el que se puede comparar a ojo con una captura de pantalla.
    /// </summary>
    public static class BackroomsCrankFlashlightProbe
    {
        // Sin acentos en la ruta del menú A PROPÓSITO: el runner de ficheros de sesión lee la orden
        // en ASCII y «dónde está» llegaba como «d?nde est?» → MENU NOT FOUND.
        [MenuItem("Backrooms/Linterna/Sonda: donde esta la linterna", false, 96)]
        public static void Probe()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[CrankFlashlightProbe] Solo en Play, con la linterna equipada.");
                return;
            }

            CrankFlashlightWieldable flashlight = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<CrankFlashlightWieldable>())
            {
                if (!f.gameObject.activeInHierarchy) continue;
                flashlight = f;
                break;
            }
            if (flashlight == null)
            {
                Debug.LogWarning("[CrankFlashlightProbe] No hay ninguna linterna ACTIVA en escena: equípala primero.");
                return;
            }

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[CrankFlashlightProbe] Sin Camera.main.");
                return;
            }

            Transform hand = null, model = null, crank = null, beam = null;
            foreach (var t in flashlight.GetComponentsInChildren<Transform>(true))
            {
                switch (t.name)
                {
                    case "Hand.R": hand ??= t; break;
                    case BackroomsCrankFlashlightModelApplier.NodeName: model = t; break;
                    case BackroomsCrankFlashlightModelApplier.CrankNodeName: crank = t; break;
                    case BackroomsCrankFlashlightCreator.BeamNodeName: beam = t; break;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[CrankFlashlightProbe] wieldable '{flashlight.name}' bajo '{flashlight.transform.parent?.name}'; " +
                          $"escala del root {flashlight.transform.lossyScale}");
            Describe(sb, "cámara", cam.transform, cam);
            Describe(sb, "Hand.R", hand, cam);
            Describe(sb, "modelo", model, cam);
            Describe(sb, "manivela", crank, cam);
            Describe(sb, "haz", beam, cam);
            if (model != null)
            {
                // Hacia dónde apunta el cuerpo (+Y local = lente) comparado con la cámara.
                var lens = model.up;
                sb.AppendLine($"  lente apunta: {Format(cam.transform.InverseTransformDirection(lens))} en cámara " +
                              $"(delante = z≈1); ángulo con el frente {Vector3.Angle(lens, cam.transform.forward):F0}°");
                sb.AppendLine($"  modelo local: pos {Format(model.localPosition)} euler {Format(model.localEulerAngles)} " +
                              $"escala {Format(model.localScale)}");
            }
            Debug.Log(sb.ToString());
        }

        private static void Describe(StringBuilder sb, string label, Transform t, Camera cam)
        {
            if (t == null)
            {
                sb.AppendLine($"  {label}: (no está)");
                return;
            }
            var local = cam.transform.InverseTransformPoint(t.position);
            sb.AppendLine($"  {label}: mundo {Format(t.position)} · en cámara {Format(local)} " +
                          $"(x=derecha, y=arriba, z=delante) · activo={t.gameObject.activeInHierarchy}");
        }

        private static string Format(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }
}
#endif
