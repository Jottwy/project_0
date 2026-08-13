using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-068 S2 — arnés para VER una pintada antes de que exista el bote de spray.
    ///
    /// El slice de render tiene que poder validarse solo: fabrica una pintada delante de la
    /// cámara y la mete por el mismo <see cref="SprayRenderer.Show"/> que usan la hidratación
    /// del chunk y el eco en vivo, así que lo que se ve en pantalla es exactamente lo que verá
    /// una pintada real. No toca el backend, no manda nada por el wire y no persiste nada: es
    /// pintura local que desaparece al salir de Play.
    /// </summary>
    public static class SprayTestPainter
    {
        private const float ChunkSize = 50f;

        [MenuItem("Backrooms/Spray/Pintar prueba delante del jugador", false, 100)]
        private static void PaintInFront()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[SprayTestPainter] Solo en Play: el renderizador arranca en runtime.");
                return;
            }

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[SprayTestPainter] No hay Camera.main — entra en una sesión primero.");
                return;
            }

            var renderer = Object.FindFirstObjectByType<SprayRenderer>();
            if (renderer == null)
            {
                Debug.LogWarning("[SprayTestPainter] No hay SprayRenderer vivo.");
                return;
            }

            // 2 m por delante, a la altura de la vista. Suficiente para caer sobre una pared en
            // un pasillo normal sin tener que buscarla con un raycast.
            Vector3 world = cam.transform.position + cam.transform.forward * 2f;
            int cx = Mathf.FloorToInt(world.x / ChunkSize);
            int cz = Mathf.FloorToInt(world.z / ChunkSize);

            var spray = new SprayMsg
            {
                // Id alto y creciente: no puede chocar con uno acuñado por el host.
                id = (uint)(0x7000_0000 + (Time.frameCount & 0xFFFF)),
                cx = cx,
                cz = cz,
                layer = 0,
                lx = world.x - cx * ChunkSize,
                ly = world.y,
                lz = world.z - cz * ChunkSize,
                // Mirando a la cámara: es la orientación que tendría una pintada de la pared
                // que el jugador tiene delante.
                yaw = cam.transform.eulerAngles.y + 180f,
                sizeX = 1.2f,
                sizeY = 1.2f,
                author = 0,
                tick = (ulong)Time.frameCount,
                strokes = SampleStrokes(),
            };

            renderer.Show(spray);
            Debug.Log($"[SprayTestPainter] Pintada fabricada id={spray.id} chunk=({cx},{cz}) " +
                      $"world=({world.x:F1},{world.y:F1},{world.z:F1}) vivas={renderer.LiveCount}");
        }

        [MenuItem("Backrooms/Spray/Borrar pintadas de prueba", false, 101)]
        private static void ClearAll()
        {
            var renderer = Object.FindFirstObjectByType<SprayRenderer>();
            if (renderer == null) return;
            renderer.Clear();
            Debug.Log("[SprayTestPainter] Pintadas locales borradas (las reales vuelven al recargar el chunk).");
        }

        /// <summary>
        /// Una flecha y un aspa: dos trazos, dos colores, y las dos formas que de verdad se van a
        /// usar para orientarse. Sirven además de comprobación visual de que un trazo continuo se
        /// une y de que el color va por téxel y no por tinte del material.
        /// </summary>
        private static SprayStrokeMsg[] SampleStrokes()
        {
            return new[]
            {
                // Flecha hacia arriba.
                new SprayStrokeMsg
                {
                    color = 4,
                    width = 10,
                    points = new byte[] { 128, 40, 128, 200, 128, 200, 80, 150, 128, 200, 176, 150 },
                },
                // Aspa cruzándola.
                new SprayStrokeMsg
                {
                    color = 2,
                    width = 8,
                    points = new byte[] { 60, 60, 196, 196, 196, 60, 60, 196 },
                },
            };
        }
    }
}
