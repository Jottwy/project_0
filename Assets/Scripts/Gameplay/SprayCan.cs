using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-068 S3 — el bote de spray, visto desde nuestro lado.
    ///
    /// Es un componente PUENTE que se cuelga del prefab del wieldable, no una edición dentro de
    /// PolymindGames: la regla del proyecto es que el código del vendor no se toca ni se guarda
    /// dentro, porque un reimport del `.unitypackage` se lo lleva en silencio.
    ///
    /// Y es la razón por la que <see cref="SprayPainter"/> nunca pregunta "¿esto es el bote de
    /// spray?". Pregunta "¿el wieldable activo trae uno de estos?", igual que <c>ReadLightOn</c>
    /// (ADR-042) nunca pregunta si el objeto es la antorcha. Cualquier item futuro que quiera
    /// pintar solo tiene que traer este componente.
    /// </summary>
    [DisallowMultipleComponent]
    public class SprayCan : MonoBehaviour
    {
        [Header("Carga")]
        [Tooltip("Metros de trazo que quedan. ADR-068 decisión 8: el bote se gasta, y marcar el " +
                 "camino cuesta recurso — igual que tapar el mural de otro.")]
        [SerializeField] private float paintMeters = 40f;

        [Tooltip("Con lo que nace un bote lleno. Solo informativo para la UI.")]
        [SerializeField] private float capacityMeters = 40f;

        [Header("Trazo")]
        [Tooltip("Índice en la paleta del cliente (0..15). El wire manda el índice, no el color.")]
        [SerializeField] private byte colorIndex = 2;

        [Tooltip("Grosor de boquilla en la retícula de 256 del lienzo.")]
        [SerializeField] private byte strokeWidth = 8;

        [Tooltip("Lado del lienzo en metros. El host lo acota a [0.1, 2.0].")]
        [SerializeField] private float canvasMeters = 1.2f;

        public float PaintMeters => paintMeters;
        public float CapacityMeters => capacityMeters;
        public float PaintFraction => capacityMeters <= 0f ? 0f : Mathf.Clamp01(paintMeters / capacityMeters);
        public bool IsEmpty => paintMeters <= 0f;

        public byte ColorIndex => colorIndex;
        public byte StrokeWidth => strokeWidth;

        /// <summary>Lado del lienzo, ya acotado a lo que el host acepta.</summary>
        public float CanvasMeters =>
            Mathf.Clamp(canvasMeters, SprayCanvas.MinCanvasMeters, SprayCanvas.MaxCanvasMeters);

        /// <summary>
        /// Gasta pintura. Devuelve lo que REALMENTE se pudo gastar, que puede ser menos de lo
        /// pedido si el bote se acaba a mitad del tramo — así el último trazo se corta donde se
        /// acabó la pintura en vez de dibujarse entero gratis o desaparecer entero.
        /// </summary>
        public float Spend(float meters)
        {
            if (meters <= 0f || paintMeters <= 0f) return 0f;
            float spent = Mathf.Min(meters, paintMeters);
            paintMeters -= spent;
            return spent;
        }

        /// <summary>Rellena el bote (recarga, o un bote nuevo recogido del suelo).</summary>
        public void Refill(float meters)
        {
            paintMeters = Mathf.Clamp(paintMeters + Mathf.Max(0f, meters), 0f, capacityMeters);
        }
    }
}
